using System.IO;
using System.Text.Json;

namespace OrandOverlay;

/// <summary>
/// 실전 1판 라이브 검증 레코더.
/// 검증 모드가 켜져 있는 동안 자동 인식 결과의 패 변화를 이벤트로 분류해 대기(pending)로 띄우고,
/// 사용자가 게임 화면과 눈으로 대조해 [일치]/[불일치]를 누르면 expected-vs-observed JSONL 한 줄을 기록한다.
/// 불일치 시 오프라인 재현용 스냅샷 JSON을 함께 남긴다. 로그는 사용자 확인 없이는 절대 쓰지 않는다.
/// </summary>
public sealed class LiveVerificationRecorder
{
    public const string EventUnitAdded = "unit_added";
    public const string EventUnitSold = "unit_sold";
    public const string EventCombineCompleted = "combine_completed";
    public const string EventSessionReentry = "session_reentry";
    public const string EventSpotCheck = "spot_check";

    private static readonly string[] RequiredEvents =
        [EventUnitAdded, EventUnitSold, EventCombineCompleted, EventSessionReentry];

    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private Dictionary<string, int> _previous = new(StringComparer.Ordinal);
    private bool _hasPrevious;
    private bool _sessionBoundaryPending;
    private readonly Dictionary<string, int> _confirmedEvents = new(StringComparer.Ordinal);

    public LiveVerificationRecorder(string? logDirectory = null, Func<DateTimeOffset>? clock = null)
    {
        LogDirectory = logDirectory
                       ?? Environment.GetEnvironmentVariable("ORAND_VERIFY_DIR")
                       ?? Path.Combine(AppPaths.UserDataDirectory, "verification");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool Enabled { get; set; }
    public string LogDirectory { get; set; }
    public string LogFilePath => Path.Combine(LogDirectory, "live-verification.jsonl");
    public LiveVerificationPending? Pending { get; private set; }
    public int ConfirmedRows { get; private set; }
    public int MismatchCount { get; private set; }

    public string ChecklistSummary
    {
        get
        {
            lock (_gate)
            {
                var marks = string.Join(" ", RequiredEvents.Select(e =>
                    $"{KoreanEventName(e)}{(_confirmedEvents.GetValueOrDefault(e) > 0 ? "✓" : "✗")}"));
                return $"기록 {ConfirmedRows}/10 · {marks} · 불일치 {MismatchCount}";
            }
        }
    }

    public static string KoreanEventName(string eventTag) => eventTag switch
    {
        EventUnitAdded => "추가",
        EventUnitSold => "판매",
        EventCombineCompleted => "조합",
        EventSessionReentry => "재진입",
        EventSpotCheck => "수시대조",
        _ => eventTag
    };

    /// <summary>자동 인식 결과를 관찰해 패 변화 이벤트를 분류한다. 파일은 쓰지 않는다.</summary>
    public void Observe(RecognitionResult result)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            if (result.ShouldClearAutomaticInventory)
            {
                if (RecognitionPolicy.IsConfirmedOutOfGame(result))
                {
                    _sessionBoundaryPending = true;
                    _previous.Clear();
                    _hasPrevious = false;
                }
                return;
            }
            if (!result.ShouldReplaceInventory) return;

            var current = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in result.Entries)
                current[entry.UnitId] = current.GetValueOrDefault(entry.UnitId) + entry.Count;
            var total = current.Values.Sum();

            string? eventTag = null;
            if (_sessionBoundaryPending)
            {
                eventTag = EventSessionReentry;
                _sessionBoundaryPending = false;
            }
            else if (_hasPrevious)
            {
                var previousTotal = _previous.Values.Sum();
                var hasNewUnit = current.Keys.Any(key => !_previous.ContainsKey(key));
                if (total > previousTotal) eventTag = EventUnitAdded;
                else if (total < previousTotal) eventTag = hasNewUnit ? EventCombineCompleted : EventUnitSold;
                else if (hasNewUnit) eventTag = EventCombineCompleted;
            }

