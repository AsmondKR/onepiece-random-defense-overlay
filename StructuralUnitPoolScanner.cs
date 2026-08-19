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

        var unitReferences = CollectUnitReferences(memory, units, token);
        return FindPoolRoot(memory, units, unitReferences, profile, token);
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

    /// <summary>
    /// 2차 훑기: 유닛 포인터가 놓여 있는 주소를 모은다.
    /// 이 집합만 있으면 이후 배열 판정을 추가 읽기 없이 조회로 끝낼 수 있다.
    /// </summary>
    private static Dictionary<ulong, ulong> CollectUnitReferences(ReadOnlyProcessMemory memory,
        Dictionary<ulong, byte> units, CancellationToken token)
    {
        var references = new Dictionary<ulong, ulong>();
        foreach (var (chunkBase, buffer) in memory.ReadChunks(memory.ReadableRegions()))
        {
            token.ThrowIfCancellationRequested();
            for (var offset = 0; offset + 8 <= buffer.Length; offset += 8)
            {
                var value = BitConverter.ToUInt64(buffer, offset);
                if (value != 0 && units.ContainsKey(value)) references[chunkBase + (ulong)offset] = value;
            }
        }
        return references;
    }

    /// <summary>
    /// 3차 훑기: countOffset/entriesPointerOffset 규격을 만족하는 구조체를 훑으면서,
    /// 그 배열이 실제로 유닛 포인터를 담고 있는지 참조 집합 조회만으로 판정한다.
    /// 여러 소유자의 유닛을 함께 담는 전역 풀을 고르고, 1·2위가 구분되지 않으면 fail-closed.
    /// </summary>
    private static ulong FindPoolRoot(ReadOnlyProcessMemory memory, Dictionary<ulong, byte> units,
        Dictionary<ulong, ulong> unitReferences, MemoryProfile profile, CancellationToken token)
    {
        var candidates = new List<(ulong Address, int Hits, int Owners, int Count, ulong Entries)>();
        // 전역 풀이라면 살아 있는 유닛의 과반을 담고 있어야 한다.
        var minimumDistinctUnits = Math.Max(profile.MinimumUnitObjects, units.Count / 2);
        var distinctUnits = new HashSet<ulong>();
        var owners = new HashSet<byte>();
        foreach (var (chunkBase, buffer) in memory.ReadChunks(memory.ReadableRegions()))
        {
            token.ThrowIfCancellationRequested();
            var limit = buffer.Length - profile.EntriesPointerOffset - sizeof(ulong);
            for (var offset = 0; offset <= limit; offset += 8)
            {
                var count = BitConverter.ToInt32(buffer, offset + profile.CountOffset);
                if (count < profile.MinimumUnitObjects || count > profile.MaximumUnits) continue;
                var entries = BitConverter.ToUInt64(buffer, offset + profile.EntriesPointerOffset);
                if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(entries) || (entries & 7) != 0) continue;

                // 전역 풀은 살아 있는 유닛을 중복 없이 한 번씩 담는다. 객체 힙을 우연히 훑은 창은
                // 같은 유닛이 여러 번 나오거나 서로 다른 유닛 수가 적어 여기서 갈린다.
                distinctUnits.Clear();
                owners.Clear();
                for (var index = 0; index < count; index++)
                {
                    var slot = entries + (ulong)((long)index * profile.EntryStride + profile.EntryPointerOffset);
                    if (!unitReferences.TryGetValue(slot, out var unit)) continue;
                    if (!distinctUnits.Add(unit)) continue;
                    if (units.TryGetValue(unit, out var owner)) owners.Add(owner);
                }
                if (distinctUnits.Count < minimumDistinctUnits) continue;
                candidates.Add((chunkBase + (ulong)offset, distinctUnits.Count, owners.Count, count, entries));
            }
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException("유닛 풀 구조를 찾지 못했습니다(대전 중이 아닐 수 있습니다).");

        // 겹침 청크 때문에 같은 구조체가 두 번 잡히므로 주소로 중복을 없앤다.
        // 같은 배열을 가리키는 구조체는 하나로 묶고(유닛을 가장 많이 설명하되 개수가 가장 작은 것),
        // 서로 다른 배열끼리만 모호성을 따진다.
        var byArray = candidates
            .DistinctBy(x => x.Address)
            .GroupBy(x => x.Entries)
            .Select(group => group.OrderByDescending(x => x.Hits).ThenBy(x => x.Count).First())
            .OrderByDescending(x => x.Owners).ThenByDescending(x => x.Hits)
            .ToList();

        // 8바이트씩 훑다 보면 진짜 구조체 주변의 어긋난 위치도 그럴듯한 (개수, 배열) 쌍을 만든다.
        // 같은 유닛 집합을 담는 후보 중에서는 슬롯이 가장 적은 것이 실제 풀이며,
        // 마지막으로 배열을 끝까지 읽어 성한 포인터만 들어 있는지 확인하고 채택한다.
        foreach (var candidate in byArray.OrderByDescending(x => x.Owners)
                     .ThenByDescending(x => x.Hits).ThenBy(x => x.Count))
        {
            token.ThrowIfCancellationRequested();
            var slots = memory.ReadAvailable(candidate.Entries, candidate.Count * profile.EntryStride);
            if (slots.Length < candidate.Count * profile.EntryStride) continue;
            var sound = true;
            for (var index = 0; index < candidate.Count && sound; index++)
            {
                var value = BitConverter.ToUInt64(slots, index * profile.EntryStride + profile.EntryPointerOffset);
                if (value != 0 && !ReadOnlyProcessMemory.IsPlausibleUserAddress(value)) sound = false;
            }
            if (sound) return candidate.Address;
        }

        var top = string.Join(" / ", byArray.Take(3).Select(x => $"소유자{x.Owners}·유닛{x.Hits}·슬롯{x.Count}"));
        throw new InvalidOperationException($"풀 후보 {byArray.Count}개가 모두 검증에 실패했습니다 (상위: {top}).");
    }

    /// <summary>배열에서 "null이거나 그럴듯한 객체 포인터"가 이어지는 선두 길이.</summary>

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

}
