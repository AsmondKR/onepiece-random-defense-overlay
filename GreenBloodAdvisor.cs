namespace OrandOverlay;

public sealed record GreenBloodAdvice(
    string UnitId,
    string Name,
    string Reason,
    string? Warning);

/// <summary>
/// 신 이상 난이도에서 마지막 스토리(에그헤드) 클리어 보상으로 나오는 그린블러드의
/// 사용처를 추천한다. 신 기준 획득이 확정이므로 보유 전에도 계획으로 항상 노출하고,
/// 미사용 보유가 감지되면 보유 중 상태만 덧붙인다. 그린블러드는 전설/히든에게만
/// 부여할 수 있으므로 후보도 전설/히든으로 제한한다. 우선순위:
/// 데이터 태그(greenblood-priority) → 현재 목표의 신+ 클리어 채용률.
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

        var advice = new List<GreenBloodAdvice>();

        // 그린블러드는 전설/히든 유닛에게만 부여할 수 있다(왜곡 유닛의 조합식과 무관).
        // 클리어 기록에는 부여 대상이 남지 않으므로 순위는 데이터 태그와
        // 신+ 채용률(현재 목표 기준)로 추정한다.
        // 보유 전설·히든이 없으면(재료로 소모된 초반 등) 추천 조합에 들어 있는
        // 전설·히든으로 폴백해 "앞으로 짤 사용처" 계획을 항상 보여준다.
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
            .OrderBy(item => item.IsKuma) // 스턴 소실 위험은 항상 후순위.
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

    /// <summary>미사용 그린블러드(자동 인식 또는 수동 보정) 보유 여부.</summary>
    public static bool HasUnusedGreenBlood(DataCatalog catalog, IEnumerable<InventoryEntry> inventory) =>
        inventory
            .Where(entry => entry.Count > 0)
            .Any(entry => catalog.Unit(entry.UnitId).Tags
                .Contains("greenblood", StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 그린블러드는 사용해도 어디에 박았는지 흔적이 남지 않는다. 인식 스트림에서
    /// 보이던 그린블러드가 게임 중 사라지면 "사용됨"으로 전환해 사용처 안내를 숨기고,
    /// 다시 보이면(재획득) 안내를 재개한다. 스캔이 사용 순간을 못 본 판을 위해
    /// 수동 토글도 제공한다. 게임 종료 시 Reset.
    /// </summary>
    public sealed class UsageTracker(DataCatalog catalog)
    {
        // 그린블러드 보유는 두 독립 탐색이 일치할 때만 확정되어 스캔마다 흔들릴 수
        // 있다. 한 번 안 보였다고 사용됨 처리하면 오탐으로 안내가 꺼지므로,
        // 연속 3회(약 3.6초) 미검출일 때만 사용된 것으로 판정한다.
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
            // 수동 표시는 대부분 유닛 부여 케이스다(세라핌 제작은 자동 인식됨).
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
