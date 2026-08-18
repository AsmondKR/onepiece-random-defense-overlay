using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OrandOverlay;

/// <summary>
/// Read-only bridge for the single, independently verified TMO/Warcraft build below.
/// It uses TMO only to locate/decrypt live Warcraft roots, then reads Warcraft directly.
/// No process write, remote call, injection, hook, or stale inventory cache is used.
/// </summary>
public sealed class TmoAssistedMemoryRecognitionService : IInventoryRecognizer
{
    private const string SupportedWarcraftVersion = "2.0.4.23745";
    private const string SupportedWarcraftSha256 = "682C12552CA05E43C5FED2340EA132D3B06FE068E676DB7D1F5623D8D4633229";
    private const string SupportedTmoVersion = "1.0.0.0";
    private const string SupportedTmoSha256 = "AC0103AD641A5E88CEE0FFD7A7862584E5E55F8203BFC50A9265B0C207A4FA68";

    private const ulong TmoWar3VmtRva = 0x459788;
    private const ulong WrapperWarcraftPidOffset = 0x08;
    private const ulong WrapperWarcraftBaseOffset = 0x20;
    private const ulong WrapperGeneratorStateOffset = 0x120;
    private const ulong WrapperGeneratorCodeOffset = 0x128;

    private const ulong PoolCountOffset = 0xB98;
    private const ulong PoolPointerOffset = 0xBA0;
    private const int MaximumPoolObjects = 0x1FFF;
    private const ulong UnitRawcodeOffset = 0x178;
    private const ulong UnitOwnerOffset = 0x1C0;
    private const ulong UnitFirstAbilityHandleOffset = 0x558;
    private const ulong UnitInventoryOffset = 0x5A0;
    private const uint LocalControllerRawcode = 0x48304334; // canonical H0C4, memory/TMO text 4C0H
    private const int MaximumAbilityChain = 512;

    // 객체당 개별 ReadProcessMemory(틱당 수천 syscall)가 게임과 커널 시간을 다투지 않게,
    // vftable(+0)~인벤토리 포인터(+0x5A0)까지를 주소순 클러스터 몇 번으로 한꺼번에 읽는다.
    private const int UnitBlockBytes = 0x5A8;
    private const ulong UnitClusterGap = 0x40000;
    private const int UnitClusterMaxBytes = 8 * 1024 * 1024;

    private readonly RawcodeUnitMap _unitMap;
    private readonly object _bindingGate = new();
    private BindingCache? _bindingCache;
    private DateTime _nextWrapperSearchUtc;

    public TmoAssistedMemoryRecognitionService(DataCatalog catalog) => _unitMap = new RawcodeUnitMap(catalog);

    public Task<RecognitionResult> RecognizeAsync(AppSettings settings, CancellationToken cancellationToken) =>
        Task.Run(() => Recognize(cancellationToken), cancellationToken);

    private RecognitionResult Recognize(CancellationToken token)
    {
        // 워크·티모지지 모두 같은 이름의 프로세스가 여러 개일 수 있다(런처 잔존·재시작,
        // 티모지지는 런처/렌더러/리더 다중 프로세스). 전부 열거해 두고 리더가 실제로
        // 붙은 조합을 따라간다.
        var gameCandidates = System.Diagnostics.Process.GetProcessesByName("Warcraft III")
            .OrderByDescending(StartTimeOrMin)
            .ToList();
        var tmoCandidates = System.Diagnostics.Process.GetProcessesByName("TMO.GG")
            .OrderByDescending(StartTimeOrMin)
            .ToList();
        try
        {
            return RecognizeCore(gameCandidates, tmoCandidates, token);
        }
        finally
        {
            foreach (var process in gameCandidates) process.Dispose();
            foreach (var process in tmoCandidates) process.Dispose();
        }
    }

    private static DateTime StartTimeOrMin(System.Diagnostics.Process process)
    {
        try { return process.StartTime; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return DateTime.MinValue;
        }
    }

