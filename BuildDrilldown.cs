namespace OrandOverlay;

/// <summary>
/// 상위 목표의 남은 조합을 티어 단계로 묶는다(유저 요청): 전설급(전설·히든)을 먼저
/// 리스트업하고, 전설을 펼치면 그 트리의 희귀함, 희귀함을 펼치면 재료가 나온다.
/// 전설급 트리에 속하지 않는 단계(목표 직계 희귀·특별·최종 조합 등)는 원래 순서대로
/// 뒤에 붙는다.
/// </summary>
public static class BuildDrilldown
{
    public sealed record LegendGroup(RecipeCraftStep Step, IReadOnlyList<RecipeCraftStep> Rares);

    public static (IReadOnlyList<LegendGroup> Legends, IReadOnlyList<RecipeCraftStep> Others)
        Build(Recommendation item)
    {
        var steps = item.RemainingCraftSteps;
        if (item.RecipeTree is null || steps.Count == 0)
            return ([], steps);
        var stepsByUnit = steps
            .GroupBy(step => step.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        var legends = new List<LegendGroup>();
        var nested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectLegends(item.RecipeTree, stepsByUnit, legends, nested,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var others = steps.Where(step => !nested.Contains(step.UnitId)).ToList();
        return (legends, others);
    }

    private static void CollectLegends(RecipeTreeNode node,
        IReadOnlyDictionary<string, RecipeCraftStep> stepsByUnit,
        List<LegendGroup> legends, ISet<string> nested, ISet<string> visitedLegends)
    {
        foreach (var child in node.Children)
        {
            if (IsLegendTier(child.Tier) &&
                stepsByUnit.TryGetValue(child.UnitId, out var legendStep))
            {
                if (visitedLegends.Add(child.UnitId))
                {
                    var rares = new List<RecipeCraftStep>();
                    CollectRares(child, stepsByUnit, rares,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    legends.Add(new LegendGroup(legendStep, rares));
                    nested.Add(child.UnitId);
                    foreach (var rare in rares) nested.Add(rare.UnitId);
                }
                continue;
            }
            CollectLegends(child, stepsByUnit, legends, nested, visitedLegends);
        }
    }

    private static void CollectRares(RecipeTreeNode node,
        IReadOnlyDictionary<string, RecipeCraftStep> stepsByUnit,
        List<RecipeCraftStep> rares, ISet<string> seen)
    {
        foreach (var child in node.Children)
        {
            if (IsRareTier(child.Tier) &&
                stepsByUnit.TryGetValue(child.UnitId, out var rareStep))
            {
                if (seen.Add(child.UnitId)) rares.Add(rareStep);
                continue;
            }
            CollectRares(child, stepsByUnit, rares, seen);
        }
    }

    public static bool IsLegendTier(string tier) => BaseTier(tier) is "전설" or "히든";

    public static bool IsRareTier(string tier) => BaseTier(tier) == "희귀함";

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();
}
