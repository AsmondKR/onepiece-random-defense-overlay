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
/// 힙(전용 커밋 영역)만 한 번 병렬로 훑어 유닛 객체와 구조체 후보를 함께 모은 뒤,
/// 살아남은 후보의 배열만 읽어 판정한다(후보마다 메모리를 훑으면 스캔이 수십 초로 늘어난다).
/// 전역 풀만 여러 소유자의 유닛을 함께 담으므로 그것으로 다른 목록과 구분한다.
/// 구분되지 않으면 fail-closed.
/// </summary>
internal static class StructuralUnitPoolScanner
{
    /// <summary>전역 풀로 인정할 최소 슬롯 수. 훑는 도중 후보를 걸러 담기 위한 하한이다.</summary>
    private const int MinimumPoolSlots = 64;

    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(5);
    private static readonly object Gate = new();
    private static DateTime _lastFailureUtc = DateTime.MinValue;
    private static string _lastFailure = "";
    private static bool _lastFailureWasNotReady;

    /// <summary>마지막 구조 스캔에 걸린 시간. 진단 문구에 노출한다.</summary>
    public static int LastScanMilliseconds { get; private set; }

    public static ulong Resolve(ReadOnlyProcessMemory memory, ProcessModule module, MemoryProfile profile,
        CancellationToken token)
    {
        // 전체 메모리 스캔은 비싸다. 직전 실패 직후에는 같은 사유로 즉시 되돌려 매 틱 재스캔을 막는다.
        lock (Gate)
            if (DateTime.UtcNow - _lastFailureUtc < FailureCooldown)
                throw _lastFailureWasNotReady
                    ? new PoolNotReadyException(_lastFailure)
                    : new InvalidOperationException(_lastFailure);
        try
        {
            var watch = Stopwatch.StartNew();
            var root = ResolveCore(memory, module, profile, token);
            LastScanMilliseconds = (int)watch.ElapsedMilliseconds;
            return root;
        }
        catch (InvalidOperationException exception)
        {
            lock (Gate)
            {
                _lastFailureUtc = DateTime.UtcNow;
                _lastFailure = exception.Message;
                _lastFailureWasNotReady = exception is PoolNotReadyException;
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

        // vftable은 모듈이 같으면 변하지 않는다. 221MB 이미지 재독을 매번 하지 않도록 캐시한다.
        var unitVftables = GetUnitVftables(memory, moduleBase, moduleSize, profile.UnitClassName);

        var (units, structs) = Sweep(memory, profile, unitVftables, token);
        if (units.Count < profile.MinimumUnitObjects)
            throw new PoolNotReadyException("대전 준비 중입니다(유닛이 아직 생성되지 않았습니다).");

        return SelectPoolRoot(memory, units, structs, profile, token);
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
    /// 힙을 한 번만 훑어 유닛 객체(주소→소유자)와 풀 구조체 후보를 함께 모은다.
    /// 영역별로 병렬 처리하되 버퍼 안 비교만 하므로 후보당 추가 읽기가 없다.
    /// </summary>
    private static (Dictionary<ulong, byte> Units, List<PoolStruct> Structs) Sweep(ReadOnlyProcessMemory memory,
        MemoryProfile profile, HashSet<ulong> unitVftables, CancellationToken token)
    {
        var units = new Dictionary<ulong, byte>();
        var structs = new List<PoolStruct>();
        var gate = new object();

        Parallel.ForEach(memory.ReadableRegions(),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount), CancellationToken = token },
            () => (Units: new Dictionary<ulong, byte>(), Structs: new List<PoolStruct>()),
            (region, _, local) =>
            {
                foreach (var (chunkBase, buffer) in memory.ReadChunks([region]))
                    ScanBuffer(chunkBase, buffer, profile, unitVftables, local.Units, local.Structs);
                return local;
            },
            local =>
            {
                lock (gate)
                {
                    foreach (var pair in local.Units) units[pair.Key] = pair.Value;
                    structs.AddRange(local.Structs);
                }
            });

        return (units, structs);
    }

    /// <summary>
    /// 버퍼 한 덩어리에서 유닛 객체와 풀 구조체 후보를 찾는다.
    /// 오프셋이 모두 8의 배수라 qword 배열로 보고 훑는다 — 이 루프가 스캔 시간의 대부분이라
    /// 바이트 단위 변환이나 해시 조회를 넣지 않는다.
    /// </summary>
    private static void ScanBuffer(ulong chunkBase, byte[] buffer, MemoryProfile profile,
        HashSet<ulong> unitVftables, Dictionary<ulong, byte> units, List<PoolStruct> structs)
    {
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(
            buffer.AsSpan(0, buffer.Length & ~7));
        var singleVftable = unitVftables.Count == 1 ? unitVftables.First() : 0;
        var countWord = profile.CountOffset / 8;
        var entriesWord = profile.EntriesPointerOffset / 8;
        var alignedOffsets = profile.CountOffset % 8 == 0 && profile.EntriesPointerOffset % 8 == 0;
        var unitWordLimit = (buffer.Length - profile.OwnerOffset - 1) / 8;

        for (var index = 0; index < words.Length; index++)
        {
            var value = words[index];
            if (value != 0 && index <= unitWordLimit &&
                (singleVftable != 0 ? value == singleVftable : unitVftables.Contains(value)))
                units[chunkBase + (ulong)index * 8] = buffer[index * 8 + profile.OwnerOffset];

            if (!alignedOffsets || index + entriesWord >= words.Length) continue;
            var count = (int)(uint)words[index + countWord];
            if (count < MinimumPoolSlots || count > profile.MaximumUnits) continue;
            var entries = words[index + entriesWord];
            if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(entries) || (entries & 7) != 0) continue;
            structs.Add(new PoolStruct(chunkBase + (ulong)index * 8, count, entries));
        }
    }

