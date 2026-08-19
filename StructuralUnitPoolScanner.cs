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
///
/// 메모리를 세 번 훑되 후보마다 개별 읽기를 하지 않는다(그러면 스캔이 수십 초로 늘어난다).
///   1차: 유닛 객체 주소와 소유자 수집
///   2차: 유닛 포인터가 밀집한 배열 구간 탐색
///   3차: 그 배열을 entriesPointerOffset으로 가리키는 구조체(=풀 루트) 확정
/// 전역 풀만 여러 소유자의 유닛을 함께 담으므로 그것으로 다른 목록과 구분한다.
/// 구분되지 않으면 fail-closed.
/// </summary>
internal static class StructuralUnitPoolScanner
{
    private const int MaximumArrayGapSlots = 64;   // 풀에는 유닛 아닌 객체도 섞인다
    private const int ArrayBaseSlack = 8192;      // 배열 선두가 유닛이 아닐 수 있다
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
        var unitVftables = FindClassVftables(image, moduleBase, profile.UnitClassName).ToHashSet();
        if (unitVftables.Count == 0)
            throw new InvalidOperationException($"{profile.UnitClassName} vftable을 찾지 못했습니다.");

        var units = CollectUnits(memory, profile, unitVftables, token);
        if (units.Count < profile.MinimumUnitObjects)
            throw new InvalidOperationException("유닛 객체가 충분하지 않습니다(대전 중이 아닐 수 있습니다).");

        var best = FindBestUnitArray(memory, units, profile, token);
        return FindPoolRoot(memory, best, units, profile, token);
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

    /// <summary>1차 훑기: 유닛 객체 주소 → 소유자. 버퍼 안에서만 판정하므로 추가 읽기가 없다.</summary>
    private static Dictionary<ulong, byte> CollectUnits(ReadOnlyProcessMemory memory, MemoryProfile profile,
        HashSet<ulong> unitVftables, CancellationToken token)
    {
        var units = new Dictionary<ulong, byte>();
        foreach (var (chunkBase, buffer) in memory.ReadChunks(memory.ReadableRegions()))
        {
            token.ThrowIfCancellationRequested();
            var limit = buffer.Length - profile.OwnerOffset - 1;
            for (var offset = 0; offset <= limit; offset += 8)
            {
                if (!unitVftables.Contains(BitConverter.ToUInt64(buffer, offset))) continue;
                units[chunkBase + (ulong)offset] = buffer[offset + profile.OwnerOffset];
            }
        }
        return units;
    }

    /// <summary>2차 훑기: 유닛 포인터가 밀집한 구간을 배열 후보로 모으고, 전역 풀을 고른다.</summary>
    private static UnitArray FindBestUnitArray(ReadOnlyProcessMemory memory, Dictionary<ulong, byte> units,
        MemoryProfile profile, CancellationToken token)
    {
        var arrays = new List<UnitArray>();
        foreach (var (chunkBase, buffer) in memory.ReadChunks(memory.ReadableRegions()))
        {
            token.ThrowIfCancellationRequested();
            var start = -1;
            var last = -1;
            var owners = new HashSet<byte>();
            var hits = 0;

            for (var offset = 0; offset + 8 <= buffer.Length; offset += 8)
            {
                if (!units.TryGetValue(BitConverter.ToUInt64(buffer, offset), out var owner))
                {
                    if (start >= 0 && offset - last > MaximumArrayGapSlots * 8)
                    {
                        Flush(chunkBase, start, hits, owners, arrays, profile);
                        start = -1; hits = 0; owners = [];
                    }
                    continue;
                }
                if (start < 0) start = offset;
                last = offset;
                hits++;
                owners.Add(owner);
            }
            Flush(chunkBase, start, hits, owners, arrays, profile);
        }

        if (arrays.Count == 0)
            throw new InvalidOperationException("유닛 포인터 배열을 찾지 못했습니다.");

        var ranked = arrays.OrderByDescending(x => x.Owners).ThenByDescending(x => x.Units).ToList();
        var best = ranked[0];
        if (ranked.Count > 1 && ranked[1].Owners == best.Owners && ranked[1].Units == best.Units)
            throw new InvalidOperationException($"구분되지 않는 유닛 배열이 {ranked.Count}개여서 프로필을 차단했습니다.");
        return best;

        static void Flush(ulong chunkBase, int start, int hits, HashSet<byte> owners, List<UnitArray> arrays,
            MemoryProfile profile)
        {
            if (start < 0 || hits < profile.MinimumUnitObjects) return;
            arrays.Add(new UnitArray(chunkBase + (ulong)start, hits, owners.Count));
        }
    }

