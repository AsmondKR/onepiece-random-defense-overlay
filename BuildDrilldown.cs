namespace OrandOverlay;

/// <summary>
/// 상위 목표의 남은 조합을 티어 사다리로 묶는다(유저 요청): 변화된·왜곡됨 → 전설·히든
/// → 희귀함 순으로 내려가며, 각 단계를 펼치면 다음 단계가, 마지막 단계를 펼치면 재료가
/// 나온다. 희귀함 목표(자동 시작 단계)는 특별함 단계를 묶는다. 사다리에 속하지 않는
/// 단계(목표 직계 특별·최종 조합 등)는 원래 순서대로 뒤에 붙는다.
/// </summary>
public static class BuildDrilldown
{
    public sealed record DrillNode(RecipeCraftStep Step, IReadOnlyList<DrillNode> Children);

    private static readonly string[][] TopLadder = [["변화된", "왜곡됨"], ["전설", "히든"], ["희귀함"]];
    private static readonly string[][] RareLadder = [["특별함"]];

    public static (IReadOnlyList<DrillNode> Legends, IReadOnlyList<RecipeCraftStep> Others)
        Build(Recommendation item)
    {
        var steps = item.RemainingCraftSteps;
        if (item.RecipeTree is null || steps.Count == 0)
            return ([], steps);
        var ladder = BaseTier(item.RecipeTree.Tier) == "희귀함" ? RareLadder : TopLadder;
        var stepsByUnit = steps
            .GroupBy(step => step.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        var nested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = Collect(item.RecipeTree, stepsByUnit, ladder, 0, nested,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var others = steps.Where(step => !nested.Contains(step.UnitId)).ToList();
        return (groups, others);
    }

    private static List<DrillNode> Collect(RecipeTreeNode node,
        IReadOnlyDictionary<string, RecipeCraftStep> stepsByUnit,
        string[][] ladder, int minLevel, ISet<string> nested, ISet<string> visited)
    {
        var result = new List<DrillNode>();
        foreach (var child in node.Children)
        {
            var level = LadderLevel(ladder, minLevel, child.Tier);
            if (level >= 0 && stepsByUnit.TryGetValue(child.UnitId, out var step))
            {
                if (visited.Add(child.UnitId))
                {
                    var children = level + 1 < ladder.Length
                        ? Collect(child, stepsByUnit, ladder, level + 1, nested, visited)
                        : [];
                    result.Add(new DrillNode(step, children));
                    nested.Add(child.UnitId);
                }
                continue;
            }
            result.AddRange(Collect(child, stepsByUnit, ladder, minLevel, nested, visited));
        }
        return result;
    }

    // 사다리에서 minLevel 이후 처음으로 이 티어가 속하는 단계. 없으면 -1.
    // 전설이 목표 직계인 경우처럼 상위 단계(변화된)를 건너뛰는 트리도 자연히 묶인다.
    private static int LadderLevel(string[][] ladder, int minLevel, string tier)
    {
        var baseTier = BaseTier(tier);
        for (var level = minLevel; level < ladder.Length; level++)
            if (ladder[level].Contains(baseTier, StringComparer.Ordinal))
                return level;
        return -1;
    }

    public static bool IsLegendTier(string tier) => BaseTier(tier) is "전설" or "히든";

    public static bool IsRareTier(string tier) => BaseTier(tier) == "희귀함";

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();
}
