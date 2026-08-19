namespace OrandOverlay;

/// <summary>
/// 남은 조합을 "선택적 조합법 보기"로 단계화한다(유저 요청).
///
/// 각 단계를 펼치면 그 단계 자신의 조합만 안흔함부터 오름차순으로 보여주고, 그 안의
/// 항목을 다시 펼치면 또 그 항목의 조합만 보여준다. 예전에는 같은 재료가 여러 갈래에
/// 필요할 때 먼저 나온 갈래가 가져가 버려서, 다른 갈래를 펼치면 재료가 비어 보였다.
/// 갈래마다 자기 재료를 온전히 갖는 편이 "이 유닛을 만들려면 무엇이 필요한가"라는
/// 질문에 정확히 답한다.
/// </summary>
public static class BuildDrilldown
{
    public sealed record DrillNode(RecipeCraftStep Step, IReadOnlyList<DrillNode> Children,
        bool FlatChildren = false);

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

        // 트리에서 유닛별 노드를 한 번만 잡아 둔다. 하위 조합을 펼칠 때 이 노드로 되돌아간다.
        var nodesByUnit = new Dictionary<string, RecipeTreeNode>(StringComparer.OrdinalIgnoreCase);
        IndexNodes(item.RecipeTree, nodesByUnit);

        // 최상위는 목표 바로 아래의 가장 바깥 단계들 — 티어 높은 순.
        // 목표 자신의 최종 조합은 카드 헤더가 이미 보여주므로 others로 남긴다.
        var top = TopSteps(item.RecipeTree, stepsByUnit)
            .Select(node => BuildNode(node, stepsByUnit, nodesByUnit, []))
            .OrderByDescending(node => TierOrder(node.Step.Tier))
            .ThenBy(node => node.Step.Name, StringComparer.CurrentCulture)
            .ToList();

        var shown = new HashSet<string>(top.Select(node => node.Step.UnitId), StringComparer.OrdinalIgnoreCase);
        foreach (var node in top) CollectShown(node, shown);
        var others = steps.Where(step => !shown.Contains(step.UnitId)).ToList();
        return (top, others);
    }

    /// <summary>티어 순서(흔함 0 → 왜곡됨 8). 오름차순 정렬에 쓴다.</summary>
    public static int TierOrder(string tier) => BaseTier(tier) switch
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

    private static void IndexNodes(RecipeTreeNode node, Dictionary<string, RecipeTreeNode> map)
    {
        map.TryAdd(node.UnitId, node);
        foreach (var child in node.Children) IndexNodes(child, map);
    }

    /// <summary>주어진 노드 아래에서 가장 바깥쪽 조합 단계들. 단계를 만나면 더 내려가지 않는다.</summary>
    private static List<RecipeTreeNode> TopSteps(RecipeTreeNode node,
        IReadOnlyDictionary<string, RecipeCraftStep> stepsByUnit, bool includeSelf = false)
    {
        if (includeSelf && stepsByUnit.ContainsKey(node.UnitId)) return [node];
        var result = new List<RecipeTreeNode>();
        foreach (var child in node.Children)
        {
            if (stepsByUnit.ContainsKey(child.UnitId)) result.Add(child);
            else result.AddRange(TopSteps(child, stepsByUnit));
        }
        return result;
    }

    /// <summary>
    /// 한 단계를 펼친 모습: 자기 하위 조합만 안흔함부터 담고, 각 항목은 다시 자기 조합을 갖는다.
    /// path는 같은 갈래에서의 순환(있을 수 없지만 데이터 오류 대비)을 막는다.
    /// </summary>
    private static DrillNode BuildNode(RecipeTreeNode node,
        IReadOnlyDictionary<string, RecipeCraftStep> stepsByUnit,
        IReadOnlyDictionary<string, RecipeTreeNode> nodesByUnit,
        HashSet<string> path)
    {
        var step = stepsByUnit[node.UnitId];
        if (!path.Add(node.UnitId)) return new DrillNode(step, []);

        var collected = new Dictionary<string, RecipeTreeNode>(StringComparer.OrdinalIgnoreCase);
        CollectSubtreeSteps(node, stepsByUnit, collected);
        var children = collected.Values
            .Select(child => BuildNode(child, stepsByUnit, nodesByUnit, path))
            .OrderBy(child => TierOrder(child.Step.Tier))
            .ThenBy(child => child.Step.Name, StringComparer.CurrentCulture)
            .ToList();

        path.Remove(node.UnitId);
        // 하위 항목을 다시 펼칠 수 있어야 하므로 평탄화 표시는 쓰지 않는다.
        return new DrillNode(step, children);
    }

    /// <summary>이 단계 아래의 모든 조합 단계(같은 유닛은 한 번만).</summary>
    private static void CollectSubtreeSteps(RecipeTreeNode node,
        IReadOnlyDictionary<string, RecipeCraftStep> stepsByUnit,
        Dictionary<string, RecipeTreeNode> collected)
    {
        foreach (var child in node.Children)
        {
            if (stepsByUnit.ContainsKey(child.UnitId)) collected.TryAdd(child.UnitId, child);
            CollectSubtreeSteps(child, stepsByUnit, collected);
        }
    }

    private static void CollectShown(DrillNode node, HashSet<string> shown)
    {
        foreach (var child in node.Children)
        {
            shown.Add(child.Step.UnitId);
            CollectShown(child, shown);
        }
    }

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();
}
