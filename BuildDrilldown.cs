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
        // 목표 티어에 따라 묶는 단계가 다르다: 상위 목표는 전설·히든 → 희귀함,
        // 희귀함 목표(자동 시작 단계)는 특별함 단계를 묶는다(유저 요청).
        var rareGoal = BaseTier(item.RecipeTree.Tier) == "희귀함";
        string[] groupTiers = rareGoal ? ["특별함"] : ["전설", "히든"];
        string[] childTiers = rareGoal ? [] : ["희귀함"];
        var stepsByUnit = steps
            .GroupBy(step => step.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        var legends = new List<LegendGroup>();
        var nested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectGroups(item.RecipeTree, stepsByUnit, groupTiers, childTiers, legends, nested,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var others = steps.Where(step => !nested.Contains(step.UnitId)).ToList();
        return (legends, others);
    }

    private static void CollectGroups(RecipeTreeNode node,
        IReadOnlyDictionary<string, RecipeCraftStep> stepsByUnit,
        string[] groupTiers, string[] childTiers,
        List<LegendGroup> legends, ISet<string> nested, ISet<string> visitedGroups)
    {
        foreach (var child in node.Children)
        {
            if (groupTiers.Contains(BaseTier(child.Tier), StringComparer.Ordinal) &&
                stepsByUnit.TryGetValue(child.UnitId, out var groupStep))
            {
                if (visitedGroups.Add(child.UnitId))
                {
                    var children = new List<RecipeCraftStep>();
                    if (childTiers.Length > 0)
                        CollectChildren(child, stepsByUnit, childTiers, children,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    legends.Add(new LegendGroup(groupStep, children));
                    nested.Add(child.UnitId);
                    foreach (var nestedChild in children) nested.Add(nestedChild.UnitId);
                }
                continue;
            }
            CollectGroups(child, stepsByUnit, groupTiers, childTiers, legends, nested, visitedGroups);
        }
    }

    private static void CollectChildren(RecipeTreeNode node,
        IReadOnlyDictionary<string, RecipeCraftStep> stepsByUnit,
        string[] childTiers, List<RecipeCraftStep> children, ISet<string> seen)
    {
        foreach (var child in node.Children)
        {
            if (childTiers.Contains(BaseTier(child.Tier), StringComparer.Ordinal) &&
                stepsByUnit.TryGetValue(child.UnitId, out var childStep))
            {
                if (seen.Add(child.UnitId)) children.Add(childStep);
                continue;
            }
            CollectChildren(child, stepsByUnit, childTiers, children, seen);
        }
    }

    public static bool IsLegendTier(string tier) => BaseTier(tier) is "전설" or "히든";

    public static bool IsRareTier(string tier) => BaseTier(tier) == "희귀함";

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();
}
