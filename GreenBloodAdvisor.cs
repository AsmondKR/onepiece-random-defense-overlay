namespace OrandOverlay;

public sealed record GreenBloodAdvice(
    string UnitId,
    string Name,
    string Reason,
    string? Warning,
    bool Seraphim = false);

/// <summary>
/// 신 이상 난이도에서 마지막 스토리(에그헤드) 클리어 보상으로 나오는 그린블러드의
/// 사용처를 추천한다. 신 기준 획득이 확정이므로 보유 전에도 계획으로 항상 노출하고,
/// 미사용 보유가 감지되면 보유 중 상태만 덧붙인다.
/// 우선순위: 호스트 패가 모인 세라핌 → 이미 보유한 전설/히든 부여.
/// 그린블러드는 전설/히든에게만 부여할 수 있다.
/// </summary>
public sealed class GreenBloodAdvisor(DataCatalog catalog)
{
    private static readonly string[] EligibleBaseTiers = ["전설", "히든"];
    private const string KumaRawcode = "030h";
    private const string KumaWarning = "쿠마는 그린블러드를 주면 스턴 0.5가 사라집니다";
    private const int MaximumAdvice = 3;

    public IReadOnlyList<GreenBloodAdvice> Evaluate(
        UnitDefinition goal,
        IEnumerable<InventoryEntry> inventory,
        IReadOnlyList<Recommendation> recommendations,
        ClearBuildStats? clearStats)
    {
        var owned = inventory
            .Where(entry => entry.Count > 0)
            .GroupBy(entry => entry.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count),
                StringComparer.OrdinalIgnoreCase);
        var recIds = recommendations
            .Select(item => item.Route.GoalUnitId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var calculator = new RecipeCompletionCalculator(catalog.Unit);

        var seraphim = catalog.AllUnits
            .Where(unit => BaseTier(unit.Tier) == "세라핌")
            .DistinctBy(unit => unit.Id)
            .Where(unit => owned.GetValueOrDefault(unit.Id) <= 0)
            .Select(unit => (Unit: unit, Host: SeraphimHost(unit)))
            .Where(item => item.Host is not null && HostReady(item.Host!, owned, calculator))
            .OrderByDescending(item => recIds.Contains(item.Unit.Id))
            .ThenByDescending(item => recIds.Contains(item.Host!.Id))
            .ThenByDescending(item => item.Host!.Tags.Contains("greenblood-priority",
                StringComparer.OrdinalIgnoreCase))
            .ThenByDescending(item => ClearShare(clearStats, goal, item.Unit))
            .ThenBy(item => item.Unit.Name, StringComparer.CurrentCulture)
            .Select(item => new GreenBloodAdvice(
                item.Unit.Id,
                $"{HostDisplayName(item.Host!)} → {item.Unit.Name}",
                "세라핌 우선",
                null,
                true))
            .Take(MaximumAdvice)
            .ToList();
        if (seraphim.Count > 0) return seraphim;

        var advice = new List<GreenBloodAdvice>();
        var ownedEligible = owned.Keys
            .Select(catalog.Unit)
            .Where(unit => EligibleBaseTiers.Contains(BaseTier(unit.Tier), StringComparer.Ordinal))
            .ToList();
        var planned = ownedEligible.Count > 0
            ? ownedEligible
            : recommendations
                .Select(recommendation => catalog.Unit(recommendation.Route.GoalUnitId))
                .Where(unit => EligibleBaseTiers.Contains(BaseTier(unit.Tier), StringComparer.Ordinal))
                .ToList();
        var fromPlan = ownedEligible.Count == 0;

        var candidates = planned
            .Select(unit => new
            {
                Unit = unit,
                IsTaggedPriority = unit.Tags.Contains("greenblood-priority",
                    StringComparer.OrdinalIgnoreCase),
                Share = ClearShare(clearStats, goal, unit),
                IsKuma = unit.Rawcodes.Contains(KumaRawcode, StringComparer.Ordinal)
            })
            .OrderBy(item => item.IsKuma)
            .ThenByDescending(item => item.IsTaggedPriority)
            .ThenByDescending(item => item.Share)
            .ThenBy(item => item.Unit.Name, StringComparer.CurrentCulture)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (advice.Count >= MaximumAdvice) break;
            if (advice.Any(existing => existing.UnitId.Equals(candidate.Unit.Id,
                    StringComparison.OrdinalIgnoreCase))) continue;
            var reason = candidate.IsTaggedPriority
                ? "커뮤니티 우선 사용처"
                : candidate.Share > 0
                    ? $"현재 목표 신+ 클리어 채용률 {Math.Round(candidate.Share * 100):0}퍼센트"
                    : fromPlan
                        ? "추천 조합의 전설·히든 (완성 시 사용처)"
                        : "보유 중인 전설·히든";
            advice.Add(new GreenBloodAdvice(candidate.Unit.Id, candidate.Unit.Name, reason,
                candidate.IsKuma ? KumaWarning : null));
        }