            _previous = current;
            _hasPrevious = true;
            if (eventTag is not null)
                Pending = BuildPending(eventTag, current, total, result);
        }
    }

    /// <summary>
    /// 사용자의 화면 대조 판정을 기록한다. 대기 이벤트가 없으면 마지막 인식 패로 수시대조(spot_check)를 기록한다.
    /// 기록할 관찰이 아예 없으면 null을 반환한다. 불일치면 재현용 스냅샷 파일도 남긴다.
    /// </summary>
    public string? Confirm(bool match, string? note = null)
    {
        lock (_gate)
        {
            var pending = Pending;
            if (pending is null)
            {
                if (!_hasPrevious) return null;
                pending = BuildPending(EventSpotCheck, _previous, _previous.Values.Sum(), null);
            }

            Directory.CreateDirectory(LogDirectory);
            var inventory = pending.Inventory
                .Select(pair => new { unitId = pair.Key, count = pair.Value })
                .OrderBy(x => x.unitId, StringComparer.Ordinal)
                .ToList();
            var row = new
            {
                timestamp = _clock().UtcDateTime.ToString("o"),
                @event = pending.EventTag,
                profileId = pending.ProfileId,
                buildVersion = pending.BuildVersion,
                expected = new
                {
                    note = match ? "사용자 화면 대조 — 일치 확인" : note ?? "사용자 화면 대조 — 불일치 보고",
                    inventory,
                    totalCount = pending.TotalCount
                },
                observed = new { inventory, totalCount = pending.TotalCount, state = pending.State },
                match
            };
            File.AppendAllText(LogFilePath,
                JsonSerializer.Serialize(row) + Environment.NewLine);

            ConfirmedRows++;
            _confirmedEvents[pending.EventTag] = _confirmedEvents.GetValueOrDefault(pending.EventTag) + 1;
            if (!match)
            {
                MismatchCount++;
                WriteMismatchSnapshot(pending, note);
            }
            Pending = null;
            return LogFilePath;
        }
    }

    private void WriteMismatchSnapshot(LiveVerificationPending pending, string? note)
    {
        var snapshot = new
        {
            capturedAt = _clock().UtcDateTime.ToString("o"),
            eventTag = pending.EventTag,
            profileId = pending.ProfileId,
            buildVersion = pending.BuildVersion,
            state = pending.State,
            note,
            observedInventory = pending.Inventory
                .Select(pair => new { unitId = pair.Key, count = pair.Value })
                .OrderBy(x => x.unitId, StringComparer.Ordinal),
            diagnostics = pending.DiagnosticsDetail
        };
        var name = $"mismatch-{_clock().UtcDateTime:yyyyMMdd-HHmmss-fff}.json";
        File.WriteAllText(Path.Combine(LogDirectory, name),
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }

    private LiveVerificationPending BuildPending(string eventTag, Dictionary<string, int> inventory, int total,
        RecognitionResult? result) => new(
        eventTag,
        new Dictionary<string, int>(inventory, StringComparer.Ordinal),
        total,
        result?.State.ToString() ?? "Ready",
        result?.Diagnostics?.ProfileId ?? _lastProfileId,
        result?.Diagnostics?.ProcessVersion ?? _lastBuildVersion,
        CacheDiagnostics(result));

    private string _lastProfileId = "";
    private string _lastBuildVersion = "";
    private string _lastDiagnosticsDetail = "";

    private string CacheDiagnostics(RecognitionResult? result)
    {
        if (result?.Diagnostics is { } diagnostics)
        {
            _lastProfileId = diagnostics.ProfileId;
            _lastBuildVersion = diagnostics.ProcessVersion;
            _lastDiagnosticsDetail = diagnostics.Detail;
        }
        return _lastDiagnosticsDetail;
    }
}

public sealed record LiveVerificationPending(
    string EventTag,
    IReadOnlyDictionary<string, int> Inventory,
    int TotalCount,
    string State,
    string ProfileId,
    string BuildVersion,
    string DiagnosticsDetail)
{
    public string Description =>
        $"{LiveVerificationRecorder.KoreanEventName(EventTag)} 감지 · 현재 {TotalCount}장 — 게임 화면과 대조 후 판정하세요";
}
