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

    private readonly RawcodeUnitMap _unitMap;
    private readonly object _bindingGate = new();
    private BindingCache? _bindingCache;

    public TmoAssistedMemoryRecognitionService(DataCatalog catalog) => _unitMap = new RawcodeUnitMap(catalog);

    public Task<RecognitionResult> RecognizeAsync(AppSettings settings, CancellationToken cancellationToken) =>
        Task.Run(() => Recognize(cancellationToken), cancellationToken);

    private RecognitionResult Recognize(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (_unitMap.Error is not null)
                return Failure(RecognitionState.ConfigurationError, "유닛 코드 데이터 오류 · 기존 패 유지", _unitMap.Error);

            using var gameProcess = WarcraftMemoryRecognitionService.FindNewestProcess("Warcraft III");
            if (gameProcess is null)
                return Failure(RecognitionState.Waiting, "워크 미실행 · 패 초기화",
                    "Warcraft III 프로세스를 기다리는 중입니다.", confirmsSessionBoundary: true);
            using var tmoProcess = WarcraftMemoryRecognitionService.FindNewestProcess("TMO.GG");
            if (tmoProcess is null)
                return Failure(RecognitionState.Waiting, "티모지지 미실행 · 패 초기화",
                    "TMO.GG를 실행하면 읽기 전용 라이브 연동을 재개합니다.");

            var tmoModule = tmoProcess.MainModule
                ?? throw new InvalidOperationException("TMO.GG 메인 모듈을 확인할 수 없습니다.");
            var gameModule = gameProcess.MainModule
                ?? throw new InvalidOperationException("Warcraft III 메인 모듈을 확인할 수 없습니다.");
            var tmoVersion = tmoModule.FileVersionInfo.FileVersion ?? "unknown";
            var gameVersion = gameModule.FileVersionInfo.FileVersion ?? "unknown";
            var baseDiagnostics = Diagnostics(gameProcess.Id, gameVersion,
                $"TMO {tmoVersion} · PID {tmoProcess.Id}");

            if (!gameVersion.Equals(SupportedWarcraftVersion, StringComparison.OrdinalIgnoreCase))
                return Failure(RecognitionState.Unsupported, $"워크 {gameVersion} 미지원 · 기존 패 유지",
                    $"검증된 {SupportedWarcraftVersion} 빌드만 허용합니다.", baseDiagnostics);
            if (!tmoVersion.Equals(SupportedTmoVersion, StringComparison.OrdinalIgnoreCase))
                return Failure(RecognitionState.Unsupported, $"티모지지 {tmoVersion} 미지원 · 기존 패 유지",
                    $"검증된 TMO {SupportedTmoVersion} 빌드만 허용합니다.", baseDiagnostics);

            var tmoBase = (ulong)tmoModule.BaseAddress.ToInt64();
            var gameBase = (ulong)gameModule.BaseAddress.ToInt64();
            var gameImageSize = checked((ulong)gameModule.ModuleMemorySize);
            var key = new BindingKey(tmoProcess.Id, tmoProcess.StartTime.ToUniversalTime().Ticks, tmoBase,
                gameProcess.Id, gameProcess.StartTime.ToUniversalTime().Ticks, gameBase);

            using var tmoMemory = ReadOnlyProcessMemory.Open(tmoProcess.Id);
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
            UnitSnapshot snapshot;
            try
            {
                snapshot = ReadStableSnapshot(gameMemory, gameBase, gameImageSize, roots.WorldFrame, token);
                EnsureRootsUnchanged(tmoMemory, gameMemory, binding, gameBase, roots);
            }
            catch (SnapshotChangedException)
            {
                token.ThrowIfCancellationRequested();
                roots = ReadLiveRoots(tmoMemory, gameMemory, binding, gameBase);
                if (roots.GameUi == 0 || roots.WorldFrame == 0) throw;
                snapshot = ReadStableSnapshot(gameMemory, gameBase, gameImageSize, roots.WorldFrame, token);
                EnsureRootsUnchanged(tmoMemory, gameMemory, binding, gameBase, roots);
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
                         $"인벤토리 {snapshot.InventoryItemCount} · 중복 {snapshot.DuplicatePointers} · 무효 rawcode {snapshot.InvalidRawcodes}"
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
            return Failure(RecognitionState.Waiting, "메모리 연동 초기화 대기 · 패 초기화", exception.Message);
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
        ulong gameImageSize, ulong worldFrame, CancellationToken token)
    {
        try
        {
            var first = ReadSnapshot(memory, gameBase, gameImageSize, worldFrame, token);
            var second = ReadSnapshot(memory, gameBase, gameImageSize, worldFrame, token);
            if (!SameSnapshot(first, second))
                throw new SnapshotChangedException("연속 두 메모리 스냅샷이 일치하지 않습니다.");
            // Green Blood is an optional map-specific state. A transient ability-list race must
            // never hold back an otherwise authoritative card snapshot. Publish the special state
            // only when both independent probes agree; otherwise keep normal cards Ready and mark
            // only Green Blood as unknown.
            var greenBlood = TmoGreenBloodProbe.Combine(
                ToGreenBloodState(first.GreenBloodKnown, first.HasGreenBlood),
                ToGreenBloodState(second.GreenBloodKnown, second.HasGreenBlood));
            return second with
            {
                GreenBloodKnown = greenBlood != GreenBloodProbeState.Unknown,
                HasGreenBlood = greenBlood == GreenBloodProbeState.Held
            };
        }
        catch (SnapshotChangedException) { throw; }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException or OverflowException)
        {
            throw new SnapshotChangedException("스냅샷 구조 읽기 실패: " + exception.Message);
        }
    }

    private static bool SameSnapshot(UnitSnapshot left, UnitSnapshot right) =>
        left.PoolCount == right.PoolCount && left.PoolPointer == right.PoolPointer &&
        left.LocalPlayerRoot == right.LocalPlayerRoot && left.LocalPlayerId == right.LocalPlayerId &&
        left.CUnitCount == right.CUnitCount && left.LocalUnitCount == right.LocalUnitCount &&
        left.InventoryItemCount == right.InventoryItemCount && left.DuplicatePointers == right.DuplicatePointers &&
        left.InvalidRawcodes == right.InvalidRawcodes && left.TotalRawcodes == right.TotalRawcodes &&
        left.PoolBytes.AsSpan().SequenceEqual(right.PoolBytes) &&
        left.StructuralFingerprint.AsSpan().SequenceEqual(right.StructuralFingerprint) &&
        left.RawcodeCounts.Count == right.RawcodeCounts.Count &&
        left.RawcodeCounts.All(pair => right.RawcodeCounts.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static UnitSnapshot ReadSnapshot(ReadOnlyProcessMemory memory, ulong gameBase, ulong gameImageSize,
        ulong worldFrame, CancellationToken token)
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
                false, false, [], [], []);
        }
        if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(poolBefore))
            throw new SnapshotChangedException($"generic pool 주소가 비정상입니다: 0x{poolBefore:X}");

        var pointers = memory.Read(poolBefore, checked(countBefore * sizeof(ulong)));
        var seen = new HashSet<ulong>();
        var counts = new Dictionary<uint, int>();
        var fingerprint = new List<ulong>();
        var unitClassCache = new Dictionary<ulong, bool>();
        var inventoryClassCache = new Dictionary<ulong, bool>();
        var cUnits = 0;
        var localUnits = 0;
        var inventoryItems = 0;
        var duplicatePointers = 0;
        var invalidRawcodes = 0;
        var controllerUnits = new List<ulong>();

        for (var index = 0; index < countBefore; index++)
        {
            token.ThrowIfCancellationRequested();
            var unit = BitConverter.ToUInt64(pointers, index * sizeof(ulong));
            if (unit == 0) continue;
            if (!ReadOnlyProcessMemory.IsPlausibleUserAddress(unit))
                throw new SnapshotChangedException($"pool의 nonzero 객체 포인터가 비정상입니다: 0x{unit:X}");
            if (!seen.Add(unit)) { duplicatePointers++; continue; }
            if (!HasExactMsvcClass(memory, gameBase, gameImageSize, unit, ".?AVCUnit@@", unitClassCache)) continue;
            cUnits++;
            var owner = memory.ReadByte(checked(unit + UnitOwnerOffset));
            fingerprint.Add(0x4355_4E49_5400_0001UL); // CUNIT + record version
            fingerprint.Add(unit);
            fingerprint.Add(owner);
            if (owner != localPlayerBefore.Id) continue;
            localUnits++;

            var rawcode = memory.ReadUInt32(checked(unit + UnitRawcodeOffset));
            fingerprint.Add(rawcode);
            if (!IsPrintableRawcode(rawcode))
            {
                invalidRawcodes++;
                throw new SnapshotChangedException($"local CUnit 0x{unit:X} rawcode가 비정상입니다: 0x{rawcode:X8}");
            }
            Add(counts, rawcode);
            if (rawcode == LocalControllerRawcode) controllerUnits.Add(unit);

            var inventory = memory.ReadUInt64(checked(unit + UnitInventoryOffset));
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

        var countAfter = memory.ReadInt32(countAddress);
        var poolAfter = memory.ReadUInt64(pointerAddress);
        if (countAfter != countBefore || poolAfter != poolBefore)
            throw new SnapshotChangedException("pool count/pointer가 열거 중 변경되었습니다.");
        var pointersAfter = memory.Read(poolAfter, checked(countAfter * sizeof(ulong)));
        var localPlayerAfter = ReadLocalPlayer(memory, gameBase);
        if (countAfter != countBefore || poolAfter != poolBefore || localPlayerAfter != localPlayerBefore)
            throw new SnapshotChangedException("local player root/id가 열거 중 변경되었습니다.");
        if (!pointers.AsSpan().SequenceEqual(pointersAfter))
            throw new SnapshotChangedException("pool pointer bytes가 열거 중 변경되었습니다.");
        return new UnitSnapshot(countBefore, poolBefore, localPlayerBefore.Root, localPlayerBefore.Id, cUnits,
            localUnits, inventoryItems, duplicatePointers, invalidRawcodes, counts.Values.Sum(),
            greenBloodKnown, hasGreenBlood, counts,
            pointersAfter, fingerprint.ToArray());
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
        try
        {
            var vftable = memory.ReadUInt64(objectAddress);
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
        bool GreenBloodKnown, bool HasGreenBlood, Dictionary<uint, int> RawcodeCounts, byte[] PoolBytes,
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
