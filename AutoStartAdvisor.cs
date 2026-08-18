namespace OrandOverlay;

/// <summary>
/// 자동 시작 도우미. 상위·항법을 정하지 않고 시작한 판에서 첫 희귀함이 잡히면,
/// 그 희귀함이 재료로 들어가는 학습된 상위 중 신+ 표본이 가장 많은 것을 추천한다
/// (8라운드 전 희귀함 하나를 빨리 뽑는 패스트 유니크 흐름 — 유저 제안).
/// </summary>
public static class AutoStartAdvisor
{
    public sealed record Advice(UnitDefinition Goal, UnitDefinition Rare, long Samples);

    public static Advice? RecommendGoal(DataCatalog catalog, ClearBuildStats? stats,
        IEnumerable<string> ownedUnitIds)
    {
        if (stats is null || !stats.HasData) return null;
        var rares = ownedUnitIds
            .Select(catalog.Unit)
            .Where(unit => BaseTier(unit.Tier) == "희귀함")
            .DistinctBy(unit => unit.Id)
            .ToList();
        if (rares.Count == 0) return null;
        var tops = catalog.AllUnits
            .Where(unit => IsTopUnitTier(unit.Tier))
            .DistinctBy(unit => unit.Id)
            .ToList();
        Advice? best = null;
        foreach (var rare in rares)
        foreach (var top in tops)
        {
            if (!RequiresUnit(catalog, top, rare.Id)) continue;
            var samples = LearnedSelection.GoalSampleCount(stats, top);
            if (samples < ClearBuildStats.MinimumGoalSamples) continue;
            if (best is null || samples > best.Samples) best = new Advice(top, rare, samples);
        }
        return best;
    }

    /// <summary>목표의 레시피 트리에 해당 유닛이 재료로 포함되는지(재귀).</summary>
    public static bool RequiresUnit(DataCatalog catalog, UnitDefinition root, string unitId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Search(root);

        bool Search(UnitDefinition unit)
        {
            if (!visited.Add(unit.Id)) return false;
            foreach (var childId in unit.Recipe.Keys)
            {
                var child = catalog.Unit(childId);
                if (child.Id.Equals(unitId, StringComparison.OrdinalIgnoreCase)) return true;
                if (Search(child)) return true;
            }
            return false;
        }
    }

    private static bool IsTopUnitTier(string tier) => BaseTier(tier)
        is "신비함" or "초월" or "불멸" or "영원" or "제한됨";

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();
}
