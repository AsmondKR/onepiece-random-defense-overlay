using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace OrandOverlay;

/// <summary>
/// 유닛 풀 루트를 런타임 구조로 찾는 읽기 전용 스캐너.
///
/// 고정 RVA나 바이트 시그니처 대신 두 가지 사실만 신뢰한다.
///   (1) 유닛 객체는 MSVC RTTI 클래스명(기본 .?AVCUnit@@)을 가진 vftable을 헤드에 둔다.
///   (2) 풀 루트는 countOffset에 개수, entriesPointerOffset에 객체 포인터 배열 주소를 둔다.
/// 두 조건을 동시에 만족하는 구조체가 정확히 하나일 때만 주소를 돌려주고,
/// 0개거나 2개 이상이면 예외로 fail-closed 시킨다. 패치로 오프셋이 이동해도 자가 복구된다.
/// </summary>
internal static class StructuralUnitPoolScanner
{
    private const int SampleEntries = 48;
    private const int MaximumCandidates = 4;
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(5);
    private static readonly object Gate = new();
    private static DateTime _lastFailureUtc = DateTime.MinValue;
    private static string _lastFailure = "";

    public static ulong Resolve(ReadOnlyProcessMemory memory, ProcessModule module, MemoryProfile profile,
        CancellationToken token)
    {
        // 전체 메모리 스캔은 비싸다. 직전 실패 직후에는 같은 사유로 즉시 되돌려 매 틱 재스캔을 막는다.
        lock (Gate)
            if (DateTime.UtcNow - _lastFailureUtc < FailureCooldown)
                throw new InvalidOperationException(_lastFailure);
        try
        {
            return ResolveCore(memory, module, profile, token);
        }
        catch (InvalidOperationException exception)
        {
            lock (Gate)
            {
                _lastFailureUtc = DateTime.UtcNow;
                _lastFailure = exception.Message;
            }
            throw;
        }
    }

    private static ulong ResolveCore(ReadOnlyProcessMemory memory, ProcessModule module, MemoryProfile profile,
        CancellationToken token)
    {
        var moduleBase = (ulong)module.BaseAddress.ToInt64();
        var moduleSize = module.ModuleMemorySize;
        if (moduleSize <= 0 || moduleSize > 512 * 1024 * 1024)
            throw new InvalidDataException($"비정상 모듈 크기: {moduleSize}");

        var image = ReadImage(memory, moduleBase, moduleSize);
        if (image.Length < 0x1000) throw new InvalidDataException("모듈 이미지를 읽지 못했습니다.");
        var unitVftables = FindClassVftables(image, moduleBase, profile.UnitClassName);
        if (unitVftables.Count == 0)
            throw new InvalidOperationException($"{profile.UnitClassName} vftable을 찾지 못했습니다.");

        // 같은 유닛 배열을 가리키는 구조체는 여럿일 수 있다(래퍼·사본). 모호함의 기준은
        // "서로 다른 배열이 후보로 잡혔는가"이지 "구조체가 몇 개인가"가 아니다.
        var candidates = new List<PoolCandidate>();
        foreach (var (chunkBase, buffer) in memory.ReadChunks(memory.ReadableRegions()))
        {
            token.ThrowIfCancellationRequested();
            var limit = buffer.Length - profile.EntriesPointerOffset - sizeof(ulong);
            for (var offset = 0; offset <= limit; offset += 8)
            {
                var count = BitConverter.ToInt32(buffer, offset + profile.CountOffset);
                if (count < profile.MinimumUnitObjects || count > profile.MaximumUnits) continue;
                var entries = BitConverter.ToUInt64(buffer, offset + profile.EntriesPointerOffset);
                if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(entries)) continue;
                var (units, owners) = InspectPool(memory, entries, count, profile, unitVftables);
                if (units < profile.MinimumUnitObjects) continue;
                candidates.Add(new PoolCandidate(chunkBase + (ulong)offset, entries, count, units, owners));
            }
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException("유닛 풀 구조를 찾지 못했습니다(대전 중이 아닐 수 있습니다).");

        // 게임에는 구역별·소유자별 유닛 목록이 여럿 있다. 우리가 원하는 것은 모든 소유자의 유닛이
        // 함께 담기는 전역 풀이므로, 소유자 종류가 가장 많고 유닛이 가장 많은 배열을 고른다.
        var ranked = candidates
            .GroupBy(x => x.Entries)
            .Select(group => group.OrderByDescending(x => x.UnitObjects).ThenByDescending(x => x.Count).First())
            .OrderByDescending(x => x.Owners)
            .ThenByDescending(x => x.UnitObjects)
            .ToList();
        var best = ranked[0];
        if (ranked.Count > 1)
        {
            var runnerUp = ranked[1];
            if (best.Owners == runnerUp.Owners && best.UnitObjects == runnerUp.UnitObjects)
                throw new InvalidOperationException(
                    $"구분되지 않는 유닛 배열이 {ranked.Count}개여서 프로필을 차단했습니다.");
        }

        // 개수 필드가 배열 유효 길이를 넘는 사본은 배제한다.
        var prefix = ValidPrefixLength(memory, best.Entries, profile);
        var usable = candidates
            .Where(x => x.Entries == best.Entries && x.Count <= prefix)
            .OrderByDescending(x => x.UnitObjects).ThenByDescending(x => x.Count).ToList();
        if (usable.Count == 0)
            throw new InvalidOperationException($"유닛 배열 유효 길이({prefix})와 맞는 개수 필드를 찾지 못했습니다.");
        return usable[0].Address;
    }

