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
        var lines = groups.Select(group =>
            $"{group.Key} {group.Sum(pair => pair.Count)}기 · " + string.Join(", ", group
                .OrderBy(pair => pair.Unit.Name, StringComparer.CurrentCulture)
                .Select(pair => pair.Count > 1 ? $"{pair.Unit.Name} ×{pair.Count}" : pair.Unit.Name)));
        var total = groups.Sum(group => group.Sum(pair => pair.Count));
        return string.Join("\n", lines) + $"\n합계 {total}기";
    }

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();
}
