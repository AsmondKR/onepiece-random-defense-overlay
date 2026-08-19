using System.IO;
using System.Text.Json;

namespace OrandOverlay;

/// <summary>자체 수집 플레이 통계 스냅샷. 없거나 깨져도 앱 동작에 영향이 없어야 한다.</summary>
public sealed class LiveStats
{
    public int TotalRecords { get; init; }
    public int LabeledRecords { get; init; }
    private readonly Dictionary<string, LiveGoalStats> _goals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _weights = new(StringComparer.OrdinalIgnoreCase);

    public static LiveStats Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new LiveStats();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1) return new LiveStats();
            var stats = new LiveStats
            {
                TotalRecords = root.GetProperty("totalRecords").GetInt32(),
                LabeledRecords = root.GetProperty("labeledRecords").GetInt32(),
            };
            foreach (var goal in root.GetProperty("goals").EnumerateObject())
                stats._goals[goal.Name] = new LiveGoalStats(
                    goal.Value.GetProperty("plays").GetInt32(),
                    goal.Value.GetProperty("labeled").GetInt32(),
                    goal.Value.GetProperty("clears").GetInt32());
            if (root.TryGetProperty("weights", out var weights))
                foreach (var entry in weights.EnumerateObject())
                    stats._weights[entry.Name] = Math.Clamp(entry.Value.GetDouble(), -0.1, 0.1);
            return stats;
        }
        catch { return new LiveStats(); }
    }

    public bool TryGetGoal(string goalId, out LiveGoalStats stats) => _goals.TryGetValue(goalId, out stats!);

    /// <summary>게이트를 통과한 유닛 가중(±0.1). 미등재면 0 — 아무 영향 없음.</summary>
    public double WeightFor(string unitId) => _weights.GetValueOrDefault(unitId);

    /// <summary>채용률 점수에 가중을 곱해 반영한다. 상한은 파싱 단계에서 이미 캡됐다.</summary>
    public static int ApplyWeight(int score, double weight) =>
        (int)Math.Round(score * (1 + weight), MidpointRounding.AwayFromZero);
}

public sealed record LiveGoalStats(int Plays, int Labeled, int Clears)
{
    /// <summary>확정 라벨이 있을 때만 클리어율을 말한다. 없으면 빈 문자열.</summary>
    public string ClearRateText =>
        Labeled > 0 ? $"클리어율 {(int)Math.Round(100.0 * Clears / Labeled)}%" : "";
}