    /// <summary>프로필에 앵커가 있으면 로컬 플레이어 슬롯을 실측한다. 실패하면 null.</summary>
    public static byte? TryReadLocalPlayerSlot(ReadOnlyProcessMemory memory, ulong moduleBase, MemoryProfile profile)
    {
        if (!profile.HasLocalPlayerAnchor) return null;
        try
        {
            var mask = Convert.ToUInt64(profile.LocalPlayerRootXorHex, 16);
            var root = memory.ReadUInt64(AddressMath.Add(moduleBase, profile.LocalPlayerRootOffsetA)) ^ mask ^
                       memory.ReadUInt64(AddressMath.Add(moduleBase, profile.LocalPlayerRootOffsetB));
            if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(root)) return null;
            var slot = BitConverter.ToUInt16(memory.Read(AddressMath.Add(root, profile.LocalPlayerIdOffset), 2));
            return slot <= 27 ? (byte)slot : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// 모듈 이미지를 조각내어 한 버퍼로 읽는다(단일 읽기 상한보다 이미지가 클 수 있다).
    /// 못 읽은 페이지는 0으로 남고 RVA 대응은 그대로 유지된다.
    /// </summary>
    private static byte[] ReadImage(ReadOnlyProcessMemory memory, ulong moduleBase, int moduleSize)
    {
        const int chunk = 16 * 1024 * 1024;
        var image = new byte[moduleSize];
        var read = 0;
        for (var position = 0; position < moduleSize; position += chunk)
        {
            var length = Math.Min(chunk, moduleSize - position);
            var part = memory.ReadAvailable(moduleBase + (ulong)position, length);
            if (part.Length == 0) continue;
            part.CopyTo(image, position);
            read += part.Length;
        }
        return read >= 0x1000 ? image : [];
    }

    /// <summary>
    /// 배열 전 구간에서 표본을 뽑아 유닛 객체 수를 센다.
    /// 풀에는 유닛이 아닌 객체도 섞이므로 "유닛이 충분히 많은가"만 본다.
    /// </summary>
    private static (int Units, int Owners) InspectPool(ReadOnlyProcessMemory memory, ulong entries, int count,
        MemoryProfile profile, IReadOnlyCollection<ulong> unitVftables)
    {
        var stride = Math.Max(1, count / SampleEntries);
        var unitObjects = 0;
        var owners = new HashSet<byte>();
        for (var index = 0; index < count; index += stride)
        {
            var unit = TryReadUnitObject(memory, entries, index, profile, unitVftables);
            if (unit is not { } address) continue;
            unitObjects++;
            var owner = memory.ReadAvailable(AddressMath.Add(address, profile.OwnerOffset), 1);
            if (owner.Length == 1) owners.Add(owner[0]);
        }
        return (unitObjects, owners.Count);
    }

    /// <summary>
    /// 배열에서 "null이거나 그럴듯한 객체 포인터"가 이어지는 선두 길이.
    /// 개수 필드가 부풀려진 후보를 거르는 상한으로 쓴다.
    /// </summary>
    private static int ValidPrefixLength(ReadOnlyProcessMemory memory, ulong entries, MemoryProfile profile)
    {
        var maxScan = Math.Min(profile.MaximumUnits, 8192);
        var bytes = memory.ReadAvailable(entries, maxScan * profile.EntryStride);
        var slots = bytes.Length / profile.EntryStride;
        for (var index = 0; index < slots; index++)
        {
            var value = BitConverter.ToUInt64(bytes, index * profile.EntryStride + profile.EntryPointerOffset);
            if (value == 0) continue;
            if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(value)) return index;
        }
        return slots;
    }