    private RecognitionResult RecognizeCore(
        IReadOnlyList<System.Diagnostics.Process> gameCandidates,
        IReadOnlyList<System.Diagnostics.Process> tmoCandidates, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            if (_unitMap.Error is not null)
                return Failure(RecognitionState.ConfigurationError, "유닛 코드 데이터 오류 · 기존 패 유지", _unitMap.Error);

            if (gameCandidates.Count == 0)
                return Failure(RecognitionState.Waiting, "워크 미실행 · 패 초기화",
                    "Warcraft III 프로세스를 기다리는 중입니다.", confirmsSessionBoundary: true);
            if (tmoCandidates.Count == 0)
                return Failure(RecognitionState.Waiting, "티모지지 미실행 · 패 초기화",
                    "TMO.GG를 실행하면 읽기 전용 라이브 연동을 재개합니다.");

            // 티모지지는 런처·렌더러·리더 등 같은 이름의 프로세스를 여러 개 띄운다.
            // "최신 1개"만 스캔하면 리더가 아닌 프로세스를 붙잡아 래퍼를 영영 못 찾는
            // 환경이 있다(외부 유저 실측) — 리더 래퍼가 발견되는 프로세스가 나올 때까지
            // 전부 조사한다. 캐시된 바인딩이 있으면 그 프로세스는 스캔 없이 통과한다.
            int? cachedTmoPid = null;
            int? cachedGamePid = null;
            lock (_bindingGate)
                if (_bindingCache is { } cachedBinding)
                {
                    cachedTmoPid = cachedBinding.Key.TmoPid;
                    cachedGamePid = cachedBinding.Key.GamePid;
                }
            System.Diagnostics.Process? tmoProcess = null;
            ProcessModule? tmoModule = null;
            ReadOnlyProcessMemory? tmoMemoryFound = null;
            HashSet<int>? readerPids = null;
            var scannedTmoCount = 0;
            var openFailureCount = 0;
            var throttled = false;
            string? tmoExePathSeen = null;
            foreach (var candidate in tmoCandidates.OrderByDescending(item => item.Id == cachedTmoPid))
            {
                token.ThrowIfCancellationRequested();
                ProcessModule? module;
                try { module = candidate.MainModule; }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    // 상승된(관리자) 프로세스는 모듈 조회부터 막힌다 — 권한 문제로 집계.
                    if (exception is Win32Exception) openFailureCount++;
                    continue;
                }
                if (module is null) continue;
                tmoExePathSeen ??= module.FileName;
                ReadOnlyProcessMemory memory;
                try { memory = ReadOnlyProcessMemory.Open(candidate.Id); }
                catch (Win32Exception)
                {
                    openFailureCount++;
                    continue;
                }
                HashSet<int> pids;
                if (candidate.Id == cachedTmoPid && cachedGamePid is { } knownGamePid)
                    pids = [knownGamePid];
                else
                {
                    // 미연동 상태의 전 프로세스 전체 스캔은 무겁다 — 2.5초에 1회로 제한.
                    if (DateTime.UtcNow < _nextWrapperSearchUtc)
                    {
                        memory.Dispose();
                        throttled = true;
                        break;
                    }
                    scannedTmoCount++;
                    try { pids = FindWrapperTargetPids(memory, (ulong)module.BaseAddress.ToInt64(), token); }
                    catch
                    {
                        memory.Dispose();
                        throw;
                    }
                }
                if (pids.Count > 0)
                {
                    tmoProcess = candidate;
                    tmoModule = module;
                    tmoMemoryFound = memory;
                    readerPids = pids;
                    break;
                }
                memory.Dispose();
            }
            if (scannedTmoCount > 0 && readerPids is null)
                _nextWrapperSearchUtc = DateTime.UtcNow.AddSeconds(2.5);
            if (tmoProcess is null || tmoModule is null || tmoMemoryFound is null || readerPids is null)
            {
                // 원인 구분이 다음 스크린샷 한 장으로 끝나도록, 티모지지 버전 불일치·권한
                // 실패·미발견을 다른 문구로 보여준다(같은 "연동 대기"로 뭉치면 원격 진단 불가).
                if (!throttled && scannedTmoCount > 0 &&
                    TmoIdentityMismatch(tmoExePathSeen) is { } mismatch)
                    return Failure(RecognitionState.Unsupported, "티모지지 버전 미지원 · 기존 패 유지",
                        $"티모지지 실행 파일이 검증된 버전과 다릅니다({mismatch}) — 티모지지가 업데이트된 것으로" +
                        " 보입니다. 오버레이가 새 티모지지 버전을 지원하도록 업데이트될 때까지 연동이 불가합니다.");
                string waitingDetail;
                if (throttled)
                    waitingDetail = "리더 래퍼 재탐색 대기 중입니다.";
                else if (openFailureCount > 0 && scannedTmoCount == 0)
                    waitingDetail =
                        $"TMO 프로세스 {tmoCandidates.Count}개 전부 메모리 열기 실패(권한 부족) — " +
                        "티모지지나 워크가 관리자 권한으로 실행 중입니다. 이 앱도 관리자 권한으로 실행해야 연동됩니다.";
                else
                    waitingDetail =
                        $"TMO 프로세스 {tmoCandidates.Count}개 조사(권한 실패 {openFailureCount}건) — 리더 래퍼 미발견. " +
                        "원랜디 판에 들어가 게임 화면에 티모지지 자체 오버레이가 뜨는지 확인해 보세요. " +
                        "안 뜨면 티모지지가 워크를 인식하지 못한 상태라 티모지지 재시작이 필요합니다.";
                return Failure(RecognitionState.Waiting,
                    "티모지지-워크 연동 대기 · 원랜디 입장 시 자동 연결", waitingDetail);
            }
            using var tmoMemory = tmoMemoryFound;
            var tmoVersion = tmoModule.FileVersionInfo.FileVersion ?? "unknown";
            var tmoBase = (ulong)tmoModule.BaseAddress.ToInt64();
            var gameProcess = gameCandidates.FirstOrDefault(process => readerPids.Contains(process.Id))
                              ?? gameCandidates[0];

            var gameModule = gameProcess.MainModule
                ?? throw new InvalidOperationException("Warcraft III 메인 모듈을 확인할 수 없습니다.");
            var gameVersion = gameModule.FileVersionInfo.FileVersion ?? "unknown";
            var baseDiagnostics = Diagnostics(gameProcess.Id, gameVersion,
                $"TMO {tmoVersion} · PID {tmoProcess.Id} (프로세스 {tmoCandidates.Count}개) · " +
                $"리더 대상 pid [{string.Join(",", readerPids)}] · 워크 후보 {gameCandidates.Count}개");

            if (!gameVersion.Equals(SupportedWarcraftVersion, StringComparison.OrdinalIgnoreCase))
                return Failure(RecognitionState.Unsupported, $"워크 {gameVersion} 미지원 · 기존 패 유지",
                    $"검증된 {SupportedWarcraftVersion} 빌드만 허용합니다.", baseDiagnostics);
            if (!tmoVersion.Equals(SupportedTmoVersion, StringComparison.OrdinalIgnoreCase))
                return Failure(RecognitionState.Unsupported, $"티모지지 {tmoVersion} 미지원 · 기존 패 유지",
                    $"검증된 TMO {SupportedTmoVersion} 빌드만 허용합니다.", baseDiagnostics);

            var gameBase = (ulong)gameModule.BaseAddress.ToInt64();
            var gameImageSize = checked((ulong)gameModule.ModuleMemorySize);
            var key = new BindingKey(tmoProcess.Id, tmoProcess.StartTime.ToUniversalTime().Ticks, tmoBase,
                gameProcess.Id, gameProcess.StartTime.ToUniversalTime().Ticks, gameBase);

            using var gameMemory = ReadOnlyProcessMemory.Open(gameProcess.Id);
            var binding = GetOrFindBinding(key, tmoModule.FileName, gameModule.FileName,
                tmoMemory, gameMemory, gameImageSize, token);

            // Binding addresses are cached; decoded roots and inventory contents never are.
            var roots = ReadLiveRoots(tmoMemory, gameMemory, binding, gameBase);
            if (roots.GameUi == 0)
                return new RecognitionResult
                {
                    Entries = [],
                    State = RecognitionState.Waiting,
                    ConfirmsSessionBoundary = true,
                    Status = "게임 진입 대기 · 패 초기화",
                    Diagnostics = WithDetail(baseDiagnostics,
                        $"TMO 래퍼 0x{binding.Wrapper:X} 검증 · GameUI=0 · 캐시 반환 안 함")
                };
            if (roots.WorldFrame == 0)
                return new RecognitionResult
                {
                    Entries = [],
                    State = RecognitionState.Waiting,
                    ConfirmsSessionBoundary = true,
                    Status = "월드 로딩 대기 · 패 초기화",
                    Diagnostics = WithDetail(baseDiagnostics, "WorldFrame=0 · 캐시 반환 안 함")
                };
            // 웨이브 중에는 적 유닛이 계속 생기고 죽어 스냅샷 경합이 잦다. 다음 틱으로
            // 미루는 대신 같은 틱 안에서 몇 번 더 시도해 조합 반영 지연을 줄인다.
            var unitClassCache = new Dictionary<ulong, bool>();
            var inventoryClassCache = new Dictionary<ulong, bool>();
            UnitSnapshot? snapshot = null;
            for (var attempt = 0; snapshot is null; attempt++)
            {
                try
                {
                    snapshot = ReadStableSnapshot(gameMemory, gameBase, gameImageSize, roots.WorldFrame,
                        unitClassCache, inventoryClassCache, token);
                    EnsureRootsUnchanged(tmoMemory, gameMemory, binding, gameBase, roots);
                }
                catch (SnapshotChangedException)
                {
                    if (attempt >= 2) throw;
                    token.ThrowIfCancellationRequested();
                    roots = ReadLiveRoots(tmoMemory, gameMemory, binding, gameBase);
                    if (roots.GameUi == 0 || roots.WorldFrame == 0) throw;
                }
            }

