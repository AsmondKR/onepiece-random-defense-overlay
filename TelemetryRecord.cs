using System.Text.Json.Serialization;

namespace OrandOverlay;

/// <summary>
/// 판 종료 시 서버로 보내는 익명 플레이 레코드(스키마 v1).
/// 내 슬롯 데이터만 담는다. 닉네임·계정·IP는 어떤 필드로도 존재하지 않는다.
/// 승패 라벨은 판정기가 확정하면 clear/fail, 아니면 unknown이다.
/// </summary>
public sealed class TelemetryRecord
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("recordId")] public string RecordId { get; init; } = "";
    [JsonPropertyName("anonId")] public string AnonId { get; init; } = "";
    [JsonPropertyName("capturedAt")] public string CapturedAt { get; init; } = "";
    [JsonPropertyName("appVersion")] public string AppVersion { get; init; } = "";
    [JsonPropertyName("mapVersion")] public string MapVersion { get; init; } = "";
    [JsonPropertyName("warcraftVersion")] public string WarcraftVersion { get; init; } = "";
    [JsonPropertyName("goalUnitId")] public string GoalUnitId { get; init; } = "";
    [JsonPropertyName("navigationMode")] public string NavigationMode { get; init; } = "";
    [JsonPropertyName("goroseiMode")] public string GoroseiMode { get; init; } = "";
    [JsonPropertyName("buildVariant")] public string BuildVariant { get; init; } = "";
    [JsonPropertyName("difficulty")] public string Difficulty { get; init; } = "unknown";
    [JsonPropertyName("finalHand")] public List<TelemetryHandEntry> FinalHand { get; init; } = [];
    [JsonPropertyName("completedTops")] public List<string> CompletedTops { get; init; } = [];
    [JsonPropertyName("topRecommendations")] public List<string> TopRecommendations { get; init; } = [];
    [JsonPropertyName("sessionStartedAt")] public string SessionStartedAt { get; init; } = "";
    [JsonPropertyName("sessionEndedAt")] public string SessionEndedAt { get; init; } = "";
    [JsonPropertyName("lastObservedUnitCount")] public int LastObservedUnitCount { get; init; }
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "unknown";
    [JsonPropertyName("outcomeSource")] public string OutcomeSource { get; init; } = "none";
}

public sealed class TelemetryHandEntry
{
    [JsonPropertyName("unitId")] public string UnitId { get; init; } = "";
    [JsonPropertyName("count")] public int Count { get; init; }
}

public static class MatchTelemetryRecorder
{
    /// <summary>세션 종료 시점 상태로 레코드를 만든다. 순수 함수 — I/O 없음.</summary>
    public static TelemetryRecord Build(string anonId, string appVersion, string mapVersion,
        string warcraftVersion, string goalUnitId, string navigationMode, string goroseiMode,
        string buildVariant, string difficulty, IReadOnlyList<InventoryEntry> finalHand,
        IReadOnlyList<string> completedTops, IReadOnlyList<string> topRecommendations,
        DateTimeOffset sessionStartedAt, DateTimeOffset sessionEndedAt, int lastObservedUnitCount,
        string outcome = "unknown", string outcomeSource = "none") => new()
    {
        RecordId = Guid.NewGuid().ToString(),
        AnonId = anonId,
        CapturedAt = DateTimeOffset.UtcNow.UtcDateTime.ToString("o"),
        AppVersion = appVersion,
        MapVersion = mapVersion,
        WarcraftVersion = warcraftVersion,
        GoalUnitId = goalUnitId,
        NavigationMode = navigationMode,
        GoroseiMode = goroseiMode,
        BuildVariant = buildVariant,
        Difficulty = string.IsNullOrWhiteSpace(difficulty) ? "unknown" : difficulty,
        FinalHand = finalHand.Where(x => x.Count > 0)
            .Select(x => new TelemetryHandEntry { UnitId = x.UnitId, Count = x.Count }).ToList(),
        CompletedTops = completedTops.ToList(),
        TopRecommendations = topRecommendations.Take(5).ToList(),
        SessionStartedAt = sessionStartedAt.UtcDateTime.ToString("o"),
        SessionEndedAt = sessionEndedAt.UtcDateTime.ToString("o"),
        LastObservedUnitCount = lastObservedUnitCount,
        Outcome = outcome,
        OutcomeSource = outcomeSource,
    };
}