    private static ulong? TryReadUnitObject(ReadOnlyProcessMemory memory, ulong entries, int index,
        MemoryProfile profile, IReadOnlyCollection<ulong> unitVftables)
    {
        var slot = memory.ReadAvailable(
            AddressMath.Add(entries, (long)index * profile.EntryStride + profile.EntryPointerOffset), sizeof(ulong));
        if (slot.Length < sizeof(ulong)) return null;
        var value = BitConverter.ToUInt64(slot, 0);
        if (value == 0 || !ReadOnlyProcessMemory.IsPlausibleUserAddress(value)) return null;
        var head = memory.ReadAvailable(value, sizeof(ulong));
        return head.Length >= sizeof(ulong) && unitVftables.Contains(BitConverter.ToUInt64(head, 0)) ? value : null;
    }

    private readonly record struct PoolCandidate(ulong Address, ulong Entries, int Count, int UnitObjects, int Owners);

    /// <summary>모듈 이미지에서 MSVC RTTI 클래스명에 해당하는 vftable 주소를 모두 찾는다.</summary>
    private static List<ulong> FindClassVftables(byte[] image, ulong moduleBase, string className)
    {
        var name = Encoding.ASCII.GetBytes(className + "\0");
        var typeDescriptorRvas = new List<uint>();
        for (var index = 0; index + name.Length <= image.Length; index++)
        {
            if (image[index] != name[0]) continue;
            if (!image.AsSpan(index, name.Length).SequenceEqual(name)) continue;
            if (index >= 0x10) typeDescriptorRvas.Add((uint)(index - 0x10)); // 이름은 타입 디스크립터 +0x10
        }

        // RTTICompleteObjectLocator: signature=1, +0x0C=타입 RVA, +0x14=자기 RVA
        var locators = new List<ulong>();
        foreach (var typeRva in typeDescriptorRvas)
            for (var index = 0; index + 0x18 <= image.Length; index += 4)
                if (BitConverter.ToUInt32(image, index) == 1 &&
                    BitConverter.ToUInt32(image, index + 0x0C) == typeRva &&
                    BitConverter.ToUInt32(image, index + 0x14) == (uint)index)
                    locators.Add(moduleBase + (ulong)index);

        // vftable 바로 앞(-8)에 COL 주소가 놓인다.
        var vftables = new List<ulong>();
        if (locators.Count == 0) return vftables;
        var locatorSet = locators.ToHashSet();
        for (var index = 0; index + 8 <= image.Length; index += 8)
            if (locatorSet.Contains(BitConverter.ToUInt64(image, index)))
                vftables.Add(moduleBase + (ulong)index + 8);
        return vftables;
    }
}