            // A player's CUnit set includes map-owned controllers/helpers in addition to cards.
            // The supplemental TMO catalog is the card boundary; keep noncatalog objects in
            // diagnostics, but never feed them into recommendations or reject an otherwise
            // structurally valid snapshot because of them.
            var cardCounts = snapshot.RawcodeCounts
                .Where(pair => _unitMap.IsRecognizedCard(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var ignoredCounts = snapshot.RawcodeCounts
                .Where(pair => !_unitMap.IsRecognizedCard(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var mapped = _unitMap.Map(cardCounts);
            var entries = mapped.Entries.ToList();
            if (snapshot.GreenBloodKnown && snapshot.HasGreenBlood)
                entries.Add(new InventoryEntry
                {
                    UnitId = "item_greenblood",
                    Count = 1,
                    Confidence = 1
                });
            var specialCount = snapshot.GreenBloodKnown && snapshot.HasGreenBlood ? 1 : 0;
            var ignoredObjects = ignoredCounts.Values.Sum();
            var ignoredRawcodes = ignoredCounts.Keys
                .Select(RawcodeCodec.Format)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            var diagnostics = new RecognitionDiagnostics
            {
                Source = "TmoAssistedWarcraftMemory",
                ProcessId = gameProcess.Id,
                ProcessVersion = gameVersion,
                ProfileId = "tmo-reforged6-2.0.4.23745",
                ProfileRevision = 1,
                ProfileSource = $"TMO {tmoVersion} read-only root bridge",
                ResolvedListAddress = $"0x{snapshot.PoolPointer:X}",
                ObservedObjects = snapshot.TotalRawcodes,
                MappedObjects = mapped.KnownCount + mapped.CatalogNamedCount + specialCount,
                UnknownObjects = ignoredObjects,
                UnknownRawcodes = ignoredRawcodes,
                Detail = $"WorldFrame 0x{roots.WorldFrame:X} · 로컬 슬롯 {snapshot.LocalPlayerId} · " +
                         $"pool {snapshot.PoolCount} · CUnit {snapshot.CUnitCount} · 로컬 {snapshot.LocalUnitCount} · " +
                         $"카드 {entries.Sum(x => x.Count)} · 내부/미등록 제외 {ignoredObjects} · " +
                         $"그린블러드 {(snapshot.GreenBloodKnown ? (snapshot.HasGreenBlood ? "보유" : "없음") : "확인불가")} · " +
                         $"인벤토리 {snapshot.InventoryItemCount} · 중복 {snapshot.DuplicatePointers} · 무효 rawcode {snapshot.InvalidRawcodes} · " +
                         $"스캔 {stopwatch.ElapsedMilliseconds}ms"
            };
            if (snapshot.PoolPointer == 0)
                return new RecognitionResult
                {
                    Entries = [],
                    State = RecognitionState.Waiting,
                    Status = "유닛 목록 초기화 대기 · 패 초기화",
                    Diagnostics = diagnostics
                };

            // A live GameUI + WorldFrame and a stable, validated pool are authoritative even when
            // the player owns zero cards. Ready+empty must clear a previously observed last card.
            if (entries.Count == 0)
                return new RecognitionResult
                {
                    Entries = [],
                    State = RecognitionState.Ready,
                    Status = $"실시간 패 0장 · {DateTime.Now:HH:mm:ss}",
                    Diagnostics = diagnostics
                };
            return new RecognitionResult
            {
                Entries = entries,
                State = RecognitionState.Ready,
                Status = $"실시간 패 {entries.Sum(x => x.Count)}장 · {DateTime.Now:HH:mm:ss}",
                Diagnostics = diagnostics
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (BindingNotReadyException exception)
        {
            // 티모지지 리더 래퍼는 원랜디 판에 입장해야 생성된다 — 로비·메뉴에서는
            // 이 대기 상태가 정상이라, 오류처럼 읽히지 않게 행동 안내를 담는다.
            // 여기 도달했다면 캐시가 가리키던 래퍼도 죽은 것이라 비워서, 다음 틱이
            // 캐시 프로세스에 갇히지 않고 전 프로세스를 다시 조사하게 한다.
            lock (_bindingGate) _bindingCache = null;
            return Failure(RecognitionState.Waiting,
                "티모지지-워크 연동 대기 · 원랜디 입장 시 자동 연결",
                exception.Message +
                " | 원랜디 판에 입장해도 계속 뜨면: 티모지지에서 워크 연동을 확인하고, 워크를 관리자 권한으로 실행했다면 이 앱도 관리자 권한으로 실행해 보세요.");
        }
        catch (ExecutableIdentityException exception)
        {
            lock (_bindingGate) _bindingCache = null;
            return Failure(RecognitionState.UnverifiedProfile, "실행 파일 검증 실패 · 기존 패 유지", exception.Message);
        }
        catch (SnapshotChangedException exception)
        {
            // Snapshot races do not invalidate the already verified wrapper locator.
            return Failure(RecognitionState.TransientReadError, "메모리 스냅샷 변경 · 기존 패 유지",
                exception.Message);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException or InvalidOperationException
                                          or OverflowException or IOException or UnauthorizedAccessException
                                          or ArgumentException or CryptographicException)
        {
            lock (_bindingGate) _bindingCache = null;
            return Failure(RecognitionState.TransientReadError, "연동 메모리 읽기 실패 · 기존 패 유지",
                exception.Message);
        }
    }

    private BindingCache GetOrFindBinding(BindingKey key, string tmoPath, string gamePath,
        ReadOnlyProcessMemory tmoMemory, ReadOnlyProcessMemory gameMemory, ulong gameImageSize,
        CancellationToken token)
    {
        lock (_bindingGate)
            if (_bindingCache is { } cached && cached.Key == key &&
                ValidateBinding(cached, tmoMemory, key, out _))
                return cached;

        VerifyExecutable(tmoPath, SupportedTmoSha256, "TMO.GG.exe");
        VerifyExecutable(gamePath, SupportedWarcraftSha256, "Warcraft III.exe");
        var expectedVmt = checked(key.TmoBase + TmoWar3VmtRva);
        var matches = new List<BindingCache>();
        var seen = new HashSet<ulong>();

        foreach (var region in tmoMemory.ReadablePrivateRegions())
        {
            token.ThrowIfCancellationRequested();
            const int chunkSize = 2 * 1024 * 1024;
            ulong offset = 0;
            while (offset < region.Size)
            {
                token.ThrowIfCancellationRequested();
                var nominal = (int)Math.Min((ulong)chunkSize, region.Size - offset);
                var readCount = (int)Math.Min((ulong)nominal + 7UL, region.Size - offset);
                var chunkAddress = checked(region.BaseAddress + offset);
                var bytes = tmoMemory.ReadAvailable(chunkAddress, readCount);
                if (bytes.Length >= sizeof(ulong))
                {
                    var target = BitConverter.GetBytes(expectedVmt);
                    var searchStart = 0;
                    while (searchStart <= bytes.Length - target.Length)
                    {
                        var relative = bytes.AsSpan(searchStart).IndexOf(target);
                        if (relative < 0) break;
                        var index = searchStart + relative;
                        if (index < nominal)
                        {
                            var candidate = checked(chunkAddress + (ulong)index);
                            if (seen.Add(candidate) && TryCreateBinding(candidate, key, tmoMemory, out var binding))
                                matches.Add(binding);
                        }
                        searchStart = index + 1;
                    }
                }
                offset += (ulong)nominal;
            }
        }

        if (matches.Count == 0)
            throw new BindingNotReadyException("검증된 TWar3Reforged6 라이브 객체를 아직 찾지 못했습니다.");
        if (matches.Count != 1)
            throw new InvalidDataException($"검증된 TWar3Reforged6 객체가 {matches.Count}개라 연동을 차단했습니다.");

        // Touch a fixed game-image byte and image bounds before accepting the cross-process binding.
        if (gameImageSize == 0 || gameImageSize > 1024UL * 1024 * 1024 ||
            gameMemory.ReadAvailable(key.GameBase, 2).Length != 2)
            throw new InvalidDataException("Warcraft 이미지 범위를 검증할 수 없습니다.");
        lock (_bindingGate) _bindingCache = matches[0];
        return matches[0];
    }

    /// <summary>
    /// 티모지지 리더 래퍼(vtable 일치 후보)가 가리키는 워크 pid 집합을 가볍게 수집한다.
    /// 전체 검증 전 단계라 vtable 일치 + pid 필드 읽기까지만 수행한다.
    /// </summary>
    private static HashSet<int> FindWrapperTargetPids(ReadOnlyProcessMemory tmoMemory, ulong tmoBase,
        CancellationToken token)
    {
        var pids = new HashSet<int>();
        var expectedVmt = checked(tmoBase + TmoWar3VmtRva);
        var target = BitConverter.GetBytes(expectedVmt);
        foreach (var region in tmoMemory.ReadablePrivateRegions())
        {
            token.ThrowIfCancellationRequested();
            const int chunkSize = 2 * 1024 * 1024;
            ulong offset = 0;
            while (offset < region.Size)
            {
                token.ThrowIfCancellationRequested();
                var nominal = (int)Math.Min((ulong)chunkSize, region.Size - offset);
                var readCount = (int)Math.Min((ulong)nominal + 7UL, region.Size - offset);
                var chunkAddress = checked(region.BaseAddress + offset);
                var bytes = tmoMemory.ReadAvailable(chunkAddress, readCount);
                if (bytes.Length >= sizeof(ulong))
                {
                    var searchStart = 0;
                    while (searchStart <= bytes.Length - target.Length)
                    {
                        var relative = bytes.AsSpan(searchStart).IndexOf(target);
                        if (relative < 0) break;
                        var index = searchStart + relative;
                        if (index < nominal)
                        {
                            try
                            {
                                pids.Add(tmoMemory.ReadInt32(
                                    checked(chunkAddress + (ulong)index + WrapperWarcraftPidOffset)));
                            }
                            catch (Exception exception) when (
                                exception is Win32Exception or InvalidDataException or OverflowException)
                            {
                                // 후보가 진짜 래퍼가 아닐 수 있다 — pid 읽기 실패는 무시.
                            }
                        }
                        searchStart = index + 1;
                    }
                }
                offset += (ulong)nominal;
            }
        }
        return pids;
    }

    private static bool TryCreateBinding(ulong wrapper, BindingKey key, ReadOnlyProcessMemory tmoMemory,
        out BindingCache binding)
    {
        binding = default!;
        try
        {
            var expectedVmt = checked(key.TmoBase + TmoWar3VmtRva);
            if (tmoMemory.ReadUInt64(wrapper) != expectedVmt) return false;
            if (tmoMemory.ReadInt32(checked(wrapper + WrapperWarcraftPidOffset)) != key.GamePid) return false;
            if (tmoMemory.ReadUInt64(checked(wrapper + WrapperWarcraftBaseOffset)) != key.GameBase) return false;
            var generatorState = tmoMemory.ReadUInt64(checked(wrapper + WrapperGeneratorStateOffset));
            var generatorCode = tmoMemory.ReadUInt64(checked(wrapper + WrapperGeneratorCodeOffset));
            if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(generatorState) ||
                !ReadOnlyProcessMemory.IsPlausibleUserAddress(generatorCode)) return false;
            var code = tmoMemory.Read(generatorCode, 0x80);
            if (!TmoWarcraftDecoder.TryParseGeneratedDecoder(code, out var metadata) ||
                metadata.StateAddress != generatorState) return false;
            _ = tmoMemory.ReadUInt64(checked(generatorState + 0x30));
            binding = new BindingCache(key, wrapper, generatorState, generatorCode);
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException or OverflowException)
        {
            return false;
        }
    }

    private static bool ValidateBinding(BindingCache binding, ReadOnlyProcessMemory memory, BindingKey key,
        out GeneratedDecoderMetadata generator)
    {
        generator = default;
        try
        {
            if (memory.ReadUInt64(binding.Wrapper) != checked(key.TmoBase + TmoWar3VmtRva) ||
                memory.ReadInt32(checked(binding.Wrapper + WrapperWarcraftPidOffset)) != key.GamePid ||
                memory.ReadUInt64(checked(binding.Wrapper + WrapperWarcraftBaseOffset)) != key.GameBase ||
                memory.ReadUInt64(checked(binding.Wrapper + WrapperGeneratorStateOffset)) != binding.GeneratorState ||
                memory.ReadUInt64(checked(binding.Wrapper + WrapperGeneratorCodeOffset)) != binding.GeneratorCode)
                return false;
            return TmoWarcraftDecoder.TryParseGeneratedDecoder(memory.Read(binding.GeneratorCode, 0x80), out generator)
                   && generator.StateAddress == binding.GeneratorState;
        }
        catch { return false; }
    }

    private static LiveRoots ReadLiveRoots(ReadOnlyProcessMemory tmoMemory, ReadOnlyProcessMemory gameMemory,
        BindingCache binding, ulong gameBase)
    {
        var code = tmoMemory.Read(binding.GeneratorCode, 0x80);
        if (!TmoWarcraftDecoder.TryParseGeneratedDecoder(code, out var generator) ||
            generator.StateAddress != binding.GeneratorState)
            throw new InvalidDataException("TMO 생성 디코더 서명이 실행 중 변경되었습니다.");
        var stateValue = tmoMemory.ReadUInt64(checked(binding.GeneratorState + 0x30));
        var gameUiKeys = TmoWarcraftDecoder.DeriveKeys(TmoWarcraftDecoder.GameUiSeed1,
            TmoWarcraftDecoder.GameUiSeed2, stateValue, generator.XorMask);
        var gameUi = TmoWarcraftDecoder.DecodeGameUi(gameMemory.ReadUInt64, gameBase, gameUiKeys);
        if (gameUi != 0 && !ReadOnlyProcessMemory.IsPlausibleUserAddress(gameUi))
            throw new InvalidDataException($"복호화된 GameUI 주소가 비정상입니다: 0x{gameUi:X}");
        if (gameUi == 0) return new LiveRoots(stateValue, generator.XorMask, 0, 0);

        var worldKeys = TmoWarcraftDecoder.DeriveKeys(TmoWarcraftDecoder.WorldFrameSeed1,
            TmoWarcraftDecoder.WorldFrameSeed2, stateValue, generator.XorMask);
        var worldFrame = TmoWarcraftDecoder.DecodeWorldFrame(gameMemory.ReadUInt64, gameBase, gameUi, worldKeys);
        if (worldFrame != 0 && !ReadOnlyProcessMemory.IsPlausibleUserAddress(worldFrame))
            throw new InvalidDataException($"복호화된 WorldFrame 주소가 비정상입니다: 0x{worldFrame:X}");
        return new LiveRoots(stateValue, generator.XorMask, gameUi, worldFrame);
    }

    private static void EnsureRootsUnchanged(ReadOnlyProcessMemory tmoMemory, ReadOnlyProcessMemory gameMemory,
        BindingCache binding, ulong gameBase, LiveRoots before)
    {
        var after = ReadLiveRoots(tmoMemory, gameMemory, binding, gameBase);
        if (after != before)
            throw new SnapshotChangedException("decoder state/GameUI/WorldFrame가 열거 중 변경되었습니다.");
    }

    private static UnitSnapshot ReadStableSnapshot(ReadOnlyProcessMemory memory, ulong gameBase,
        ulong gameImageSize, ulong worldFrame, Dictionary<ulong, bool> unitClassCache,
        Dictionary<ulong, bool> inventoryClassCache, CancellationToken token)
    {
        try
        {
            // 연속 두 패스의 "로컬 플레이어 데이터"가 일치할 때만 발행한다. 적 유닛의
            // 생성·사망은 로컬 패와 무관하므로 비교 대상이 아니다 — 웨이브 중에도
            // 반영이 밀리지 않는다. 불일치하면 마지막 패스를 기준으로 이어서 재시도한다.
            var previous = ReadSnapshot(memory, gameBase, gameImageSize, worldFrame,
                unitClassCache, inventoryClassCache, token);
            for (var pass = 0; pass < 3; pass++)
            {
                var current = ReadSnapshot(memory, gameBase, gameImageSize, worldFrame,
                    unitClassCache, inventoryClassCache, token);
                if (SameSnapshot(previous, current))
                {
                    // Green Blood is an optional map-specific state. A transient ability-list race
                    // must never hold back an otherwise authoritative card snapshot. Publish the
                    // special state only when both independent probes agree; otherwise keep normal
                    // cards Ready and mark only Green Blood as unknown.
                    var greenBlood = TmoGreenBloodProbe.Combine(
                        ToGreenBloodState(previous.GreenBloodKnown, previous.HasGreenBlood),
                        ToGreenBloodState(current.GreenBloodKnown, current.HasGreenBlood));
                    return current with
                    {
                        GreenBloodKnown = greenBlood != GreenBloodProbeState.Unknown,
                        HasGreenBlood = greenBlood == GreenBloodProbeState.Held
                    };
                }
                previous = current;
            }
            throw new SnapshotChangedException("연속 두 메모리 스냅샷이 일치하지 않습니다.");
        }
        catch (SnapshotChangedException) { throw; }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException or OverflowException)
        {
            throw new SnapshotChangedException("스냅샷 구조 읽기 실패: " + exception.Message);
        }
    }

    private static bool SameSnapshot(UnitSnapshot left, UnitSnapshot right) =>
        left.LocalPlayerRoot == right.LocalPlayerRoot && left.LocalPlayerId == right.LocalPlayerId &&
        left.LocalUnitCount == right.LocalUnitCount &&
        left.InventoryItemCount == right.InventoryItemCount &&
        left.StructuralFingerprint.AsSpan().SequenceEqual(right.StructuralFingerprint) &&
        left.RawcodeCounts.Count == right.RawcodeCounts.Count &&
        left.RawcodeCounts.All(pair => right.RawcodeCounts.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static UnitSnapshot ReadSnapshot(ReadOnlyProcessMemory memory, ulong gameBase, ulong gameImageSize,
        ulong worldFrame, Dictionary<ulong, bool> unitClassCache, Dictionary<ulong, bool> inventoryClassCache,
        CancellationToken token)
    {
        var localPlayerBefore = ReadLocalPlayer(memory, gameBase);
        var countAddress = checked(worldFrame + PoolCountOffset);
        var pointerAddress = checked(worldFrame + PoolPointerOffset);
        var countBefore = memory.ReadInt32(countAddress);
        if (countBefore < 0 || countBefore > MaximumPoolObjects)
            throw new InvalidDataException($"generic pool 개수 {countBefore}가 검증 범위를 벗어났습니다.");
        var poolBefore = memory.ReadUInt64(pointerAddress);
        if (poolBefore == 0 && countBefore == 0)
        {
            var countAfterEmpty = memory.ReadInt32(countAddress);
            var poolAfterEmpty = memory.ReadUInt64(pointerAddress);
            var localAfterEmpty = ReadLocalPlayer(memory, gameBase);
            if (countAfterEmpty != 0 || poolAfterEmpty != 0 || localAfterEmpty != localPlayerBefore)
                throw new SnapshotChangedException("빈 pool/local player가 읽는 중 변경되었습니다.");
            return new UnitSnapshot(0, 0, localPlayerBefore.Root, localPlayerBefore.Id, 0, 0, 0, 0, 0, 0,
                false, false, [], []);
        }
        if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(poolBefore))
            throw new SnapshotChangedException($"generic pool 주소가 비정상입니다: 0x{poolBefore:X}");

        var pointers = memory.Read(poolBefore, checked(countBefore * sizeof(ulong)));
        var seen = new HashSet<ulong>();
        var counts = new Dictionary<uint, int>();
        var fingerprint = new List<ulong>();
        var cUnits = 0;
        var localUnits = 0;
        var inventoryItems = 0;
        var duplicatePointers = 0;
        var invalidRawcodes = 0;
        var controllerUnits = new List<ulong>();

        var addresses = new List<ulong>(countBefore);
        for (var index = 0; index < countBefore; index++)
        {
            var unit = BitConverter.ToUInt64(pointers, index * sizeof(ulong));
            if (unit == 0) continue;
            if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(unit))
                throw new SnapshotChangedException($"pool의 nonzero 객체 포인터가 비정상입니다: 0x{unit:X}");
            if (!seen.Add(unit)) { duplicatePointers++; continue; }
            addresses.Add(unit);
        }
        var blocks = ReadUnitBlocks(memory, addresses, token);

        foreach (var unit in addresses)
        {
            token.ThrowIfCancellationRequested();
            ulong vftable;
            var hasBlock = blocks.TryGetValue(unit, out var block);
            if (hasBlock)
                vftable = BitConverter.ToUInt64(block.Buffer, block.Offset);
            else
            {
                // 클러스터에 담기지 못한 객체(읽기 불가 페이지 인접 등)는 vftable만 먼저 확인해,
                // CUnit이 아니면 큰 블록 읽기 실패를 스냅샷 무효 사유로 만들지 않는다.
                var head = memory.ReadAvailable(unit, sizeof(ulong));
                if (head.Length < sizeof(ulong))
                    throw new SnapshotChangedException($"객체 0x{unit:X} vftable을 읽지 못했습니다.");
                vftable = BitConverter.ToUInt64(head, 0);
            }
            if (!IsExactMsvcClassVftable(memory, gameBase, gameImageSize, vftable, ".?AVCUnit@@", unitClassCache))
                continue;
            cUnits++;
            if (!hasBlock)
            {
                var isolated = memory.ReadAvailable(unit, UnitBlockBytes);
                if (isolated.Length < UnitBlockBytes)
                    throw new SnapshotChangedException($"CUnit 0x{unit:X} 블록을 읽지 못했습니다.");
                block = (isolated, 0);
            }
            var owner = block.Buffer[block.Offset + (int)UnitOwnerOffset];
            if (owner != localPlayerBefore.Id) continue;
            localUnits++;
            fingerprint.Add(0x4355_4E49_5400_0001UL); // CUNIT + record version
            fingerprint.Add(unit);
            fingerprint.Add(owner);

            var rawcode = BitConverter.ToUInt32(block.Buffer, block.Offset + (int)UnitRawcodeOffset);
            fingerprint.Add(rawcode);
            if (!IsPrintableRawcode(rawcode))
            {
                invalidRawcodes++;
                throw new SnapshotChangedException($"local CUnit 0x{unit:X} rawcode가 비정상입니다: 0x{rawcode:X8}");
            }
            Add(counts, rawcode);
            if (rawcode == LocalControllerRawcode) controllerUnits.Add(unit);

            var inventory = BitConverter.ToUInt64(block.Buffer, block.Offset + (int)UnitInventoryOffset);
            fingerprint.Add(inventory);
            if (inventory == 0) continue;
            if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(inventory))
                throw new SnapshotChangedException($"CUnit 0x{unit:X} inventory 포인터가 비정상입니다.");
            if (!HasExactMsvcClass(memory, gameBase, gameImageSize, inventory, ".?AVCAbilityInventory@@",
                    inventoryClassCache))
            {
                fingerprint.Add(0x4E4F_5F49_4E56_454EUL); // NO_INVEN
                continue;
            }
            var slotCount = memory.ReadUInt32(checked(inventory + 0xD0));
            fingerprint.Add(slotCount);
            if (slotCount > 6)
                throw new SnapshotChangedException($"inventory slotCount {slotCount}가 범위를 벗어났습니다.");
            for (var slotIndex = 0U; slotIndex < slotCount; slotIndex++)
            {
                var handle = memory.ReadUInt64(checked(inventory + 0xD4 + 12UL * slotIndex));
                fingerprint.Add(handle);
                // Reforged uses both zero and all-bits-one for an empty inventory slot.
                // The latter is a stable sentinel, not a handle-table lookup failure.
                if (TmoInventoryHandle.IsEmpty(handle)) continue;
                if (!TryResolveHandle(memory, gameBase, handle, out var resolved))
                    throw new SnapshotChangedException($"inventory nonzero handle 0x{handle:X}을 해석하지 못했습니다.");
                fingerprint.Add(resolved);
                if (memory.ReadUInt64(checked(resolved + 0x30)) != 0)
                    throw new SnapshotChangedException($"inventory handle 0x{handle:X}의 상태 필드가 비정상입니다.");
                var linked = memory.ReadUInt64(checked(resolved + 0x90));
                if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(linked))
                    throw new SnapshotChangedException($"inventory handle 0x{handle:X}의 linked 포인터가 비정상입니다.");
                fingerprint.Add(linked);
                var itemRawcode = memory.ReadUInt32(checked(linked + 0x70));
                fingerprint.Add(itemRawcode);
                if (!IsPrintableRawcode(itemRawcode))
                {
                    invalidRawcodes++;
                    throw new SnapshotChangedException($"inventory item rawcode가 비정상입니다: 0x{itemRawcode:X8}");
                }
                Add(counts, itemRawcode);
                inventoryItems++;
            }
        }

