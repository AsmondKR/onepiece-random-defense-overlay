using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OrandOverlay;

// Direct Warcraft reader. Profiles remain fail-closed until an exact executable hash and
// all layout offsets have been independently verified for that build.
public sealed class WarcraftMemoryRecognitionService : IInventoryRecognizer
{
    private readonly RawcodeUnitMap _unitMap;
    private readonly MemoryProfileRepository _profiles = new();
    private readonly object _cacheGate = new();
    private LocatorCache? _locatorCache;

    public WarcraftMemoryRecognitionService(DataCatalog catalog) => _unitMap = new RawcodeUnitMap(catalog);

    public Task<RecognitionResult> RecognizeAsync(AppSettings settings, CancellationToken cancellationToken) =>
        Task.Run(() => Recognize(cancellationToken), cancellationToken);

    private RecognitionResult Recognize(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var loaded = _profiles.GetProfiles();
            if (loaded.Error is not null)
                return Failure(RecognitionState.ConfigurationError, "메모리 프로필 오류 · 기존 패 유지", loaded.Error,
                    profileSource: loaded.Source);
            if (_unitMap.Error is not null)
                return Failure(RecognitionState.ConfigurationError, "rawcode 데이터 오류 · 기존 패 유지", _unitMap.Error);

            using var process = FindNewestProcess("Warcraft III");
            if (process is null)
                return Failure(RecognitionState.Waiting, "워크 미실행 · 기존 패 유지", "Warcraft III 프로세스를 기다리는 중입니다.");

            var mainModule = process.MainModule;
            var version = mainModule?.FileVersionInfo.FileVersion ?? "unknown";
            var baseDiagnostics = new RecognitionDiagnostics
            {
                Source = "WarcraftMemory",
                ProcessId = process.Id,
                ProcessVersion = version,
                ProfileSource = loaded.Source
            };
            var profile = loaded.Profiles.FirstOrDefault(x =>
                x.FileVersion.Equals(version, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
                return Failure(RecognitionState.Unsupported, $"워크 {version} 프로필 없음 · 기존 패 유지",
                    "이 빌드와 정확히 일치하는 프로필만 사용할 수 있습니다.", baseDiagnostics);

            baseDiagnostics = WithProfile(baseDiagnostics, profile);
            // 검증 세션(ORAND_VERIFY_DIR 지정) 동안에만 미검증 프로필을 임시 활성한다.
            // 실전 1판 검증을 통과해야 verified=true로 핀되므로, 일반 실행에서는 fail-closed 유지.
            var verificationSession = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("ORAND_VERIFY_DIR"));
            if (!profile.Enabled || (!profile.Verified && !verificationSession))
                return Failure(RecognitionState.UnverifiedProfile, $"프로필 미검증 · {version} · 기존 패 유지",
                    "enabled와 verified가 모두 true인 검증 프로필이 필요합니다.", baseDiagnostics);

            var validationErrors = MemoryProfileValidator.Validate(profile);
            if (validationErrors.Count > 0)
                return Failure(RecognitionState.ConfigurationError, "프로필 구성 오류 · 기존 패 유지",
                    string.Join("; ", validationErrors), baseDiagnostics);

            var executablePath = mainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
                return Failure(RecognitionState.TransientReadError, "워크 실행 파일 확인 실패 · 기존 패 유지",
                    "MainModule.FileName을 읽을 수 없습니다.", baseDiagnostics);
            var actualHash = ExecutableHashCache.Sha256(executablePath);
            if (!actualHash.Equals(profile.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                return Failure(RecognitionState.UnverifiedProfile, "워크 빌드 해시 불일치 · 기존 패 유지",
                    $"프로필 {profile.ExecutableSha256[..12]}… / 실행 파일 {actualHash[..12]}…", baseDiagnostics);

            var module = process.Modules.Cast<ProcessModule>().FirstOrDefault(x =>
                x.ModuleName.Equals(profile.ModuleName, StringComparison.OrdinalIgnoreCase));
            if (module is null)
                return Failure(RecognitionState.TransientReadError, $"모듈 없음 · {profile.ModuleName}",
                    "대상 모듈이 아직 로드되지 않았습니다.", baseDiagnostics);

            using var memory = ReadOnlyProcessMemory.Open(process.Id);
            var processStarted = process.StartTime.ToUniversalTime().Ticks;
            var moduleBase = (ulong)module.BaseAddress.ToInt64();
            // 로컬 슬롯은 실측 앵커가 있으면 실제 값을, 없으면 프로필 고정값을 쓴다.
            var measuredSlot = StructuralUnitPoolScanner.TryReadLocalPlayerSlot(memory, moduleBase, profile);
            // 앵커가 있는데 로컬 플레이어가 없으면 대전 중이 아니다. 비싼 구조 스캔을 돌리지 않는다.
            if (profile.HasLocalPlayerAnchor && measuredSlot is null)
                return Failure(RecognitionState.Waiting, "대전 대기 중 · 기존 패 유지",
                    "게임에 입장하면 인식을 시작합니다.", baseDiagnostics);
            var localSlot = measuredSlot ?? profile.LocalPlayerSlot;
            var locatorAddress = GetLocatorAddress(memory, process, processStarted, module, profile, loaded.Generation, token);
            var listAddress = FollowPointerPath(memory, locatorAddress, profile.PointerOffsets);
            MemoryUnitSnapshot snapshot;
            try
            {
                snapshot = ReadConsistentSnapshot(memory, listAddress, profile, localSlot, _unitMap.IsGrowthUnit, token);
            }
            catch (SnapshotChangedException)
            {
                token.ThrowIfCancellationRequested();
                listAddress = FollowPointerPath(memory, locatorAddress, profile.PointerOffsets);
                snapshot = ReadConsistentSnapshot(memory, listAddress, profile, localSlot, _unitMap.IsGrowthUnit, token);
            }

            // 성장 중인 특별함 유닛이 판 전체에 하나뿐이면 그것은 로컬 플레이어 것이다.
            // 여럿이면 어느 플레이어 것인지 가릴 수단이 없으므로 아무것도 넣지 않는다(오귀속 방지).
            var counts = snapshot.RawcodeCounts;
            var growthTotal = snapshot.NeutralGrowthCounts.Values.Sum();
            var adoptedGrowth = growthTotal == 1;
            if (adoptedGrowth)
            {
                counts = new Dictionary<uint, int>(counts);
                foreach (var pair in snapshot.NeutralGrowthCounts)
                    counts[pair.Key] = counts.GetValueOrDefault(pair.Key) + pair.Value;
            }
            var mapped = _unitMap.Map(counts);
            var diagnostics = new RecognitionDiagnostics
            {
                Source = baseDiagnostics.Source,
                ProcessId = baseDiagnostics.ProcessId,
                ProcessVersion = baseDiagnostics.ProcessVersion,
                ProfileId = profile.ProfileId,
                ProfileRevision = profile.ProfileRevision,
                ProfileSource = loaded.Source,
                ResolvedListAddress = $"0x{listAddress:X}",
                ObservedObjects = snapshot.OwnedObjects + (adoptedGrowth ? 1 : 0),
                MappedObjects = mapped.KnownCount + mapped.CatalogNamedCount,
                UnknownObjects = mapped.UnknownCount,
                UnknownRawcodes = mapped.UnknownRawcodes,
                Detail = $"목록 슬롯 {snapshot.ListCount} · 추천 데이터 연결 {mapped.KnownCount} · " +
                         $"이름 카탈로그 연결 {mapped.CatalogNamedCount} · 중복 포인터 {snapshot.DuplicatePointers}" +
                         (adoptedGrowth ? " · 중앙 성장형 1기 포함" :
                             growthTotal > 1 ? $" · 중앙 성장형 {growthTotal}기(귀속 불가로 제외)" : "") +
                         (profile.LocatorKind == MemoryLocatorKind.StructuralScan
                             ? $" · 구조 탐색 {StructuralUnitPoolScanner.LastScanMilliseconds}ms"
                             : "")
            };
            if (profile.RequireNonEmptyInventory && snapshot.OwnedObjects == 0)
                return new RecognitionResult
                {
                    State = RecognitionState.Waiting,
                    Status = "보유 패 없음 · 대기 중",
                    Diagnostics = diagnostics
                };
            var catalogMatches = mapped.KnownCount + mapped.CatalogNamedCount;
            // 뽑기 전에는 로컬 소유 객체가 맵 컨트롤러·헬퍼뿐이라 카드가 0장이다.
            // 이것은 읽기 오류가 아니라 아직 패가 없는 상태다.
            if (catalogMatches == 0)
                return new RecognitionResult
                {
                    State = RecognitionState.Waiting,
                    Status = "보유 패 없음 · 대기 중",
                    Diagnostics = diagnostics
                };
            var catalogRatio = (double)catalogMatches / Math.Max(1, diagnostics.ObservedObjects);
            if (catalogRatio < profile.MinimumCatalogMatchRatio)
                return new RecognitionResult
                {
                    State = RecognitionState.TransientReadError,
                    Status = "rawcode 검증 실패 · 기존 패 유지",
                    Diagnostics = new RecognitionDiagnostics
                    {
                        Source = diagnostics.Source,
                        ProcessId = diagnostics.ProcessId,
                        ProcessVersion = diagnostics.ProcessVersion,
                        ProfileId = diagnostics.ProfileId,
                        ProfileRevision = diagnostics.ProfileRevision,
                        ProfileSource = diagnostics.ProfileSource,
                        ResolvedListAddress = diagnostics.ResolvedListAddress,
                        ObservedObjects = diagnostics.ObservedObjects,
                        MappedObjects = diagnostics.MappedObjects,
                        UnknownObjects = diagnostics.UnknownObjects,
                        UnknownRawcodes = diagnostics.UnknownRawcodes,
                        Detail = diagnostics.Detail + $" · 카탈로그 일치율 {catalogRatio:P0} < {profile.MinimumCatalogMatchRatio:P0}"
                    }
                };
            var suffix = mapped.UnknownCount > 0 ? $" · 미등록 {mapped.UnknownCount}" : "";
            return new RecognitionResult
            {
                Entries = mapped.Entries,
                State = RecognitionState.Ready,
                Status = $"워크 메모리 {mapped.Entries.Sum(x => x.Count)}장{suffix}" +
                         (profile.Verified ? "" : " · 검증 세션(임시 활성)") + $" · {DateTime.Now:HH:mm:ss}",
                Diagnostics = diagnostics
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (PoolNotReadyException exception)
        {
            // 맵 로딩·대전 준비 단계에는 유닛이 아직 없다. 오류가 아니라 대기 상태로 알린다.
            lock (_cacheGate) _locatorCache = null;
            return Failure(RecognitionState.Waiting, "대전 준비 중 · 기존 패 유지", exception.Message);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException or InvalidOperationException
                                          or OverflowException or IOException or UnauthorizedAccessException
                                          or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            lock (_cacheGate) _locatorCache = null;
            return Failure(RecognitionState.TransientReadError, "워크 메모리 읽기 실패 · 기존 패 유지", exception.Message);
        }
    }

    private ulong GetLocatorAddress(ReadOnlyProcessMemory memory, Process process, long processStarted,
        ProcessModule module, MemoryProfile profile, long generation, CancellationToken token)
    {
        var key = new LocatorCacheKey(process.Id, processStarted, (ulong)module.BaseAddress.ToInt64(),
            profile.ProfileId, profile.ProfileRevision, generation);
        lock (_cacheGate)
            if (_locatorCache is { } cached && cached.Key == key) return cached.Address;

        var address = ResolveLocator(memory, module, profile, token);
        lock (_cacheGate) _locatorCache = new LocatorCache(key, address);
        return address;
    }

    private static ulong ResolveLocator(ReadOnlyProcessMemory memory, ProcessModule module, MemoryProfile profile,
        CancellationToken token)
    {
        var moduleBase = (ulong)module.BaseAddress.ToInt64();
        if (profile.LocatorKind == MemoryLocatorKind.ModuleOffset)
        {
            if ((ulong)profile.ModuleOffset >= (ulong)module.ModuleMemorySize)
                throw new InvalidDataException("moduleOffset이 대상 모듈 범위를 벗어났습니다.");
            return AddressMath.Add(moduleBase, profile.ModuleOffset);
        }

        if (profile.LocatorKind == MemoryLocatorKind.StructuralScan)
            return StructuralUnitPoolScanner.Resolve(memory, module, profile, token);

        var moduleSize = module.ModuleMemorySize;
        if (moduleSize <= 0 || moduleSize > 512 * 1024 * 1024)
            throw new InvalidDataException($"비정상 모듈 크기: {moduleSize}");
        var bytes = memory.Read(moduleBase, moduleSize);
        var pattern = SignaturePattern.Parse(profile.Signature);
        var matches = pattern.Find(bytes, 2);
        if (matches.Count == 0) throw new InvalidOperationException("현재 빌드의 유닛 목록 서명을 찾지 못했습니다.");
        if (matches.Count > 1) throw new InvalidOperationException("유닛 목록 서명이 고유하지 않아 프로필을 차단했습니다.");

        token.ThrowIfCancellationRequested();
        var instruction = AddressMath.Add(moduleBase, matches[0]);
        var displacementAddress = AddressMath.Add(instruction, profile.RelativeDisplacementOffset);
        var displacement = memory.ReadInt32(displacementAddress);
        return AddressMath.Add(AddressMath.Add(instruction, profile.InstructionLength), displacement);
    }

    private static ulong FollowPointerPath(ReadOnlyProcessMemory memory, ulong address, IReadOnlyList<int> offsets)
    {
        foreach (var offset in offsets)
        {
            var pointer = memory.ReadUInt64(address);
            if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(pointer))
                throw new InvalidDataException($"포인터 체인에 비정상 주소가 있습니다: 0x{pointer:X}");
            address = AddressMath.Add(pointer, offset);
        }
        return address;
    }

    private static MemoryUnitSnapshot ReadConsistentSnapshot(ReadOnlyProcessMemory memory, ulong listAddress,
        MemoryProfile profile, byte localPlayerSlot, Func<uint, bool> isGrowthUnit, CancellationToken token)
    {
        var countAddress = AddressMath.Add(listAddress, profile.CountOffset);
        var entriesAddress = AddressMath.Add(listAddress, profile.EntriesPointerOffset);
        var countBefore = memory.ReadInt32(countAddress);
        if (countBefore < 0 || countBefore > profile.MaximumUnits)
            throw new InvalidDataException($"유닛 수 {countBefore}가 허용 범위 0~{profile.MaximumUnits}를 벗어났습니다.");
        var entriesBefore = profile.EntriesAreInline ? entriesAddress : memory.ReadUInt64(entriesAddress);
        if (countBefore > 0 && !ReadOnlyProcessMemory.IsPlausibleUserAddress(entriesBefore))
            throw new InvalidDataException($"유닛 배열 주소가 비정상입니다: 0x{entriesBefore:X}");

        var counts = new Dictionary<uint, int>();
        var neutralGrowth = new Dictionary<uint, int>();
        var seenPointers = new HashSet<ulong>();
        var duplicatePointers = 0;
        var ownedObjects = 0;
        byte[]? entryPointers = null;
        ulong firstEntry = 0;
        if (countBefore > 0 && profile.EntriesContainPointers)
        {
            firstEntry = AddressMath.Add(entriesBefore, profile.EntryPointerOffset);
            var pointerBytes = checked((countBefore - 1) * profile.EntryStride + sizeof(ulong));
            entryPointers = memory.Read(firstEntry, pointerBytes);
        }
        for (var index = 0; index < countBefore; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = AddressMath.Add(entriesBefore, checked((long)index * profile.EntryStride + profile.EntryPointerOffset));
            var unit = entryPointers is not null
                ? BitConverter.ToUInt64(entryPointers, checked(index * profile.EntryStride))
                : entry;
            if (unit == 0) continue;
            if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(unit))
                throw new InvalidDataException($"비정상 유닛 객체 포인터: 0x{unit:X}");
            if (!seenPointers.Add(unit)) { duplicatePointers++; continue; }

            var ownerAddress = FollowObjectFieldPath(memory, unit, profile.OwnerPointerOffsets, profile.OwnerOffset);
            var owner = memory.ReadByte(ownerAddress);
            var neutral = owner == profile.NeutralPlayerSlot;
            if (owner != localPlayerSlot && !neutral) continue;
            var rawcodeAddress = FollowObjectFieldPath(memory, unit, profile.RawcodePointerOffsets, profile.RawcodeOffset);
            var rawcode = memory.ReadUInt32(rawcodeAddress);
            if (neutral)
            {
                // 중앙에서 성장 중인 특별함 유닛은 중립 소유다. 어느 플레이어 것인지 판별할 수단이 없으므로
                // 후보로만 모아 두고, 판 전체에 하나뿐일 때만 로컬 패로 인정한다.
                if (isGrowthUnit(rawcode))
                    neutralGrowth[rawcode] = neutralGrowth.GetValueOrDefault(rawcode) + 1;
                continue;
            }
            ownedObjects++;
            counts[rawcode] = counts.GetValueOrDefault(rawcode) + 1;
        }

        var countAfter = memory.ReadInt32(countAddress);
        var entriesAfter = profile.EntriesAreInline ? entriesAddress : memory.ReadUInt64(entriesAddress);
        if (countAfter != countBefore || entriesAfter != entriesBefore)
            throw new SnapshotChangedException();
        return new MemoryUnitSnapshot(countBefore, ownedObjects, duplicatePointers, counts, neutralGrowth);
    }

    private static ulong FollowObjectFieldPath(ReadOnlyProcessMemory memory, ulong objectAddress,
        IReadOnlyList<int> pointerOffsets, int fieldOffset)
    {
        var address = objectAddress;
        foreach (var pointerOffset in pointerOffsets)
        {
            address = memory.ReadUInt64(AddressMath.Add(address, pointerOffset));
            if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(address))
                throw new InvalidDataException($"객체 필드 포인터가 비정상입니다: 0x{address:X}");
        }
        return AddressMath.Add(address, fieldOffset);
    }

    internal static Process? FindNewestProcess(string name)
    {
        var processes = Process.GetProcessesByName(name);
        Process? selected = null;
        long selectedStart = long.MinValue;
        foreach (var process in processes)
        {
            try
            {
                var start = process.StartTime.ToUniversalTime().Ticks;
                if (start <= selectedStart) continue;
                selected = process;
                selectedStart = start;
            }
            catch { process.Dispose(); }
        }
        foreach (var process in processes)
            if (!ReferenceEquals(process, selected)) process.Dispose();
        return selected;
    }

    private static RecognitionResult Failure(RecognitionState state, string status, string detail,
        RecognitionDiagnostics? diagnostics = null, string profileSource = "") => new()
    {
        State = state,
        Status = status,
        Diagnostics = diagnostics is null
            ? new RecognitionDiagnostics { Source = "WarcraftMemory", ProfileSource = profileSource, Detail = detail }
            : new RecognitionDiagnostics
            {
                Source = diagnostics.Source,
                ProcessVersion = diagnostics.ProcessVersion,
                ProcessId = diagnostics.ProcessId,
                ProfileId = diagnostics.ProfileId,
                ProfileRevision = diagnostics.ProfileRevision,
                ProfileSource = diagnostics.ProfileSource,
                Detail = detail
            }
    };

    private static RecognitionDiagnostics WithProfile(RecognitionDiagnostics source, MemoryProfile profile) => new()
    {
        Source = source.Source,
        ProcessId = source.ProcessId,
        ProcessVersion = source.ProcessVersion,
        ProfileSource = source.ProfileSource,
        ProfileId = profile.ProfileId,
        ProfileRevision = profile.ProfileRevision
    };

    private sealed record LocatorCache(LocatorCacheKey Key, ulong Address);
    private sealed record LocatorCacheKey(int ProcessId, long Started, ulong ModuleBase, string ProfileId,
        int Revision, long ProfileGeneration);
    private sealed record MemoryUnitSnapshot(int ListCount, int OwnedObjects, int DuplicatePointers,
        Dictionary<uint, int> RawcodeCounts, Dictionary<uint, int> NeutralGrowthCounts);
    private sealed class SnapshotChangedException : InvalidOperationException;
}

internal static class AddressMath
{
    public static ulong Add(ulong address, long offset)
    {
        if (offset >= 0) return checked(address + (ulong)offset);
        return checked(address - (ulong)(-offset));
    }
}

internal sealed class SignaturePattern(byte?[] bytes)
{
    public static SignaturePattern Parse(string text) => new(text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x is "?" or "??" ? (byte?)null : Convert.ToByte(x, 16)).ToArray());