    private static readonly object VftableGate = new();
    private static (ulong ModuleBase, string ClassName, HashSet<ulong> Vftables)? _vftableCache;

    private static HashSet<ulong> GetUnitVftables(ReadOnlyProcessMemory memory, ulong moduleBase, int moduleSize,
        string className)
    {
        lock (VftableGate)
            if (_vftableCache is { } cached && cached.ModuleBase == moduleBase && cached.ClassName == className)
                return cached.Vftables;

        var image = ReadImage(memory, moduleBase, moduleSize);
        if (image.Length < 0x1000) throw new InvalidDataException("모듈 이미지를 읽지 못했습니다.");
        var vftables = FindClassVftables(image, moduleBase, className).ToHashSet();
        if (vftables.Count == 0)
            throw new InvalidOperationException($"{className} vftable을 찾지 못했습니다.");
        lock (VftableGate) _vftableCache = (moduleBase, className, vftables);
        return vftables;
    }

    /// <summary>
    /// 모아 둔 구조체 후보 중에서 실제 전역 풀을 고른다.
    /// 전역 풀은 살아 있는 유닛을 중복 없이 한 번씩 담으므로, 배열을 한 번 읽어
    /// 서로 다른 유닛 수와 소유자 종류로 판정한다. 구분되지 않으면 fail-closed.
    /// </summary>
    private static ulong SelectPoolRoot(ReadOnlyProcessMemory memory, Dictionary<ulong, byte> units,
        List<PoolStruct> structs, MemoryProfile profile, CancellationToken token)
    {
        // 전역 풀은 살아 있는 유닛을 넉넉히 담지만 "과반"을 요구하면 안 된다 — 메모리에는
        // 풀 밖의 CUnit(죽은 유닛·다른 구조체 소속)도 함께 잡히기 때문이다. 실제로 판이
        // 길어져 유닛이 565개로 늘었을 때 과반 조건이 인식을 통째로 막았다.
        // 순위 매기기(소유자 종류 → 유닛 수)가 진짜 풀을 골라 주므로 하한은 낮게 둔다.
        var minimumDistinctUnits = Math.Max(profile.MinimumUnitObjects,
            Math.Min(MinimumPoolSlots, units.Count / 4));
        var candidates = new List<(ulong Address, int Hits, int Owners, int Count, ulong Entries)>();
        var distinctUnits = new HashSet<ulong>();
        var owners = new HashSet<byte>();

        foreach (var candidate in structs.Where(x => x.Count >= minimumDistinctUnits)
                     .DistinctBy(x => (x.Entries, x.Count)))
        {
            token.ThrowIfCancellationRequested();
            var bytes = memory.ReadAvailable(candidate.Entries, candidate.Count * profile.EntryStride);
            if (bytes.Length < candidate.Count * profile.EntryStride) continue;

            distinctUnits.Clear();
            owners.Clear();
            var sound = true;
            for (var index = 0; index < candidate.Count && sound; index++)
            {
                var value = BitConverter.ToUInt64(bytes, index * profile.EntryStride + profile.EntryPointerOffset);
                if (value == 0) continue;
                if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(value)) { sound = false; break; }
                if (!units.TryGetValue(value, out var owner) || !distinctUnits.Add(value)) continue;
                owners.Add(owner);
            }
            // 배열에 깨진 포인터가 섞여 있으면 풀이 아니다(진짜 풀은 끝까지 성한 포인터만 담는다).
            if (!sound || distinctUnits.Count < minimumDistinctUnits) continue;
            candidates.Add((candidate.Address, distinctUnits.Count, owners.Count, candidate.Count, candidate.Entries));
        }

        if (candidates.Count == 0)
            throw new PoolNotReadyException("대전 준비 중입니다(유닛 풀이 아직 만들어지지 않았습니다).");

        // 8바이트씩 훑다 보면 진짜 구조체 주변의 어긋난 위치도 그럴듯한 (개수, 배열) 쌍을 만든다.
        // 같은 유닛 집합을 담는 후보 중에서는 슬롯이 가장 적은 것이 실제 풀이다.
        var ranked = candidates
            .GroupBy(x => x.Entries)
            .Select(group => group.OrderByDescending(x => x.Hits).ThenBy(x => x.Count).First())
            .OrderByDescending(x => x.Owners).ThenByDescending(x => x.Hits).ThenBy(x => x.Count)
            .ToList();

        if (ranked.Count > 1 && ranked[1].Owners == ranked[0].Owners && ranked[1].Hits == ranked[0].Hits)
        {
            var top = string.Join(" / ", ranked.Take(3).Select(x => $"소유자{x.Owners}·유닛{x.Hits}·슬롯{x.Count}"));
            throw new InvalidOperationException($"구분되지 않는 유닛 배열이 {ranked.Count}개여서 프로필을 차단했습니다 (상위: {top}).");
        }
        return ranked[0].Address;
    }

    private readonly record struct PoolStruct(ulong Address, int Count, ulong Entries);

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

/// <summary>
/// 대전 준비/로딩 중이라 유닛이나 풀이 아직 없는 상태.
/// 읽기 오류가 아니라 대기 상태로 표시해야 한다.
/// </summary>
internal sealed class PoolNotReadyException(string message) : InvalidOperationException(message);
