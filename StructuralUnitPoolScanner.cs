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

        var candidates = new List<ulong>();
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
                if (!LooksLikeUnitPool(memory, entries, count, profile, unitVftables)) continue;

                var candidate = chunkBase + (ulong)offset;
                if (candidates.Contains(candidate)) continue;
                candidates.Add(candidate);
                if (candidates.Count > MaximumCandidates) break;
            }
            if (candidates.Count > MaximumCandidates) break;
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException("유닛 풀 구조를 찾지 못했습니다(대전 중이 아닐 수 있습니다).");
        if (candidates.Count > 1)
            throw new InvalidOperationException($"유닛 풀 후보가 {candidates.Count}개여서 프로필을 차단했습니다.");
        return candidates[0];
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

    private static bool LooksLikeUnitPool(ReadOnlyProcessMemory memory, ulong entries, int count,
        MemoryProfile profile, IReadOnlyCollection<ulong> unitVftables)
    {
        var sample = Math.Min(count, SampleEntries);
        var pointerBytes = memory.ReadAvailable(entries, sample * profile.EntryStride);
        if (pointerBytes.Length < profile.EntryStride) return false;

        var sampled = pointerBytes.Length / profile.EntryStride;
        var unitObjects = 0;
        for (var index = 0; index < sampled; index++)
        {
            var value = BitConverter.ToUInt64(pointerBytes, index * profile.EntryStride + profile.EntryPointerOffset);
            if (value == 0) continue;
            if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(value)) return false;
            var head = memory.ReadAvailable(value, sizeof(ulong));
            if (head.Length < sizeof(ulong)) continue;
            if (unitVftables.Contains(BitConverter.ToUInt64(head, 0))) unitObjects++;
        }
        return unitObjects >= Math.Min(profile.MinimumUnitObjects, sampled);
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