    public List<int> Find(byte[] source, int maximumMatches = int.MaxValue)
    {
        var result = new List<int>();
        for (var start = 0; start <= source.Length - bytes.Length; start++)
        {
            var found = true;
            for (var index = 0; index < bytes.Length; index++)
                if (bytes[index] is byte expected && source[start + index] != expected) { found = false; break; }
            if (!found) continue;
            result.Add(start);
            if (result.Count >= maximumMatches) break;
        }
        return result;
    }
}

internal sealed class ReadOnlyProcessMemory : IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private readonly SafeProcessHandle _handle;

    private ReadOnlyProcessMemory(SafeProcessHandle handle) => _handle = handle;

    public static ReadOnlyProcessMemory Open(int processId)
    {
        var handle = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "프로세스를 읽기 전용으로 열 수 없습니다.");
        return new ReadOnlyProcessMemory(handle);
    }

    public static bool IsPlausibleUserAddress(ulong address) => address is >= 0x10000 and <= 0x00007FFFFFFFFFFF;

    public byte[] Read(ulong address, int count)
    {
        if (count < 0 || count > 512 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(count));
        if (count > 0 && !IsPlausibleUserAddress(address)) throw new InvalidDataException($"비정상 읽기 주소: 0x{address:X}");
        var bytes = new byte[count];
        if (!ReadProcessMemory(_handle, (nint)address, bytes, count, out var read) || read.ToInt64() != count)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"메모리 읽기 실패: 0x{address:X}");
        return bytes;
    }

    public byte[] ReadAvailable(ulong address, int count)
    {
        if (count <= 0) return [];
        if (count > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(count));
        if (!IsPlausibleUserAddress(address)) return [];
        var bytes = new byte[count];
        var ok = ReadProcessMemory(_handle, (nint)address, bytes, count, out var read);
        var actual = Math.Clamp(read.ToInt64(), 0, count);
        if (!ok && actual == 0) return [];
        if (actual == count) return bytes;
        Array.Resize(ref bytes, (int)actual);
        return bytes;
    }

    /// <summary>
    /// 구조 스캔이 훑을 영역. 유닛 객체와 풀 배열은 모두 힙에 있으므로 전용(private) 커밋 영역만 본다.
    /// 매핑된 이미지·데이터 파일을 빼면 훑을 양이 크게 줄어든다.
    /// </summary>
    public IEnumerable<MemoryRegion> ReadableRegions() => ReadablePrivateRegions();

    /// <summary>
    /// 영역을 겹침 있는 청크로 나눠 읽는다. 일부 페이지를 못 읽어도 영역 전체를 버리지 않는다.
    /// 겹침은 구조체가 청크 경계에 걸려 누락되는 것을 막는다.
    /// </summary>
    public IEnumerable<(ulong Base, byte[] Buffer)> ReadChunks(IEnumerable<MemoryRegion> regions,
        int chunkBytes = 16 * 1024 * 1024, int overlap = 0x2000)
    {
        foreach (var region in regions)
        {
            ulong position = 0;
            while (position < region.Size)
            {
                var length = (int)Math.Min((ulong)chunkBytes, region.Size - position);
                var address = region.BaseAddress + position;
                var buffer = ReadAvailable(address, length);
                if (buffer.Length >= 0x1000) yield return (address, buffer);
                position += (ulong)Math.Max(length - overlap, 0x1000);
            }
        }
    }

    public IEnumerable<MemoryRegion> ReadablePrivateRegions()
    {
        ulong address = 0x10000;
        while (address < 0x00007FFFFFFFFFFF)
        {
            var queried = VirtualQueryEx(_handle, (nint)address, out var info, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
            if (queried == 0) yield break;
            var baseAddress = (ulong)info.BaseAddress.ToInt64();
            var size = info.RegionSize.ToUInt64();
            if (size == 0) yield break;
            if (info.State == 0x1000 && info.Type == 0x20000 && IsReadable(info.Protect) && IsPlausibleUserAddress(baseAddress))
                yield return new MemoryRegion(baseAddress, size);
            var next = baseAddress + size;
            if (next <= address) yield break;
            address = next;
        }
    }

    private static bool IsReadable(uint protect)
    {
        if ((protect & 0x100) != 0 || (protect & 0x01) != 0) return false;
        return (protect & 0xFF) is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;
    }

    public byte ReadByte(ulong address) => Read(address, 1)[0];
    public int ReadInt32(ulong address) => BitConverter.ToInt32(Read(address, 4));
    public uint ReadUInt32(ulong address) => BitConverter.ToUInt32(Read(address, 4));
    public ulong ReadUInt64(ulong address) => BitConverter.ToUInt64(Read(address, 8));
    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, nint baseAddress, byte[] buffer, int size, out nint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address,
        out MEMORY_BASIC_INFORMATION buffer, nuint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}

internal readonly record struct MemoryRegion(ulong BaseAddress, ulong Size);
