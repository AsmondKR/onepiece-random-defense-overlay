namespace OrandOverlay;

public sealed record RareRerollAdvice(
    string UnitId,
    string Name,
    int OwnedCount,
    int NeededCount,
    int RerollCount,
    string Reason,
    bool Sell = false);

/// <summary>
/// Finds story-reward rare cards that are not needed by the recommendations currently
/// shown to the player. Recipes are expanded down to the rare tier, while already-owned
/// higher-tier units satisfy their branch before rare-card demand is calculated.
/// </summary>
public sealed class RareRerollAdvisor(DataCatalog catalog)
{
    public IReadOnlyList<RareRerollAdvice> Evaluate(
        IEnumerable<InventoryEntry> inventory,
        IReadOnlyList<Recommendation> recommendations,
        UnitDefinition? goal = null,
        ClearBuildStats? clearStats = null)
    {
        if (recommendations.Count == 0) return [];
        // 현재 목표의 신+ 클리어에서 실제 채용되는 지원 유닛 채용률 — 유틸 전설
        // 보존 판단의 근거가 된다(에이스 전설 사례).
        var supportShare = goal is not null && clearStats is { HasData: true }
            ? clearStats.GoalProfile(goal.Rawcodes)?.SupportShare
            : null;

        var owned = inventory
            .Where(entry => entry.Count > 0)
            .GroupBy(entry => entry.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count),
                StringComparer.OrdinalIgnoreCase);
        var availability = owned.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var rareDemand = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var recommendation in recommendations)
        {
            var rootId = recommendation.CompositionUnits.FirstOrDefault()?.UnitId
                         ?? recommendation.Route.GoalUnitId;
            if (string.IsNullOrWhiteSpace(rootId)) continue;
            AccumulateRareDemand(rootId, 1, availability, rareDemand,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        // 계획에 아직 부족한 희귀패가 있으면 리롤이 그 패를 뽑을 기회다.
        // 부족한 희귀패가 하나도 없으면 리롤로 얻을 것이 없으므로 판매를 권한다.
        var hasMissingRare = rareDemand.Any(pair =>
            pair.Value > Math.Max(0, owned.GetValueOrDefault(pair.Key)));

        return owned
            .Select(pair => (pair.Key, Count: pair.Value, Unit: catalog.Unit(pair.Key)))
            .Where(item => BaseTier(item.Unit.Tier) == "희귀함")
            // 재료로는 안 쓰여도 ① 자체 지원 스탯(방깎·이감·스턴 등)이 있거나
            // ② 현재 목표의 클리어에서 실제 채용(3% 이상)되는 유틸 전설·히든의 직계
            // 재료인 희귀패(예: 마르코 → 에이스 전설 방깎33)는 남긴다(유저 피드백).
            // 거의 모든 희귀함이 어떤 유틸 전설로든 이어지므로(실측 42종 중 41종)
            // 채용률 조건 없이 넓히면 정리 기능 자체가 무력화된다.
            .Where(item => rareDemand.GetValueOrDefault(item.Key) > 0 ||
                           (!HasUtilityAbility(item.Unit) &&
                            !FeedsAdoptedUtilityLegend(item.Key, supportShare)))
            .Select(item =>
            {
                var needed = rareDemand.GetValueOrDefault(item.Key);
                var reroll = Math.Max(0, item.Count - needed);
                var sell = !hasMissingRare;
                var reason = needed == 0
                    ? sell ? "추천 제작에 안 씀 · 부족한 희귀패 없음" : "현재 추천 제작에는 사용하지 않음"
                    : $"추천 제작에 {needed}장 필요 · 초과분";
                return new RareRerollAdvice(item.Key, item.Unit.Name, item.Count,
                    needed, reroll, reason, sell);
            })
            .Where(item => item.RerollCount > 0)
            .OrderBy(item => item.NeededCount > 0)
            .ThenByDescending(item => item.RerollCount)
            .ThenBy(item => item.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private void AccumulateRareDemand(string unitId, int requiredCount,
        IDictionary<string, int> availability,
        IDictionary<string, int> rareDemand,
        ISet<string> visiting)
    {
        if (requiredCount <= 0 || IsResource(unitId)) return;
        var unit = catalog.Unit(unitId);
        if (BaseTier(unit.Tier) == "자원") return;

        // Rare cards are the reroll decision boundary. Record their total requirement;
        // comparison with current ownership happens after all visible plans are expanded.
        if (BaseTier(unit.Tier) == "희귀함")
        {
            var currentDemand = rareDemand.TryGetValue(unit.Id, out var demand) ? demand : 0;
            rareDemand[unit.Id] = SaturatingAdd(currentDemand, requiredCount);
            return;
        }

        var available = Math.Max(0, availability.TryGetValue(unit.Id, out var count) ? count : 0);
        var consumed = Math.Min(available, requiredCount);
        if (consumed > 0) availability[unit.Id] = available - consumed;
        var missing = requiredCount - consumed;
        if (missing <= 0 || unit.Recipe.Count == 0 || !visiting.Add(unit.Id)) return;

        foreach (var ingredient in unit.Recipe)
        {
            if (ingredient.Value <= 0) continue;
            AccumulateRareDemand(ingredient.Key, SaturatingMultiply(missing, ingredient.Value),
                availability, rareDemand, visiting);
        }
        visiting.Remove(unit.Id);
    }

    // 패에 있는 것만으로 역할 목표(스턴·이감·방깎·마방깎)에 기여하는 능력들.
    private static readonly string[] UtilityAbilityNames =
    [
        "스턴", "이동속도 감소", "발동이동속도 감소",
        "방어력 감소", "발동방어력 감소", "중첩방어력 감소", "단일방어력 감소",
        "마법 방어력 감소"
    ];

    public static bool HasUtilityAbility(UnitDefinition unit) => unit.OfficialAbilities
        .Any(ability => UtilityAbilityNames.Contains(
            RecommendationPresentation.SafeText(ability.Name), StringComparer.Ordinal));

    public const double LegendAdoptionThreshold = 0.03;

    private List<UnitDefinition>? _utilityLegends;

    /// <summary>
    /// 현재 목표의 클리어에서 채용률 3% 이상인 유틸 전설·히든의 직계 재료인지.
    /// 목표·클리어 데이터가 없으면 false(정리 로직은 재료 수요만 본다).
    /// </summary>
    public bool FeedsAdoptedUtilityLegend(string unitId,
        IReadOnlyDictionary<string, double>? supportShare)
    {
        if (supportShare is null) return false;
        _utilityLegends ??= catalog.AllUnits
            .Where(unit => BaseTier(unit.Tier) is "전설" or "히든")
            .Where(HasUtilityAbility)
            .DistinctBy(unit => unit.Id)
            .ToList();
        return _utilityLegends.Any(legend =>
            legend.Rawcodes.Any(code =>
                supportShare.GetValueOrDefault(code) >= LegendAdoptionThreshold) &&
            legend.Recipe.Keys.Any(key =>
                catalog.Unit(key).Id.Equals(unitId, StringComparison.OrdinalIgnoreCase)));
    }

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();

    private static bool IsResource(string id) => id is "GOLD" or "LUMBER" or "POINT" or "RANDOM";

    private static int SaturatingAdd(int left, int right) =>
        left > int.MaxValue - right ? int.MaxValue : left + right;

    private static int SaturatingMultiply(int left, int right) =>
        left == 0 || right == 0 ? 0 : left > int.MaxValue / right ? int.MaxValue : left * right;
}
