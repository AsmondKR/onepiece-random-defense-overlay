namespace OrandOverlay;

/// <summary>
/// 클리어 정산: 현재 인식된 유닛(필드 배치 포함 — 클리어 화면에서 로컬 소유로
/// 전부 잡힘)을 티어별로 세어 "몇 전설을 짰는지" 요약한다(유저 요청).
/// </summary>
public static class SettlementReport
{
    private static readonly string[] TierOrder =
        ["초월", "불멸", "영원", "제한됨", "신비함", "왜곡됨", "변화된", "히든", "전설", "세라핌"];

    public static string Build(DataCatalog catalog, IEnumerable<InventoryEntry> inventory)
    {
        var groups = inventory
            .Where(entry => entry.Count > 0)
            .Select(entry => (Unit: catalog.Unit(entry.UnitId), entry.Count))
            .Where(pair => TierOrder.Contains(BaseTier(pair.Unit.Tier), StringComparer.Ordinal))
            .GroupBy(pair => BaseTier(pair.Unit.Tier), StringComparer.Ordinal)
            .OrderBy(group => Array.IndexOf(TierOrder, group.Key))
            .ToList();
        if (groups.Count == 0)
            return "정산할 상위·전설급 유닛이 없습니다.\n클리어 직후 결과 화면에서 눌러주세요.";
        var totalLegends = 0;
        var totalHiddens = 0;
        var lines = groups.Select(group =>
        {
            var groupLegends = 0;
            var groupHiddens = 0;
            foreach (var pair in group)
            {
                var (legends, hiddens) = LegendEquivalent(catalog, pair.Unit);
                groupLegends += legends * pair.Count;
                groupHiddens += hiddens * pair.Count;
            }
            totalLegends += groupLegends;
            totalHiddens += groupHiddens;
            var equivalent = group.Key is "전설" or "히든" || groupLegends + groupHiddens == 0
                ? ""
                : $" ({FormatEquivalent(groupLegends, groupHiddens)})";
            return $"{group.Key} {group.Sum(pair => pair.Count)}기{equivalent} · " + string.Join(", ", group
                .OrderBy(pair => pair.Unit.Name, StringComparer.CurrentCulture)
                .Select(pair => pair.Count > 1 ? $"{pair.Unit.Name} ×{pair.Count}" : pair.Unit.Name));
        }).ToList();
        var total = groups.Sum(group => group.Sum(pair => pair.Count));
        return string.Join("\n", lines) +
               $"\n합계 {total}기 · 전설 환산 {FormatEquivalent(totalLegends, totalHiddens)}";
    }

    /// <summary>
    /// 커뮤니티 셈법 "몇 전 짰냐": 유닛의 조합 트리에서 전설·히든 티어 노드 수를 센다.
    /// 전설·히든 자신은 1로 치고, 상위·왜곡·세라핌 등은 트리를 내려가며 합산한다.
    /// </summary>
    public static (int Legends, int Hiddens) LegendEquivalent(DataCatalog catalog, UnitDefinition unit)
    {
        var baseTier = BaseTier(unit.Tier);
        if (baseTier == "전설") return (1, 0);
        if (baseTier == "히든") return (0, 1);
        var legends = 0;
        var hiddens = 0;
        Walk(unit, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return (legends, hiddens);

        void Walk(UnitDefinition current, int multiplier, HashSet<string> visiting)
        {
            foreach (var (childId, count) in current.Recipe)
            {
                if (count <= 0) continue;
                var child = catalog.Unit(childId);
                var childTier = BaseTier(child.Tier);
                if (childTier == "전설") { legends += multiplier * count; continue; }
                if (childTier == "히든") { hiddens += multiplier * count; continue; }
                if (!visiting.Add(child.Id)) continue;
                Walk(child, multiplier * count, visiting);
                visiting.Remove(child.Id);
            }
        }
    }

    private static string FormatEquivalent(int legends, int hiddens) =>
        hiddens > 0 ? $"{legends}전 {hiddens}히" : $"{legends}전";

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();
}