    /// <summary>3차 훑기: 배열 선두를 가리키는 포인터를 찾아 풀 루트(구조체 시작)를 확정한다.</summary>
    private static ulong FindPoolRoot(ReadOnlyProcessMemory memory, UnitArray array,
        Dictionary<ulong, byte> units, MemoryProfile profile, CancellationToken token)
    {
        var low = array.FirstUnitSlot - (ulong)(ArrayBaseSlack * profile.EntryStride);
        var roots = new List<(ulong Address, int Count)>();
        foreach (var (chunkBase, buffer) in memory.ReadChunks(memory.ReadableRegions()))
        {
            token.ThrowIfCancellationRequested();
            for (var offset = profile.EntriesPointerOffset - profile.CountOffset; offset + 8 <= buffer.Length; offset += 8)
            {
                var value = BitConverter.ToUInt64(buffer, offset);
                if (value > array.FirstUnitSlot || value < low || (value & 7) != 0) continue;
                // 개수 필드는 같은 구조체 안에 있으므로 같은 버퍼에서 읽는다.
                var countOffset = offset - (profile.EntriesPointerOffset - profile.CountOffset);
                if (countOffset < 0 || countOffset + 4 > buffer.Length) continue;
                var count = BitConverter.ToInt32(buffer, countOffset);
                if (count < array.Units || count > profile.MaximumUnits) continue;
                // 구조체 시작 주소로 환산한다(개수 필드 위치 − countOffset).
                roots.Add((AddressMath.Add(chunkBase + (ulong)countOffset, -profile.CountOffset), count));
            }
        }

        if (roots.Count == 0)
            throw new InvalidOperationException("유닛 배열을 참조하는 풀 루트를 찾지 못했습니다.");

        // 후보가 여럿이면 각자의 (배열, 개수)를 실제로 읽어, 끝까지 성한 포인터만 담고
        // 유닛을 가장 많이 설명하는 루트를 고른다. 개수 필드가 우연히 큰 구조체는 여기서 탈락한다.
        var scored = new List<(ulong Address, int Units)>();
        foreach (var (address, count) in roots)
        {
            token.ThrowIfCancellationRequested();
            var entries = memory.ReadAvailable(AddressMath.Add(address, profile.EntriesPointerOffset), sizeof(ulong));
            if (entries.Length < sizeof(ulong)) continue;
            var arrayAddress = BitConverter.ToUInt64(entries, 0);
            if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(arrayAddress)) continue;
            var slots = memory.ReadAvailable(arrayAddress, count * profile.EntryStride);
            if (slots.Length < count * profile.EntryStride) continue;

            var hits = 0;
            var broken = false;
            for (var index = 0; index < count && !broken; index++)
            {
                var value = BitConverter.ToUInt64(slots, index * profile.EntryStride + profile.EntryPointerOffset);
                if (value == 0) continue;
                if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(value)) broken = true;
                else if (units.ContainsKey(value)) hits++;
            }
            if (!broken && hits >= profile.MinimumUnitObjects) scored.Add((address, hits));
        }

        if (scored.Count == 0)
            throw new InvalidOperationException("풀 루트 후보가 모두 검증에 실패했습니다.");
        return scored.OrderByDescending(x => x.Units).First().Address;
    }

    /// <summary>배열에서 "null이거나 그럴듯한 객체 포인터"가 이어지는 선두 길이.</summary>
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
        var locators = new HashSet<ulong>();
        foreach (var typeRva in typeDescriptorRvas)
            for (var index = 0; index + 0x18 <= image.Length; index += 4)
                if (BitConverter.ToUInt32(image, index) == 1 &&
                    BitConverter.ToUInt32(image, index + 0x0C) == typeRva &&
                    BitConverter.ToUInt32(image, index + 0x14) == (uint)index)
                    locators.Add(moduleBase + (ulong)index);

        // vftable 바로 앞(-8)에 COL 주소가 놓인다.
        var vftables = new List<ulong>();
        if (locators.Count == 0) return vftables;
        for (var index = 0; index + 8 <= image.Length; index += 8)
            if (locators.Contains(BitConverter.ToUInt64(image, index)))
                vftables.Add(moduleBase + (ulong)index + 8);
        return vftables;
    }

    private readonly record struct UnitArray(ulong FirstUnitSlot, int Units, int Owners);
}