        var greenBlood = controllerUnits.Count == 1
            ? ProbeGreenBlood(memory, gameBase, controllerUnits[0])
            : GreenBloodProbeState.Unknown;
        var greenBloodKnown = greenBlood != GreenBloodProbeState.Unknown;
        var hasGreenBlood = greenBlood == GreenBloodProbeState.Held;

        // 적 유닛 생성·사망으로 pool 개수는 항상 흔들릴 수 있다. 발행 안전성은 로컬
        // 데이터의 이중 패스 일치(SameSnapshot)가 보장하므로, 열거 도중 pool 버퍼
        // 자체가 재할당·이동한 경우만 이번 패스를 무효로 본다.
        var poolAfter = memory.ReadUInt64(pointerAddress);
        if (poolAfter != poolBefore)
            throw new SnapshotChangedException("pool pointer가 열거 중 변경되었습니다.");
        var localPlayerAfter = ReadLocalPlayer(memory, gameBase);
        if (localPlayerAfter != localPlayerBefore)
            throw new SnapshotChangedException("local player root/id가 열거 중 변경되었습니다.");
        return new UnitSnapshot(countBefore, poolBefore, localPlayerBefore.Root, localPlayerBefore.Id, cUnits,
            localUnits, inventoryItems, duplicatePointers, invalidRawcodes, counts.Values.Sum(),
            greenBloodKnown, hasGreenBlood, counts, fingerprint.ToArray());
    }

    /// <summary>
    /// 풀 객체 주소들을 정렬해 인접 묶음당 한 번의 큰 읽기로 필요한 필드 블록을 가져온다.
    /// 읽지 못한 구간의 객체는 결과에서 빠지며, 호출측이 개별 읽기로 폴백한다.
    /// </summary>
    private static Dictionary<ulong, (byte[] Buffer, int Offset)> ReadUnitBlocks(
        ReadOnlyProcessMemory memory, List<ulong> addresses, CancellationToken token)
    {
        var result = new Dictionary<ulong, (byte[] Buffer, int Offset)>(addresses.Count);
        if (addresses.Count == 0) return result;
        var sorted = new List<ulong>(addresses);
        sorted.Sort();
        var start = 0;
        while (start < sorted.Count)
        {
            token.ThrowIfCancellationRequested();
            var first = sorted[start];
            var end = checked(first + UnitBlockBytes);
            var last = start;
            while (last + 1 < sorted.Count)
            {
                var next = sorted[last + 1];
                var nextEnd = checked(next + UnitBlockBytes);
                if (next > end && next - end > UnitClusterGap) break;
                if (nextEnd - first > (ulong)UnitClusterMaxBytes) break;
                if (nextEnd > end) end = nextEnd;
                last++;
            }
            var buffer = memory.ReadAvailable(first, (int)(end - first));
            for (var index = start; index <= last; index++)
            {
                var offset = (long)(sorted[index] - first);
                if (offset + UnitBlockBytes <= buffer.Length)
                    result[sorted[index]] = (buffer, (int)offset);
            }
            start = last + 1;
        }
        return result;
    }

    private static GreenBloodProbeState ToGreenBloodState(bool known, bool held) =>
        !known ? GreenBloodProbeState.Unknown : held ? GreenBloodProbeState.Held : GreenBloodProbeState.Absent;

    private static GreenBloodProbeState ProbeGreenBlood(ReadOnlyProcessMemory memory, ulong gameBase, ulong unit)
    {
        try
        {
            var baselineFound = false;
            var heldFound = false;
            var seenHandles = new HashSet<ulong>();
            var seenAbilities = new HashSet<ulong>();
            var handle = memory.ReadUInt64(checked(unit + UnitFirstAbilityHandleOffset));
            for (var index = 0; index < MaximumAbilityChain; index++)
            {
                if (TmoInventoryHandle.IsEmpty(handle))
                    return baselineFound
                        ? heldFound ? GreenBloodProbeState.Held : GreenBloodProbeState.Absent
                        : GreenBloodProbeState.Unknown;
                if (!seenHandles.Add(handle) || !TryResolveHandle(memory, gameBase, handle, out var node))
                    return GreenBloodProbeState.Unknown;
                var ability = memory.ReadUInt64(checked(node + 0x90));
                if (ability == 0)
                    return baselineFound
                        ? heldFound ? GreenBloodProbeState.Held : GreenBloodProbeState.Absent
                        : GreenBloodProbeState.Unknown;
                if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(ability) || !seenAbilities.Add(ability))
                    return GreenBloodProbeState.Unknown;
                var id = memory.ReadUInt32(checked(ability + 0x70));
                var zeroBasedLevel = memory.ReadUInt32(checked(ability + 0x9C));
                if (!IsPrintableRawcode(id) || zeroBasedLevel > 1000)
                    return GreenBloodProbeState.Unknown;
                baselineFound |= id == TmoGreenBloodProbe.ControllerBaselineAbility;
                heldFound |= id == TmoGreenBloodProbe.HeldAbility;
                // Held can be accepted as soon as both positive sentinels are observed. Absent is
                // accepted only after a clean end-of-chain, so a partial traversal never becomes a
                // confident negative.
                if (baselineFound && heldFound) return GreenBloodProbeState.Held;
                handle = memory.ReadUInt64(checked(ability + 0x58));
            }
            return GreenBloodProbeState.Unknown;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException or
                                          OverflowException or SnapshotChangedException)
        {
            return GreenBloodProbeState.Unknown;
        }
    }

    private static LocalPlayerSnapshot ReadLocalPlayer(ReadOnlyProcessMemory memory, ulong gameBase)
    {
        var root = memory.ReadUInt64(checked(gameBase + 0x2B59964)) ^ 0x363ABBFD3DEFAEC9UL ^
                   memory.ReadUInt64(checked(gameBase + 0x2BD7BC8));
        if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(root))
            throw new InvalidDataException($"로컬 플레이어 root가 비정상입니다: 0x{root:X}");
        var id = BitConverter.ToUInt16(memory.Read(checked(root + 0x2664), 2));
        if (id > 27) throw new InvalidDataException($"로컬 플레이어 ID {id}가 범위를 벗어났습니다.");
        return new LocalPlayerSnapshot(root, id);
    }

    private static bool TryResolveHandle(ReadOnlyProcessMemory memory, ulong gameBase, ulong handle,
        out ulong resolved)
    {
        var root = memory.ReadUInt64(checked(gameBase + 0x2B808C0));
        return TmoHandleResolver.TryResolve(memory.ReadUInt64, memory.ReadUInt32, root, handle, out resolved);
    }

    private static bool HasExactMsvcClass(ReadOnlyProcessMemory memory, ulong imageBase, ulong imageSize,
        ulong objectAddress, string expected, IDictionary<ulong, bool> vftableCache)
    {
        ulong vftable;
        try
        {
            vftable = memory.ReadUInt64(objectAddress);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException)
        {
            throw new SnapshotChangedException("MSVC RTTI 구조가 읽는 중 변경되었습니다: " + exception.Message);
        }
        return IsExactMsvcClassVftable(memory, imageBase, imageSize, vftable, expected, vftableCache);
    }

    private static bool IsExactMsvcClassVftable(ReadOnlyProcessMemory memory, ulong imageBase, ulong imageSize,
        ulong vftable, string expected, IDictionary<ulong, bool> vftableCache)
    {
        try
        {
            if (vftableCache.TryGetValue(vftable, out var cached)) return cached;
            if (!InsideImage(vftable, imageBase, imageSize, 1) || vftable < 8)
            {
                vftableCache[vftable] = false;
                return false;
            }
            var locator = memory.ReadUInt64(vftable - 8);
            if (!InsideImage(locator, imageBase, imageSize, 0x18))
            {
                vftableCache[vftable] = false;
                return false;
            }
            var col = memory.Read(locator, 0x18);
            if (BitConverter.ToUInt32(col, 0) != 1)
            {
                vftableCache[vftable] = false;
                return false;
            }
            var typeRva = BitConverter.ToUInt32(col, 0x0C);
            var selfRva = BitConverter.ToUInt32(col, 0x14);
            var typeDescriptor = checked(imageBase + typeRva);
            if (!InsideImage(typeDescriptor, imageBase, imageSize, checked((ulong)expected.Length + 17)) ||
                checked(imageBase + selfRva) != locator)
            {
                vftableCache[vftable] = false;
                return false;
            }
            var actual = memory.Read(checked(typeDescriptor + 0x10), expected.Length + 1);
            var expectedBytes = Encoding.ASCII.GetBytes(expected);
            var result = actual[^1] == 0 && actual.AsSpan(0, expectedBytes.Length).SequenceEqual(expectedBytes);
            vftableCache[vftable] = result;
            return result;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException)
        {
            throw new SnapshotChangedException("MSVC RTTI 구조가 읽는 중 변경되었습니다: " + exception.Message);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool InsideImage(ulong address, ulong imageBase, ulong imageSize, ulong bytes)
    {
        if (address < imageBase || bytes > imageSize) return false;
        var offset = address - imageBase;
        return offset <= imageSize - bytes;
    }

    private static bool IsPrintableRawcode(uint rawcode) =>
        Enumerable.Range(0, 4).All(index => ((rawcode >> (index * 8)) & 0xFF) is >= 0x20 and <= 0x7E);

    private static void Add(IDictionary<uint, int> counts, uint rawcode) =>
        counts[rawcode] = counts.TryGetValue(rawcode, out var current) ? current + 1 : 1;

    private static void VerifyExecutable(string path, string expectedHash, string displayName)
    {
        var actual = TmoExecutableHashCache.Sha256(path);
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new ExecutableIdentityException($"{displayName} SHA-256 불일치: {actual[..12]}…");
    }

    /// <summary>리더 래퍼 미발견의 원인이 티모지지 버전 차이인지 확인한다(캐시된 해시 재사용).</summary>
    private static string? TmoIdentityMismatch(string? path)
    {
        if (path is null) return null;
        try
        {
            VerifyExecutable(path, SupportedTmoSha256, "TMO.GG.exe");
            return null;
        }
        catch (ExecutableIdentityException exception)
        {
            return exception.Message;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or CryptographicException or Win32Exception)
        {
            return null;
        }
    }

    private static RecognitionDiagnostics Diagnostics(int pid, string version, string detail) => new()
    {
        Source = "TmoAssistedWarcraftMemory",
        ProcessId = pid,
        ProcessVersion = version,
        ProfileId = "tmo-reforged6-2.0.4.23745",
        ProfileRevision = 1,
        ProfileSource = "strict executable hash + live generated decoder",
        Detail = detail
    };

    private static RecognitionDiagnostics WithDetail(RecognitionDiagnostics source, string detail) => new()
    {
        Source = source.Source,
        ProcessVersion = source.ProcessVersion,
        ProcessId = source.ProcessId,
        ProfileId = source.ProfileId,
        ProfileRevision = source.ProfileRevision,
        ProfileSource = source.ProfileSource,
        ResolvedListAddress = source.ResolvedListAddress,
        ObservedObjects = source.ObservedObjects,
        MappedObjects = source.MappedObjects,
        UnknownObjects = source.UnknownObjects,
        UnknownRawcodes = source.UnknownRawcodes,
        Detail = detail
    };

    private static RecognitionResult Failure(RecognitionState state, string status, string detail,
        RecognitionDiagnostics? diagnostics = null, bool confirmsSessionBoundary = false) => new()
    {
        Entries = [],
        State = state,
        ConfirmsSessionBoundary = confirmsSessionBoundary,
        Status = status,
        Diagnostics = diagnostics is null
            ? Diagnostics(0, "", detail)
            : WithDetail(diagnostics, detail)
    };

    private sealed record BindingCache(BindingKey Key, ulong Wrapper, ulong GeneratorState, ulong GeneratorCode);
    private sealed record BindingKey(int TmoPid, long TmoStarted, ulong TmoBase,
        int GamePid, long GameStarted, ulong GameBase);
    private sealed record LiveRoots(ulong GeneratorStateValue, ulong GeneratorXorMask, ulong GameUi, ulong WorldFrame);
    private sealed record LocalPlayerSnapshot(ulong Root, ushort Id);
    private sealed record UnitSnapshot(int PoolCount, ulong PoolPointer, ulong LocalPlayerRoot, ushort LocalPlayerId,
        int CUnitCount,
        int LocalUnitCount, int InventoryItemCount, int DuplicatePointers, int InvalidRawcodes, int TotalRawcodes,
        bool GreenBloodKnown, bool HasGreenBlood, Dictionary<uint, int> RawcodeCounts,
        ulong[] StructuralFingerprint);
    private sealed class SnapshotChangedException(string message) : InvalidOperationException(message);
    private sealed class BindingNotReadyException(string message) : InvalidOperationException(message);
    private sealed class ExecutableIdentityException(string message) : Exception(message);
}

internal static class TmoExecutableHashCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long Length, long Modified, string Hash)> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    public static string Sha256(string path)
    {
        var info = new FileInfo(path);
        lock (Gate)
            if (Entries.TryGetValue(path, out var cached) && cached.Length == info.Length &&
                cached.Modified == info.LastWriteTimeUtc.Ticks)
                return cached.Hash;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        lock (Gate) Entries[path] = (info.Length, info.LastWriteTimeUtc.Ticks, hash);
        return hash;
    }
}
