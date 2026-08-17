namespace OrandOverlay;

public sealed record SpecialDismantleAdvice(string UnitId, string Name, bool Dismantle,
    string Reason);

/// <summary>
/// 특수함 유닛(모건·베티·아이스버그·오타마·블고리·페루·폭시)은 조합 불가 특수
/// 획득물이며 분해하면 카포네 갱 벳지를 얻는다. 갱벳지는 시류·센토마루·우솝·죠즈
/// (희귀함)와 방주맥심·보니·카타쿠리의 재료라, 현재 추천 빌드에 갱벳지가 부족하면
/// 분해를, 아니면 현재 목표 신+ 채용 실측을 근거로 유지를 추천한다.
/// </summary>
public sealed class SpecialDismantleAdvisor(DataCatalog catalog)
{
    public const string GangBadgeRawcode = "C10h";
    private const double KeepShareFloor = 0.03;

    public IReadOnlyList<SpecialDismantleAdvice> Evaluate(
        IEnumerable<InventoryEntry> inventory,
        IReadOnlyList<Recommendation> recommendations,
        UnitDefinition goal,
        ClearBuildStats? clearStats)
    {
        var entries = inventory.Where(entry => entry.Count > 0).ToList();
        var specials = entries
            .Select(entry => catalog.Unit(entry.UnitId))
            .Where(unit => BaseTier(unit.Tier) == "특수함")
            .DistinctBy(unit => unit.Id)
            .ToList();
        if (specials.Count == 0) return [];

        // 추천 빌드 전체에서 갱벳지 부족분을 세고, 보유 갱벳지로 상쇄한다.
        var badgeNeed = recommendations
            .SelectMany(recommendation => recommendation.RemainingCraftSteps)
            .Where(step => catalog.Unit(step.UnitId).Rawcodes
                .Contains(GangBadgeRawcode, StringComparer.Ordinal))
            .Sum(step => step.MissingCount);
        var badgeOwned = entries
            .Where(entry => catalog.Unit(entry.UnitId).Rawcodes
                .Contains(GangBadgeRawcode, StringComparer.Ordinal))
            .Sum(entry => entry.Count);
        var deficit = Math.Max(0, badgeNeed - badgeOwned);

        var profile = clearStats?.GoalProfile(goal.Rawcodes);
        var advice = new List<SpecialDismantleAdvice>();
        foreach (var special in specials
                     .OrderBy(unit => SupportShareOf(profile, unit)))
        {
            if (deficit > 0)
            {
                advice.Add(new SpecialDismantleAdvice(special.Id, special.Name, true,
                    $"추천 빌드에 갱벳지 {badgeNeed}개 필요 — 분해해서 재료로"));
                deficit--;
                continue;
            }
            var share = SupportShareOf(profile, special);
            advice.Add(share >= KeepShareFloor
                ? new SpecialDismantleAdvice(special.Id, special.Name, false,
                    $"현재 목표 신+ 채용률 {Math.Round(share * 100):0}퍼센트 — 유지 권장")
                : new SpecialDismantleAdvice(special.Id, special.Name, false,
                    "갱벳지 수요 없음 — 유지·분해 자유"));
        }
        return advice;
    }

    private static double SupportShareOf(GoalClearProfile? profile, UnitDefinition unit) =>
        profile is null
            ? 0
            : unit.Rawcodes
                .Select(code => profile.SupportShare.GetValueOrDefault(code))
                .DefaultIfEmpty()
                .Max();

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();
}
