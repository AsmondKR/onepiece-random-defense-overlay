namespace OrandOverlay;

/// <summary>
/// 남은 조합을 실제 조합 트리 그대로 단계화한다(유저 요청): 최상위는 목표의 직계
/// 재료 단계(티어 높은 순 — 전설급 먼저), 각 단계를 펼치면 그 단계의 하위 조합
/// 단계가, 더 내려갈 단계가 없으면 재료 목록이 나온다. 같은 유닛이 여러 갈래에
/// 나오면 처음 등장한 자리에서 한 번만 보여준다.
/// </summary>
public static class BuildDrilldown
{
    public sealed record DrillNode(RecipeCraftStep Step, IReadOnlyList<DrillNode> Children,
        bool FlatChildren = false);

    // 희귀함부터는 더 중첩하지 않고, 펼치면 안흔함부터의 조합식을 한 번에 보여준다.
    private const int RareRank = 3;

    public static (IReadOnlyList<DrillNode> Legends, IReadOnlyList<RecipeCraftStep> Others)
        Build(Recommendation item)
    {
        var steps = item.RemainingCraftSteps;
        if (item.RecipeTree is null || steps.Count == 0)
            return ([], steps);
        var stepsByUnit = steps
            .GroupBy(step => step.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        var nested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = CollectTree(item.RecipeTree, stepsByUnit, nested)
            .OrderByDescending(node => TierRank(node.Step.Tier))
            .ToList();
        var others = steps.Where(step => !nested.Contains(step.UnitId)).ToList();
        return (groups, others);
    }

    private static List<DrillNode> CollectTree(RecipeTreeNode node,
        IReadOnlyDictionary<string, RecipeCraftStep> stepsByUnit, ISet<string> visited)
    {
        var result = new List<DrillNode>();
        foreach (var child in node.Children)
        {
            if (stepsByUnit.TryGetValue(child.UnitId, out var step) && visited.Add(child.UnitId))
            {
                var flat = TierRank(child.Tier) <= RareRank;
                var children = flat
                    ? CollectFlat(child, stepsByUnit, visited)
                    : CollectTree(child, stepsByUnit, visited);
                result.Add(new DrillNode(step, children, flat));
                continue;
            }
            // 조합 단계가 아닌 노드(최하위 재료·보유 완료)나 이미 표시한 유닛은
            // 건너뛰고 그 아래만 계속 살핀다.
            result.AddRange(CollectTree(child, stepsByUnit, visited));
        }
        return result;
    }

    // 하위 조합 단계 전체를 티어 오름차순(안흔함부터)으로 평탄화한다.
    private static List<DrillNode> CollectFlat(RecipeTreeNode node,
        IReadOnlyDictionary<string, RecipeCraftStep> stepsByUnit, ISet<string> visited)
    {
        var flat = new List<RecipeCraftStep>();
        CollectFlatSteps(node, stepsByUnit, visited, flat);
        return flat
            .OrderBy(step => TierRank(step.Tier))
            .ThenBy(step => step.Name, StringComparer.CurrentCulture)
            .Select(step => new DrillNode(step, []))
            .ToList();
    }

    private static void CollectFlatSteps(RecipeTreeNode node,
        IReadOnlyDictionary<string, RecipeCraftStep> stepsByUnit, ISet<string> visited,
        List<RecipeCraftStep> flat)
    {
        foreach (var child in node.Children)
        {
            if (stepsByUnit.TryGetValue(child.UnitId, out var step) && visited.Add(child.UnitId))
                flat.Add(step);
            CollectFlatSteps(child, stepsByUnit, visited, flat);
        }
    }

    private static int TierRank(string tier) => BaseTier(tier) switch
    {
        "왜곡됨" => 8,
        "변화된" => 7,
        "히든" => 6,
        "전설" => 5,
        "신비함" => 4,
        "희귀함" => 3,
        "특별함" => 2,
        "안흔함" => 1,
        _ => 0
    };

    public static bool IsLegendTier(string tier) => BaseTier(tier) is "전설" or "히든";

    public static bool IsRareTier(string tier) => BaseTier(tier) == "희귀함";

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();
}