        return advice.Take(MaximumAdvice).ToList();
    }

    public static bool HasUnusedGreenBlood(DataCatalog catalog, IEnumerable<InventoryEntry> inventory) =>
        inventory
            .Where(entry => entry.Count > 0)
            .Any(entry => catalog.Unit(entry.UnitId).Tags
                .Contains("greenblood", StringComparer.OrdinalIgnoreCase));

    public sealed class UsageTracker(DataCatalog catalog)
    {
        private const int MissingStreakForUsed = 3;

        private bool _seen;
        private int _missingStreak;
        private int _seraphimWhenSeen;
        public bool Used { get; private set; }

        /// <summary>
        /// 세라핌 제작이 아니라 유닛에 직접 부여한 경우. 부여 시 진력해방
        /// (스턴 1.2 · 공속 30, 맵 실측)이 패 수치에 합산돼야 한다.
        /// 세라핌 제작이면 새 세라핌 유닛이 인식에 나타나므로 구분할 수 있다.
        /// </summary>
        public bool UsedOnUnit { get; private set; }

        public void Observe(IEnumerable<InventoryEntry> entries)
        {
            var snapshot = entries.Where(entry => entry.Count > 0).ToList();
            var seraphim = snapshot
                .Where(entry => catalog.Unit(entry.UnitId).Tier.Split('[', 2)[0].Trim() == "세라핌")
                .Sum(entry => entry.Count);
            if (HasUnusedGreenBlood(catalog, snapshot))
            {
                _seen = true;
                _missingStreak = 0;
                _seraphimWhenSeen = seraphim;
                Used = false;
                UsedOnUnit = false;
                return;
            }
            if (!_seen) return;
            if (++_missingStreak < MissingStreakForUsed) return;
            _seen = false;
            _missingStreak = 0;
            Used = true;
            UsedOnUnit = seraphim <= _seraphimWhenSeen;
        }

        public void Toggle()
        {
            Used = !Used;
            UsedOnUnit = Used;
        }

        public void Reset()
        {
            _seen = false;
            _missingStreak = 0;
            _seraphimWhenSeen = 0;
            Used = false;
            UsedOnUnit = false;
        }
    }

    private UnitDefinition? SeraphimHost(UnitDefinition seraphim)
    {
        if (BaseTier(seraphim.Tier) != "세라핌") return null;
        foreach (var id in seraphim.Recipe.Keys)
        {
            if (id.Equals("item_greenblood", StringComparison.OrdinalIgnoreCase)) continue;
            var unit = catalog.Unit(id);
            if (BaseTier(unit.Tier) is "아이템" or "자원") continue;
            return unit;
        }
        return null;
    }

    private static bool HostReady(UnitDefinition host, IReadOnlyDictionary<string, int> owned,
        RecipeCompletionCalculator calculator)
    {
        if (owned.GetValueOrDefault(host.Id) > 0) return true;
        return calculator.Calculate([host.Id], owned).CompletionRatio >= 1;
    }

    private static string HostDisplayName(UnitDefinition unit)
    {
        var tier = BaseTier(unit.Tier);
        var name = unit.Name.Trim();
        if (!string.IsNullOrWhiteSpace(tier) &&
            name.EndsWith(" " + tier, StringComparison.OrdinalIgnoreCase))
            return name[..^(tier.Length + 1)].TrimEnd();
        return name;
    }

    private static double ClearShare(ClearBuildStats? clearStats, UnitDefinition goal,
        UnitDefinition candidate)
    {
        if (clearStats is null) return 0;
        var profile = clearStats.GoalProfile(goal.Rawcodes);
        if (profile is null || profile.SampleCount < ClearBuildStats.MinimumGoalSamples) return 0;
        return candidate.Rawcodes
            .Select(rawcode => profile.SupportShare.GetValueOrDefault(rawcode))
            .DefaultIfEmpty()
            .Max();
    }

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();
}
