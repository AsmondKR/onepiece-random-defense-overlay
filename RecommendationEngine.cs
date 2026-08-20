using System.Globalization;

namespace OrandOverlay;

public sealed class RecommendationEngine(DataCatalog catalog, ClearBuildStats? clearStats = null,
    CombineHotkeyCatalog? combineHotkeys = null)
{
    // RecommendNearestCrafts 진입 시 항법에 따라 설정된다. 1상위 항법이면 1상위
    // 클리어만, 긴급소집 같은 다상위 항법이면 상위 2기 이상 클리어만 집계한
    // 프로필을 쓴다(표본 부족 시 전체로 후퇴).
    private TopScope _topScope;

    // 이번 패스의 스턴 공략 목표·상한 — 패 수치 카드가 고정 1.4 대신 이 값을 쓴다.
    // (기본 1.4/1.5, 니카 이감 1.6/1.7, 노이감 2.9/3.0 등 공략마다 다르다.)
    public double ActiveStunTarget { get; private set; } = StableStunTarget;
    public double ActiveStunCap { get; private set; } = MaximumUsefulStun;
    // 이번 추천 패스에서 쓸 클리어 프로필. 마딜 목표는 보유 앵커(이미 짠 취향 유닛)와
    // 같이 쓰인 클리어만 재집계한 조건부 프로필일 수 있다.
    private GoalClearProfile? _activeClearProfile;
    private LiveStats _liveStats = new();

    /// <summary>자체 수집 통계의 게이트 통과 가중을 화면 순서 점수에 반영하도록 연결한다.</summary>
    public void SetLiveStats(LiveStats liveStats) => _liveStats = liveStats;
    private string? _activeAnchorLabel;

    private const double CompletionWeight = 34;
    private const double RoleWeight = 36;
    // TMO build-helper 43747 strategy baseline: full slow is 102 on both Divine/Nightmare.
    private const double FullSlowTarget = 102;
    private const double StableStunTarget = 1.4;
    private const double MaximumUsefulStun = 1.5;
    // 니카(루초·뱀초) 실측 216판: 이감 버전(스턴 1.6·이감 95)과 노이감 버전
    // (스턴 2.1·이감 40)이 갈린다. 패의 스턴이 이 값 이상이면 노이감으로 판정.
    private const double NikaNoSlowCommitStun = 1.8;
    private const double FullArmorReductionTarget = 211;
    // 신+ 상디초월 클리어 92판 실측: 방깎 중앙값 0, 마방깎 중앙값 1(에넬·후지토라·우타
    // 경유, p75=18). 마딜 상위는 방깎 대신 마방깎 소스 최소 한 점만 확보하고, 큰 수치는
    // 채용률 정렬에 맡긴다.
    private const double MagicArmorSourceTarget = 1;

    // 2.314 이후(2026-07-17~08-16) 야마토 성공 조합과 명시적 추천을 집계한 점수다.
    // 단순 언급은 제외하고 성공 구성 +2, 명시 추천 +2, 조건부 추천 +1,
    // 명시 비추천 -2로 정리했다. 같은 역할 후보 안에서는 이 점수가 제작 거리보다 우선한다.
    private static readonly IReadOnlyDictionary<string, int> YamatoCommunityPriority =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Q30h"] = 12, // 모비딕호
            ["V50h"] = 11, // 에이스 왜곡
            ["F30h"] = 10, // 카르가라
            ["HA0h"] = 10, // 킹
            ["830h"] = 9,  // 시저
            ["M30h"] = 9,  // 사보 히든
            ["W50h"] = 8,  // 비비 변화
            ["630h"] = 8,  // 센고쿠 전설
            ["B30h"] = 7,  // 흰수염 전설
            ["N30h"] = 7,  // 료쿠규 히든
            ["O30h"] = 6,  // 봉쿠레 히든
            ["W20h"] = 6,  // 드래곤 전설
            ["Z20h"] = 5,  // 바르톨로메오 전설
            ["V20h"] = 1   // 스모커 전설: 최근 글에서는 대부분 최후의 보루로 평가
        };

    // TMO public clears, 2026-07-18~08-16: 1,626 God/Nightmare stratified samples,
    // 40 Usopp-transcendent clears. Raw co-occurrence is adjusted only where a frequent
    // unit duplicates Usopp's built-in boss/berserk control instead of advancing the
    // 102 slow / 1.4 stun / 211 armor core. This keeps community evidence subordinate
    // to the user's stated role completion order.
    private static readonly IReadOnlyDictionary<string, int> UsoppCommunityPriority =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["M30h"] = 21, // Sabo hidden: 21/40, slow + armor.
            ["W20h"] = 20, // Dragon: 14/40, exact 0.9 stun + slow + armor.
            ["IC0h"] = 20, // Queen: 14/40, 0.4 stun alternative (often paired with Bartolomeo 1.0).
            ["630h"] = 18, // Sengoku: 12/40, slow + triggered armor.
            ["W50h"] = 16, // Vivi changed: 11/40, slow + armor.
            ["Q30h"] = 15, // Moby Dick: 10/40, only when pirate ship is owned.
            ["N30h"] = 15, // Ryokugyu: 10/40, triggered slow + armor.
            ["HA0h"] = 14, // King: 7/40, slow + fixed/triggered armor.
            ["830h"] = 14, // Caesar: 10/40, strong pure armor finish.
            ["H30h"] = 13, // Cracker: 11/40, armor finish.
            ["Z20h"] = 12, // Bartolomeo: 12/40, stun alternative.
            ["O30h"] = 11, // Bon Clay: 9/40, 0.5 stun alternative.
            ["V20h"] = 10, // Smoker: 8/40, large slow + armor break.
            ["T30h"] = 9,  // Rebecca: 9/40, armor.
            ["V50h"] = 9,  // Ace changed: 9/40, slow.
            ["540h"] = 5   // Killer: 12/40, boss roles duplicate Usopp; armor only.
        };

    // 2.314 원랜디 갤 최신 조초 평가(2026-08-10): 예전의 조크봉 고정이 아니라
    // 크제 또는 봉히 한 기로 홀딩 축을 잡고, 남은 패를 풀방깎과 보조딜에 쓴다.
    private static readonly IReadOnlyDictionary<string, int> ZoroCommunityPriority =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["F50h"] = 30, // 크로커다일 제한: 다상위 항법의 우선 파트너.
            ["O30h"] = 29, // 봉쿠레 히든: 1상위 항법의 저비용 대체.
            ["830h"] = 18, // 시저: 순수 방깎 마감.
            ["H30h"] = 17, // 크래커: 방깎 마감.
            ["M30h"] = 16, // 사보 히든: 이감 + 방깎.
            ["W50h"] = 15, // 비비 변화: 이감 + 방깎.
            ["540h"] = 10  // 킬러: 풀방깎 뒤 보스 보완.
        };

    // 2.314 원랜디 갤 최신 징초 대깨 평가(2026-08-14~16): 징초 외 암브 한 기가
    // 필수이며, 1상위에서는 스모커/베르고/퀸, 다상위에서는 킹/카벤(특성공학이면
    // 알비다)을 패 상황에 맞춰 고른다. 점수는 같은 역할 안에서 제작 거리보다 우선한다.
    private static readonly IReadOnlyDictionary<string, int> JinbeCommunityPriority =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Q80h"] = 40, // 알비다 제한
            ["IA0h"] = 38, // 킹 제한
            ["B50h"] = 36, // 카벤딧슈 영원
            ["V20h"] = 34, // 스모커 전설: 1상위 기본 암브 + 이감
            ["W30h"] = 32, // 베르고 히든: 저비용 암브
            ["IC0h"] = 30, // 퀸 히든: 암브 + 스턴 + 방깎
            ["930h"] = 28  // 시키 전설: 암브 + 스턴
        };

    public IReadOnlyList<Recommendation> Recommend(
        string goalUnitId,
        IEnumerable<InventoryEntry> inventory,
        int take = 3)
    {
        var counts = inventory
            .GroupBy(x => x.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count), StringComparer.OrdinalIgnoreCase);

        var ownedRoles = AggregateRoles(counts);
        var recipeCalculator = new RecipeCompletionCalculator(catalog.Unit);
        return catalog.Data.Routes
            .Where(x => x.GoalUnitId.Equals(goalUnitId, StringComparison.OrdinalIgnoreCase))
            .Select(route => Evaluate(route, counts, ownedRoles, recipeCalculator))
            .OrderByDescending(x => x.Score)
            .Take(take)
            .ToList();
    }

    public IReadOnlyList<Recommendation> RecommendNearestCrafts(
        string goalUnitId,
        IEnumerable<InventoryEntry> inventory,
        int take = 8,
        string navigationMode = "PathOfKings.BountyHunter",
        GoroseiMode gorosei = GoroseiMode.None,
        string buildVariant = BuildVariants.AutoId,
        bool suppressSeraphim = false)
    {
        var counts = inventory
            .GroupBy(x => x.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.Count),
                StringComparer.OrdinalIgnoreCase);
        _shipNeedCache.Clear();
        var calculator = new RecipeCompletionCalculator(catalog.Unit);
        var goal = catalog.Unit(goalUnitId);
        var goalSuggestion = EvaluateCraft(goal, counts, calculator);
        // 니카 루초/뱀초처럼 인게임 rawcode를 공유하는 목표는 어느 쪽으로 인식돼도
        // 보유로 판정한다.
        var goalOwned = counts.GetValueOrDefault(goalUnitId) > 0 ||
                        goal.Rawcodes.Any(code =>
                            counts.GetValueOrDefault("rawcode:" + code) > 0);
        var navigation = NavigationProfiles.Find(navigationMode);
        _topScope = navigation.AllowsMultipleTopUnits ? TopScope.MultiTop
            : navigation.CanCraftTopUnits ? TopScope.SoloTop
            : TopScope.Any;

        // 취향 조건부 학습: 이미 짠 지원급 유닛을 앵커로, 그 유닛과 같이 쓰인
        // 클리어만 골라 채용률을 재계산한다(마딜에서 검증 후 전 상위로 확대 —
        // 뱀초의 이감/노이감처럼 물딜도 행보가 갈린다). 목박 필러는 앵커가 아니다.
        var ownedAnchorCodes = counts.Where(pair => pair.Value > 0)
            .Select(pair => catalog.Unit(pair.Key))
            .Where(unit => CountsAsCompletedSupport(unit.Tier))
            .SelectMany(unit => unit.Rawcodes)
            .Where(code => !goal.Rawcodes.Contains(code, StringComparer.Ordinal))
            .Where(code => !LeftoverFillerRawcodes.Contains(code))
            .ToList();
        _activeClearProfile = clearStats?.ResolveProfile(goal.Rawcodes, _topScope,
            ownedAnchorCodes);
        _activeAnchorLabel = _activeClearProfile?.AnchorRawcodes is { Count: > 0 } anchorCodes
            ? string.Join("·", anchorCodes.Select(code => catalog.Unit("rawcode:" + code).Name))
            : null;

        // 키자루+특성공학은 특포가 키자루 스킬강화에 계속 들어가므로,
        // 특강(필수) 상위는 특포 경합으로 추가 상위 후보에서 제외한다.
        var avoidTraitHungryTops =
            goal.Rawcodes.Contains("5B0H", StringComparer.Ordinal) &&
            navigation.Id.Equals("AlliedForces.TraitEngineering", StringComparison.OrdinalIgnoreCase);
        // 그린블러드는 판당 1회용 — 이미 세라핌을 만들었거나(보유 세라핌 존재),
        // 유닛에 부여했으면(사용됨 신호·가상 buff 항목) 세라핌은 더 만들 수 없다.
        var seraphimBlocked = suppressSeraphim ||
                              counts.GetValueOrDefault("greenblood_buff") > 0 ||
                              catalog.AllUnits
                                  .Where(unit => unit.Tier.Split('[', 2)[0].Trim() == "세라핌")
                                  .Any(unit => counts.GetValueOrDefault(unit.Id) > 0);
        var candidates = catalog.AllUnits
            .Where(unit => !unit.Id.Equals(goalUnitId, StringComparison.OrdinalIgnoreCase))
            .Where(unit => counts.GetValueOrDefault(unit.Id) <= 0)
            .Where(unit => !seraphimBlocked || unit.Tier.Split('[', 2)[0].Trim() != "세라핌")
            .Where(unit => MeetsOwnedPrerequisites(unit, counts))
            .Where(unit => IsRecommendedCraftTier(unit.Tier, navigation.AllowsMultipleTopUnits))
            .Where(unit => !avoidTraitHungryTops ||
                           !unit.Rawcodes.Any(TraitHungryTopRawcodes.Contains))
            .Where(unit => !AvoidTraitPointCraftWithoutEconomy(navigation, unit, counts))
            .Where(unit => unit.Recipe.Count > 0)
            .Select(unit => new CraftCandidate(unit, EvaluateCraft(unit, counts, calculator),
                StrategyMetricsFor(unit)))
            .Where(candidate => candidate.Recommendation.RecipeProgress.RequiredLeafCount > 0)
            .ToList();

        var showGoal = navigation.CanCraftTopUnits && !goalOwned;
        var maximumSupports = Math.Max(0, take - (showGoal ? 1 : 0));
        // 목표 자체 스턴 + 패에 쌓인 스턴으로 빌드 방향(니카 이감/노이감)을 판정한다.
        // 보유한 목표의 스턴은 집계에 이미 포함되고, 조합 예정이면 여기서 더한다.
        var committedStun = AggregateStrategyMetrics(counts).Stun +
                            (showGoal ? StrategyMetricsFor(goal).Stun : 0);
        var strategy = ApplyGorosei(StrategyProfileFor(goal, committedStun, buildVariant), gorosei);
        ActiveStunTarget = strategy?.StunTarget ?? StableStunTarget;
        ActiveStunCap = strategy?.StunCap ?? MaximumUsefulStun;
        // 키자루 초월 + 역발상: 레일리는 확정 획득이지만 특성포인트가 부족해 자체
        // 딜이 약하다(유저 검증 · 가이드는 특성공학 추천). 단일·끝딜 보강으로
        // 라인딜 공백을 메운다. 클리어 기록엔 항법 흔적이 없어 항법 선택으로 반영.
        if (goal.Rawcodes.Contains("5B0H", StringComparer.Ordinal) &&
            navigation.Id.Equals("BestHelp.ReverseThinking", StringComparison.OrdinalIgnoreCase) &&
            strategy is { } kizaruStrategy)
            strategy = kizaruStrategy with
            {
                SingleDamageTarget = Math.Max(1, kizaruStrategy.SingleDamageTarget),
                FinisherDamageTarget = Math.Max(1, kizaruStrategy.FinisherDamageTarget)
            };
        var nearest = strategy is not null
            ? OrderStrategySupports(goal, counts, candidates, maximumSupports, strategy.Value,
                navigation.AllowsMultipleTopUnits, navigation.CanCraftTopUnits)
            : OrderByCraftDistance(candidates).Take(maximumSupports).Select(x => x.Recommendation).ToList();

        // 초월은 하위 전설을 먼저 짜야 스토리를 민다. 역할 패키지보다 후보 보드 앞에 둔다.
        var recipeLegendaryIds = RecipeLegendaryIds(goal);
        if (recipeLegendaryIds.Count > 0)
        {
            var missingLegendaries = recipeLegendaryIds
                .Where(id => counts.GetValueOrDefault(id) <= 0)
                .Select(id => EvaluateCraft(catalog.Unit(id), counts, calculator))
                .ToList();
            var pinned = new HashSet<string>(missingLegendaries.Select(item => item.Route.GoalUnitId),
                StringComparer.OrdinalIgnoreCase);
            nearest = missingLegendaries
                .Concat(nearest.Where(item => !pinned.Contains(item.Route.GoalUnitId)))
                .Take(maximumSupports)
                .ToList();
        }

        // 어떤 유닛을 조합할지는 역할 로직이 고르고, 화면 순서는 신+ 채용률(또는
        // 수작업 우선도)이 높은 순으로 보여준다. 동점은 역할 파이프라인 순서 유지.
        // 초월의 하위 전설은 채용률보다 스토리 진행이 앞선다.
        nearest = nearest
            .Select((recommendation, index) => (recommendation, index))
            .OrderByDescending(pair =>
                recipeLegendaryIds.Contains(pair.recommendation.Route.GoalUnitId) ? 1 : 0)
            .ThenByDescending(pair =>
                CommunityPriorityScore(goal, catalog.Unit(pair.recommendation.Route.GoalUnitId)))
            .ThenBy(pair => pair.index)
            .Select(pair => pair.recommendation)
            .ToList();

        var visibleGoal = showGoal ? [goalSuggestion] : Enumerable.Empty<Recommendation>();
        var results = visibleGoal.Concat(nearest).Take(Math.Max(1, take)).ToList();

        // 세라핌은 역할 지표(스턴·이감·방깎)가 없어 파이프라인이 집지 못한다.
        // 현재 목표 채용률이 충분한(10%+) 최고 세라핌 1기를, 역할 구성(스턴 페어 등)을
        // 밀어내지 않도록 목록에 '추가'로 끼워 넣는다(실측: 징베 S-호크 48%,
        // 상디 S-베어 34% — 목표별로 만드는 세라핌이 갈린다).
        if (_activeClearProfile is { } seraphimProfile && !seraphimBlocked)
        {
            var bestSeraphim = catalog.AllUnits
                .Where(unit => unit.Tier.Split('[', 2)[0].Trim() == "세라핌")
                .DistinctBy(unit => unit.Id)
                .Where(unit => counts.GetValueOrDefault(unit.Id) <= 0)
                .Select(unit => (Unit: unit, Share: unit.Rawcodes
                    .Select(code => seraphimProfile.SupportShare.GetValueOrDefault(code))
                    .DefaultIfEmpty()
                    .Max()))
                .Where(pair => pair.Share >= 0.10)
                .OrderByDescending(pair => pair.Share)
                .FirstOrDefault();
            if (bestSeraphim.Unit is not null && !results.Any(recommendation =>
                    recommendation.Route.GoalUnitId.Equals(bestSeraphim.Unit.Id,
                        StringComparison.OrdinalIgnoreCase)))
            {
                var seraphimScore = CommunityPriorityScore(goal, bestSeraphim.Unit);
                var insertAt = results.Count;
                for (var i = showGoal ? 1 : 0; i < results.Count; i++)
                {
                    var supportId = results[i].Route.GoalUnitId;
                    if (recipeLegendaryIds.Contains(supportId)) continue;
                    if (CommunityPriorityScore(goal, catalog.Unit(supportId)) >= seraphimScore) continue;
                    insertAt = i;
                    break;
                }
                results.Insert(insertAt, EvaluateCraft(bestSeraphim.Unit, counts, calculator));
            }
        }

        // 유저는 1번부터 순서대로 조합한다 — 위 순위 빌드가 소비할 패를 차감한 잔여
        // 패로 아래 순위의 완료율·남은 조합을 다시 계산해, 같은 카드가 여러 순위에
        // 이중 집계되지 않게 한다(1번 완료 시 2번 %가 부풀어 보이던 문제).
        IReadOnlyDictionary<string, int> cascadeInventory = counts;
        for (var i = 0; i < results.Count; i++)
        {
            results[i] = EvaluateCraft(catalog.Unit(results[i].Route.GoalUnitId),
                cascadeInventory, calculator, out var remainingAfterBuild);
            cascadeInventory = remainingAfterBuild;
        }

        foreach (var recommendation in results)
        {
            if (recommendation.Route.GoalUnitId.Equals(goalUnitId, StringComparison.OrdinalIgnoreCase))
                continue;
            recommendation.ClearEvidence = BuildClearEvidence(
                catalog.Unit(recommendation.Route.GoalUnitId).Rawcodes);
        }
        return results;
    }

    /// <summary>
    /// 자동 시작 단계: 현재 패로 가장 빨리 완성되는 희귀함 순위. 서로 대안 관계라
    /// 각 희귀함은 전체 패 기준으로 독립 평가한다(순위 캐스케이드 미적용).
    /// 빈 패에서는 재료 수가 적은(빨리 나오는) 순서가 된다.
    /// </summary>
    public IReadOnlyList<Recommendation> RecommendFastRares(
        IEnumerable<InventoryEntry> inventory, int take = 5)
    {
        var counts = inventory
            .GroupBy(x => x.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.Count),
                StringComparer.OrdinalIgnoreCase);
        var calculator = new RecipeCompletionCalculator(catalog.Unit);
        return catalog.AllUnits
            .Where(unit => unit.Tier.Split('[', 2)[0].Trim() == "희귀함")
            .Where(unit => unit.Recipe.Count > 0)
            .DistinctBy(unit => unit.Id)
            .Select(unit => EvaluateCraft(unit, counts, calculator))
            .Where(recommendation => recommendation.RecipeProgress.RequiredLeafCount > 0)
            .OrderByDescending(recommendation => recommendation.RecipeProgress.CompletionRatio)
            .ThenBy(recommendation => recommendation.RecipeProgress.RequiredLeafCount)
            .Take(Math.Max(1, take))
            .ToList();
    }

    // 배(해적선 060h·고대의 배 Y50h)는 일반 재료 조합으로 만들 수 없는 특수 획득물이다.
    // 좀비·토큰·확장팩·초월쿠마 같은 기타 재료는 게임 안에서 정상 획득 루트가 있으므로
    // 게이트하지 않는다(전부 게이트하면 초월 후보가 통째로 사라진다).
    private static readonly string[] ShipPrerequisiteRawcodes = ["060h", "Y50h"];

    /// <summary>
    /// 레시피 트리가 배·아이템을 요구하는 유닛은 그 재료가 실제 패에 있을 때만
    /// 지원 후보로 노출한다. 중간 재료를 이미 보유했다면 그 하위 트리는 따지지 않는다.
    /// 특포(POINT)는 패로 안 잡혀 게이트하지 않고, 카드 경고만 띄운다.
    /// </summary>
    private bool MeetsOwnedPrerequisites(UnitDefinition unit,
        IReadOnlyDictionary<string, int> inventory) =>
        SpecialRequirementSatisfied(unit, inventory,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 조합에 특포가 들어가는 상위(알비다 제한 등)는 첫 상위 특강 예산을 빼고
    /// 남는 특포가 있을 때만 추천한다. 첫 상위는 거의 특강을 하므로, 특성공학이
    /// 아니고 특포 잔량이 안 보이면 추천하지 않는다.
    /// </summary>
    private bool AvoidTraitPointCraftWithoutEconomy(
        NavigationOption navigation, UnitDefinition unit,
        IReadOnlyDictionary<string, int> inventory)
    {
        var needed = TraitPointCost(unit);
        if (needed <= 0) return false;
        if (navigation.Id.Equals("AlliedForces.TraitEngineering",
                StringComparison.OrdinalIgnoreCase))
            return false;
        return OwnedTraitPoints(inventory) - FirstTopTraitEnhanceCost < needed;
    }

    private int TraitPointCost(UnitDefinition unit)
    {
        var total = 0;
        foreach (var (childId, required) in unit.Recipe)
        {
            var child = catalog.Unit(childId);
            if (child.Rawcodes.Contains("POINT", StringComparer.Ordinal) ||
                childId.Equals("POINT", StringComparison.OrdinalIgnoreCase) ||
                childId.EndsWith(":POINT", StringComparison.OrdinalIgnoreCase))
                total += required;
        }
        return total;
    }

    private static int OwnedTraitPoints(IReadOnlyDictionary<string, int> inventory) =>
        Math.Max(inventory.GetValueOrDefault("POINT"),
            inventory.GetValueOrDefault("rawcode:POINT"));

    private const int FirstTopTraitEnhanceCost = 4;

    private static bool IsSpecialPrerequisite(UnitDefinition unit) =>
        unit.Rawcodes.Any(code =>
            ShipPrerequisiteRawcodes.Contains(code, StringComparer.Ordinal)) ||
        BaseTier(unit.Tier) == "아이템";

    private bool SpecialRequirementSatisfied(UnitDefinition unit,
        IReadOnlyDictionary<string, int> inventory, HashSet<string> visiting)
    {
        if (!visiting.Add(unit.Id)) return true;
        foreach (var childId in unit.Recipe.Keys)
        {
            var child = catalog.Unit(childId);
            if (IsResourcePseudo(child)) continue;
            if (inventory.GetValueOrDefault(child.Id) > 0 ||
                inventory.GetValueOrDefault(childId) > 0) continue;
            if (IsSpecialPrerequisite(child)) return false;
            if (!SpecialRequirementSatisfied(child, inventory, visiting)) return false;
        }
        return true;
    }

    private List<string> CollectMissingSpecials(UnitDefinition unit,
        IReadOnlyDictionary<string, int> inventory)
    {
        var missing = new List<string>();
        Walk(unit, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return missing.Distinct(StringComparer.CurrentCulture).ToList();

        void Walk(UnitDefinition current, HashSet<string> visiting)
        {
            if (!visiting.Add(current.Id)) return;
            foreach (var (childId, required) in current.Recipe)
            {
                var child = catalog.Unit(childId);
                if (child.Rawcodes.Contains("POINT", StringComparer.Ordinal))
                {
                    if (inventory.GetValueOrDefault(child.Id) < required)
                        missing.Add($"특성포인트 {required}개");
                    continue;
                }
                if (IsResourcePseudo(child)) continue;
                if (inventory.GetValueOrDefault(child.Id) > 0 ||
                    inventory.GetValueOrDefault(childId) > 0) continue;
                if (IsSpecialPrerequisite(child))
                    missing.Add(child.Name);
                else
                    Walk(child, visiting);
            }
        }
    }

    // 후보의 조합 경로가 소비해야 하는 배 코드 목록(보유한 중간재 하위는 제외).
    // 한 패스 안에서 인벤토리가 고정이므로 유닛별로 캐시한다.
    private readonly Dictionary<string, List<string>> _shipNeedCache =
        new(StringComparer.OrdinalIgnoreCase);

    private List<string> RequiredShipCodes(UnitDefinition unit,
        IReadOnlyDictionary<string, int> inventory)
    {
        if (_shipNeedCache.TryGetValue(unit.Id, out var cached)) return cached;
        var needed = new List<string>();
        Walk(unit, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _shipNeedCache[unit.Id] = needed;
        return needed;

        void Walk(UnitDefinition current, HashSet<string> visiting)
        {
            if (!visiting.Add(current.Id)) return;
            foreach (var childId in current.Recipe.Keys)
            {
                var child = catalog.Unit(childId);
                if (IsResourcePseudo(child)) continue;
                // 배 재료는 보유 여부와 무관하게 소요로 센다 — 보유한 배가 바로
                // 이 후보가 소모할 자원이다. 배가 아닌 보유 중간재는 이미 완성돼
                // 있으므로(그 배도 그때 소모됨) 하위를 더 세지 않는다.
                var ship = child.Rawcodes.FirstOrDefault(code =>
                    ShipPrerequisiteRawcodes.Contains(code, StringComparer.Ordinal));
                if (ship is not null)
                {
                    needed.Add(ship);
                    continue;
                }
                if (inventory.GetValueOrDefault(child.Id) > 0) continue;
                Walk(child, visiting);
            }
        }
    }

    // 물딜·마딜 공용 역할 파이프라인. 스턴 1.4 축은 공통이고, 마감 깎기만
    // 물딜(방깎 211)과 마딜(마방깎 소스 1)로 갈린다.
    private List<Recommendation> OrderStrategySupports(UnitDefinition goal,
        IReadOnlyDictionary<string, int> inventory,
        IReadOnlyCollection<CraftCandidate> candidates,
        int take,
        GoalStrategyProfile strategy,
        bool allowsMultipleTopUnits,
        bool canCraftGoal)
    {
        var selected = new List<CraftCandidate>();
        var remaining = candidates
            .Where(candidate => candidate.Metrics.HasAny ||
                                strategy.FillCommunitySupports &&
                                CommunityPriorityScore(goal, candidate.Unit) > 0)
            .ToList();
        var projected = AggregateStrategyMetrics(inventory);
        if (canCraftGoal && inventory.GetValueOrDefault(goal.Id) <= 0)
            projected += StrategyMetricsFor(goal);

        // 모든 물딜은 상위별 시너지보다 먼저 스턴 1.4를 확보해 라인을 안정시킨다.
        if (strategy.StunBeforeSlow && selected.Count < take &&
            projected.Stun + 0.0001 < strategy.StunTarget)
        {
            var stunSet = ChooseStunSet(goal, strategy, projected, remaining, selected, inventory,
                take - selected.Count);
            foreach (var candidate in stunSet)
                Add(candidate);
        }

        // 조초의 크제/봉히처럼 전용 파트너가 스턴 조합에 이미 포함되지 않았다면
        // 다음 순서에서 한 기만 보완한다.
        while (selected.Count < take &&
               selected.Count(candidate => IsCommunityCore(goal, candidate.Unit)) <
               strategy.CommunityCoreTarget)
        {
            var core = remaining
                .Where(candidate => CommunityPriorityScore(goal, candidate.Unit) > 0)
                .Where(candidate => IsCommunityCore(goal, candidate.Unit))
                .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap))
                .OrderByDescending(candidate => CommunityPriorityScore(goal, candidate.Unit))
                .ThenByDescending(candidate => candidate.Recommendation.RecipeProgress.CompletionRatio)
                .ThenBy(candidate => candidate.Recommendation.RecipeProgress.MissingLeaves
                    .Sum(leaf => leaf.MissingCount))
                .FirstOrDefault();
            if (core is null) break;
            Add(core);
        }

        // 징초는 자체 암브 외에 한 기가 더 있어야 한다는 최신 공략을 별도 목표로
        // 둔다. 아머브레이크를 방깎 수치로 환산하지 않고 기수로만 센다.
        while (selected.Count < take && projected.ArmorBreak + 0.0001 < strategy.ArmorBreakTarget)
        {
            var armorBreak = OrderTowardsTarget(remaining
                    .Where(candidate => candidate.Metrics.ArmorBreak > 0)
                    .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                    .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap)), projected.ArmorBreak,
                strategy.ArmorBreakTarget, candidate => candidate.Metrics.ArmorBreak,
                projected, strategy, goal)
                .FirstOrDefault();
            if (armorBreak is null) break;
            Add(armorBreak);
        }

        while (selected.Count < take && projected.Slow + 0.0001 < strategy.SlowTarget)
        {
            var next = OrderTowardsTarget(remaining
                    .Where(candidate => candidate.Metrics.Slow > 0)
                    .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                    .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap)), projected.Slow,
                strategy.SlowTarget, candidate => candidate.Metrics.Slow, projected, strategy, goal)
                .FirstOrDefault();
            if (next is null) break;
            Add(next);
        }

        if (!strategy.StunBeforeSlow && selected.Count < take &&
            projected.Stun + 0.0001 < strategy.StunTarget)
        {
            var stunSet = ChooseStunSet(goal, strategy, projected, remaining, selected, inventory,
                take - selected.Count);
            foreach (var candidate in stunSet)
                Add(candidate);
        }

        // 크립·정의의 문·해왕류·황금종 동선을 위해 공중이동 한 기를 확보한다.
        // 별도 유틸 전설을 낭비하지 않고, 마감 깎기(물딜은 방깎, 마딜은 마방깎)를
        // 짜는 단계에서 공중이동을 함께 제공하는 후보를 먼저 고른다. 그런 후보가
        // 없을 때만 다른 유효 역할을 겸하는 공중이동 후보로 폴백한다.
        var finisherIsMagic = strategy.ArmorReductionTarget <= 0 &&
                              strategy.MagicArmorReductionTarget > 0;
        while (selected.Count < take &&
               projected.AirMovement + 0.0001 < strategy.AirMovementTarget)
        {
            var compatibleAir = remaining
                .Where(candidate => candidate.Metrics.AirMovement > 0)
                .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap))
                .ToList();
            Func<CraftCandidate, double> finisher = finisherIsMagic
                ? candidate => candidate.Metrics.MagicArmorReduction
                : candidate => candidate.Metrics.ArmorReduction;
            var air = OrderTowardsTarget(
                    compatibleAir.Any(candidate => finisher(candidate) > 0)
                        ? compatibleAir.Where(candidate => finisher(candidate) > 0)
                        : compatibleAir,
                    finisherIsMagic ? projected.MagicArmorReduction : projected.ArmorReduction,
                    finisherIsMagic ? strategy.MagicArmorReductionTarget : strategy.ArmorReductionTarget,
                    finisher,
                    projected, strategy, goal)
                .FirstOrDefault();
            if (air is null) break;
            Add(air);
        }

        while (selected.Count < take &&
               projected.ArmorReduction + 0.0001 < strategy.ArmorReductionTarget)
        {
            var armor = OrderTowardsTarget(remaining
                    .Where(candidate => candidate.Metrics.ArmorReduction > 0)
                    .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                    .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap)), projected.ArmorReduction,
                strategy.ArmorReductionTarget, candidate => candidate.Metrics.ArmorReduction,
                projected, strategy, goal)
                .FirstOrDefault();
            if (armor is null) break;
            Add(armor);
        }

        // 마딜 상위의 마감 깎기: 마방깎 소스를 최소 목표만큼 확보한다. 물딜의 방깎
        // 211과 달리 큰 수치 목표를 두지 않는다(실측상 보편 스택이 아님).
        while (selected.Count < take &&
               projected.MagicArmorReduction + 0.0001 < strategy.MagicArmorReductionTarget)
        {
            var magicArmor = OrderTowardsTarget(remaining
                    .Where(candidate => candidate.Metrics.MagicArmorReduction > 0)
                    .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                    .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap)),
                projected.MagicArmorReduction,
                strategy.MagicArmorReductionTarget,
                candidate => candidate.Metrics.MagicArmorReduction,
                projected, strategy, goal)
                .FirstOrDefault();
            if (magicArmor is null) break;
            Add(magicArmor);
        }

        while (selected.Count < take && projected.BossControl + 0.0001 < strategy.BossControlTarget)
        {
            var boss = OrderTowardsTarget(remaining
                    .Where(candidate => candidate.Metrics.BossControl > 0)
                    .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                    .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap)), projected.BossControl,
                strategy.BossControlTarget, candidate => candidate.Metrics.BossControl, projected, strategy, goal)
                .FirstOrDefault();
            if (boss is null) break;
            Add(boss);
        }

        while (selected.Count < take &&
               projected.BerserkBossControl + 0.0001 < strategy.BerserkBossControlTarget)
        {
            var berserk = OrderTowardsTarget(remaining
                    .Where(candidate => candidate.Metrics.BerserkBossControl > 0)
                    .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                    .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap)), projected.BerserkBossControl,
                strategy.BerserkBossControlTarget, candidate => candidate.Metrics.BerserkBossControl,
                projected, strategy, goal)
                .FirstOrDefault();
            if (berserk is null) break;
            Add(berserk);
        }

        // 딜 밸런스(고인물 검증): 필수 유틸을 채운 뒤 남는 슬롯에서, 상위가 단일이면
        // 끝딜 한 기, 끝딜이면 단일 한 기를 보완한다. 패에 이미 있으면 건너뛴다.
        while (selected.Count < take &&
               projected.FinisherDamage + 0.0001 < strategy.FinisherDamageTarget)
        {
            var finisher = OrderTowardsTarget(remaining
                    .Where(candidate => candidate.Metrics.FinisherDamage > 0)
                    .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                    .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap)),
                projected.FinisherDamage, strategy.FinisherDamageTarget,
                candidate => candidate.Metrics.FinisherDamage, projected, strategy, goal)
                .FirstOrDefault();
            if (finisher is null) break;
            Add(finisher);
        }

        while (selected.Count < take &&
               projected.SingleDamage + 0.0001 < strategy.SingleDamageTarget)
        {
            var single = OrderTowardsTarget(remaining
                    .Where(candidate => candidate.Metrics.SingleDamage > 0)
                    .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                    .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap)),
                projected.SingleDamage, strategy.SingleDamageTarget,
                candidate => candidate.Metrics.SingleDamage, projected, strategy, goal)
                .FirstOrDefault();
            if (single is null) break;
            Add(single);
        }

        if (strategy.OptionalBossSupportAfterCore && selected.Count < take &&
            projected.BossControl <= 0 && projected.BerserkBossControl <= 0)
        {
            var optionalBoss = OrderByCraftDistance(remaining
                    .Where(candidate => candidate.Metrics.BossControl > 0 ||
                                        candidate.Metrics.BerserkBossControl > 0)
                    .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                    .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap)))
                .FirstOrDefault();
            if (optionalBoss is not null) Add(optionalBoss);
        }

        // A researched one-top profile can have mandatory buffers which are not expressible as
        // slow/stun/armor totals (for example Toki's attack speed for Mihawk eternal). Add those
        // only after the measurable core, retaining community priority before craft distance.
        if (strategy.FillCommunitySupports && selected.Count < take)
        {
            foreach (var support in remaining
                         .Where(candidate => CommunityPriorityScore(goal, candidate.Unit) > 0)
                         .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                         .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap))
                         .OrderByDescending(candidate => CommunityPriorityScore(goal, candidate.Unit))
                         .ThenByDescending(candidate =>
                             candidate.Recommendation.RecipeProgress.CompletionRatio)
                         .ThenBy(candidate => candidate.Recommendation.RecipeProgress.MissingLeaves
                             .Sum(leaf => leaf.MissingCount))
                         .Take(take - selected.Count)
                         .ToList())
                Add(support);
        }

        // 다상위 항법에서만 핵심 수치 완성 뒤의 추가 상위 후보를 이어서 보여준다.
        // 패왕의길에서는 불필요한 방깎/보잡을 목표치 이상으로 억지 추천하지 않는다.
        if (allowsMultipleTopUnits)
        {
            foreach (var upper in OrderByCraftDistance(remaining
                         .Where(candidate => IsTopTier(candidate.Unit.Tier))
                         .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, selected, inventory))
                         .Where(candidate => FitsStunCap(projected, candidate, strategy.StunCap)))
                     .Take(take - selected.Count).ToList())
                Add(upper);
        }

        return selected.Select(candidate => candidate.Recommendation).ToList();

        void Add(CraftCandidate candidate)
        {
            selected.Add(candidate);
            remaining.Remove(candidate);
            projected += candidate.Metrics;
        }
    }

    private List<CraftCandidate> ChooseStunSet(UnitDefinition goal,
        GoalStrategyProfile strategy,
        StrategyMetrics projected,
        IReadOnlyCollection<CraftCandidate> remaining,
        IReadOnlyCollection<CraftCandidate> alreadySelected,
        IReadOnlyDictionary<string, int> inventory,
        int availableSlots)
    {
        if (availableSlots <= 0) return [];
        var pool = OrderByCraftDistance(remaining
                .Where(candidate => candidate.Metrics.Stun > 0)
                .Where(candidate => IsCompatibleSupport(goal, candidate.Unit, alreadySelected, inventory)))
            .Take(24)
            .ToList();
        var maximumPicks = Math.Min(3, availableSlots);
        List<CraftCandidate>? best = null;
        var bestDistance = double.MaxValue;
        var bestCoreCoverage = -1;
        var bestSize = int.MaxValue;
        var bestUsefulMetrics = -1;
        var bestOvershoot = true;
        var bestCraftScore = double.MinValue;
        var current = new List<CraftCandidate>();

        Search(0);
        return best is null ? [] : OrderByCraftDistance(best).ToList();

        void Search(int start)
        {
            if (current.Count > 0)
            {
                var totalStun = projected.Stun + current.Sum(candidate => candidate.Metrics.Stun);
                if (totalStun <= strategy.StunCap + 0.0001)
                {
                    var distance = Math.Abs(strategy.StunTarget - totalStun);
                    // 조로의 봉쿠레/크로커다일처럼 전용 홀딩 축은 스턴 세트 안에서
                    // 먼저 충족한다. 그다음 스턴은 최소 기수로 채운다. 같은 1.4라도
                    // 3기 세트는 남은 추천 슬롯에서 이감·방깎·보조딜 자리를 빼앗는다.
                    var coreCoverage = Math.Min(strategy.CommunityCoreTarget,
                        current.Count(candidate => IsCommunityCore(goal, candidate.Unit)));
                    var size = current.Count;
                    var usefulMetrics = current.Sum(candidate =>
                        RemainingUsefulMetricCount(candidate.Metrics, projected, strategy));
                    var overshoot = totalStun > strategy.StunTarget + 0.0001;
                    var craftScore = current.Sum(candidate =>
                        CommunityPriorityScore(goal, candidate.Unit) * 10000 +
                        candidate.Recommendation.RecipeProgress.CompletionRatio * 1000 -
                        candidate.Recommendation.RecipeProgress.MissingLeaves.Sum(leaf => leaf.MissingCount));
                    var sameDistance = Math.Abs(distance - bestDistance) < 0.0001;
                    var sameCore = coreCoverage == bestCoreCoverage;
                    if (distance < bestDistance - 0.0001 ||
                        sameDistance && coreCoverage > bestCoreCoverage ||
                        sameDistance && sameCore && size < bestSize ||
                        sameDistance && sameCore && size == bestSize &&
                        usefulMetrics > bestUsefulMetrics ||
                        sameDistance && sameCore && size == bestSize &&
                        usefulMetrics == bestUsefulMetrics && bestOvershoot && !overshoot ||
                        sameDistance && sameCore && size == bestSize &&
                        usefulMetrics == bestUsefulMetrics && bestOvershoot == overshoot &&
                        craftScore > bestCraftScore)
                    {
                        best = current.ToList();
                        bestDistance = distance;
                        bestCoreCoverage = coreCoverage;
                        bestSize = size;
                        bestUsefulMetrics = usefulMetrics;
                        bestOvershoot = overshoot;
                        bestCraftScore = craftScore;
                    }
                }
            }

            if (current.Count >= maximumPicks) return;
            for (var index = start; index < pool.Count; index++)
            {
                var candidate = pool[index];
                if (!IsCompatibleSupport(goal, candidate.Unit,
                        alreadySelected.Concat(current).ToList(), inventory)) continue;
                var totalStun = projected.Stun + current.Sum(item => item.Metrics.Stun) +
                                candidate.Metrics.Stun;
                if (totalStun > strategy.StunCap + 0.0001) continue;
                current.Add(candidate);
                Search(index + 1);
                current.RemoveAt(current.Count - 1);
            }
        }
    }

    private static bool FitsStunCap(StrategyMetrics projected, CraftCandidate candidate,
        double stunCap) =>
        candidate.Metrics.Stun <= 0 ||
        projected.Stun + candidate.Metrics.Stun <= stunCap + 0.0001;

    private static IOrderedEnumerable<CraftCandidate> OrderByCraftDistance(
        IEnumerable<CraftCandidate> candidates) => candidates
        .OrderByDescending(candidate => candidate.Recommendation.RecipeProgress.CompletionRatio)
        .ThenBy(candidate => candidate.Recommendation.RecipeProgress.MissingLeaves.Sum(leaf => leaf.MissingCount))
        .ThenBy(candidate => candidate.Recommendation.RecipeProgress.RequiredLeafCount)
        .ThenByDescending(candidate => candidate.Metrics.Total)
        .ThenBy(candidate => candidate.Recommendation.Route.Name, StringComparer.CurrentCulture);

    private IOrderedEnumerable<CraftCandidate> OrderTowardsTarget(
        IEnumerable<CraftCandidate> candidates,
        double current,
        double target,
        Func<CraftCandidate, double> contribution,
        StrategyMetrics projected,
        GoalStrategyProfile strategy,
        UnitDefinition goal) => candidates
        // 커뮤니티 점수와 상위별 유효 복합 유틸을 먼저 보고, 같은 조건에서
        // 현재 패 제작 거리와 목표 초과량을 비교한다.
        .OrderByDescending(candidate => CommunityPriorityScore(goal, candidate.Unit))
        .ThenByDescending(candidate => RemainingUsefulMetricCount(candidate.Metrics, projected, strategy))
        .ThenByDescending(candidate => candidate.Recommendation.RecipeProgress.CompletionRatio)
        .ThenBy(candidate => candidate.Recommendation.RecipeProgress.MissingLeaves.Sum(leaf => leaf.MissingCount))
        .ThenBy(candidate => candidate.Recommendation.RecipeProgress.RequiredLeafCount)
        .ThenBy(candidate => current + contribution(candidate) + 0.0001 < target ? 1 : 0)
        .ThenBy(candidate => Math.Abs(target - current - contribution(candidate)))
        .ThenBy(candidate => candidate.Recommendation.Route.Name, StringComparer.CurrentCulture);

    // 2026-08-17 유저 검증(야마토+바헌): 비비 변화는 빌드를 다 짜고 패가 남을 때
    // 목박으로 들어가는 마무리 필러라, 클리어 동시출현(1상위 실측 51%)이 우선순위를
    // 과대평가한다. 꽉 짠 빌드에서도 54%(빌드 크기 신호 무효), 야마토 특이도 lift
    // 2.4(목표 특이도 신호 무효)라 종료 스냅샷 구조만으로는 걸러지지 않는다 —
    // 이런 유닛은 실측 채용률 대신 수작업 커뮤니티 테이블 순위로 후퇴시킨다.
    private static readonly IReadOnlySet<string> LeftoverFillerRawcodes =
        new HashSet<string>(StringComparer.Ordinal) { "W50h" }; // 비비 변화

    // 가이드 43747 특강 노트 기준 "특강(필수)" 상위 — 특성포인트가 없으면 제 성능이
    // 안 나온다. 키자루 초월은 특강이 특포 반복 소모형 스킬강화라(특성공학 항법에서
    // 특포를 계속 빨아들임) 이 목록과 특포 경합이 생긴다. 실측 검증: 키자루 다상위
    // 21판의 동반 상위에 이 목록 유닛이 0회. 시키(B40h)는 마딜 용도는 특강 불필요라 제외.
    private static readonly IReadOnlySet<string> TraitHungryTopRawcodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "E90H", // 도플라밍고 초월
            "490H", // 바질호킨스 초월
            "290H", // 사보 초월
            "DB0H", // 야마토 초월
            "B90H", // 우솝 초월
            "F90H", // 조로 초월
            "2B0H", // 스네이크맨 초월
            "H90H", // 상디 초월
            "N50H", // 타시기 초월
            "940h", // 레일리 불멸
            "C50h", // 핸콕 영원
            "R80h", // 오뎅 영원
            "O80h", // 마르코(인간폼) 제한
            "Q80h"  // 알비다 제한(조합에 특포 4개 필요)
        };

    private ClearEvidence? BuildClearEvidence(IReadOnlyList<string> candidateRawcodes)
    {
        if (_activeClearProfile is null) return null;
        var share = candidateRawcodes
            .Select(code => _activeClearProfile.SupportShare.GetValueOrDefault(code))
            .DefaultIfEmpty()
            .Max();
        if (share <= 0) return null;
        return new ClearEvidence(_activeClearProfile.SampleCount,
            (int)Math.Round(share * 100, MidpointRounding.AwayFromZero),
            _activeClearProfile.Scope, _activeAnchorLabel);
    }

    private int CommunityPriorityScore(UnitDefinition goal, UnitDefinition candidate)
    {
        // 신+ 클리어 데이터가 충분하면 실측 채용률(보유 앵커가 있으면 조건부 재집계)을
        // 쓰고, 표본이 부족한 목표는 기존 수작업 커뮤니티 테이블로 후퇴한다. 역할 코어
        // 우선 구조는 그대로이며 이 점수는 같은 역할 버킷 안의 순서만 바꾼다.
        var isLeftoverFiller = candidate.Rawcodes.Any(LeftoverFillerRawcodes.Contains);
        var clearScore = isLeftoverFiller || _activeClearProfile is null
            ? (int?)null
            : (int)Math.Round(candidate.Rawcodes
                .Select(code => _activeClearProfile.SupportShare.GetValueOrDefault(code))
                .DefaultIfEmpty()
                .Max() * 100, MidpointRounding.AwayFromZero);
        if (clearScore is not null)
            return LiveStats.ApplyWeight(clearScore.Value, _liveStats.WeightFor(candidate.Id));

        IReadOnlyDictionary<string, int>? priorities = null;
        if (goal.Id.Equals("yamato_transcendent", StringComparison.OrdinalIgnoreCase) ||
            goal.Rawcodes.Contains("DB0H", StringComparer.Ordinal))
            priorities = YamatoCommunityPriority;
        else if (goal.Rawcodes.Contains("B90H", StringComparer.Ordinal))
            priorities = UsoppCommunityPriority;
        else if (goal.Rawcodes.Contains("F90H", StringComparer.Ordinal))
            priorities = ZoroCommunityPriority;
        else if (goal.Rawcodes.Contains("A90H", StringComparer.Ordinal))
            priorities = JinbeCommunityPriority;
        if (priorities is null) return 0;
        return candidate.Rawcodes.Select(rawcode => priorities.GetValueOrDefault(rawcode))
            .DefaultIfEmpty().Max();
    }

    private static bool IsCommunityCore(UnitDefinition goal, UnitDefinition candidate) =>
        goal.Rawcodes.Contains("F90H", StringComparer.Ordinal) &&
        candidate.Rawcodes.Any(rawcode => rawcode is "F50h" or "O30h");

    private static int RemainingUsefulMetricCount(StrategyMetrics metrics,
        StrategyMetrics projected,
        GoalStrategyProfile strategy)
    {
        var count = 0;
        if (projected.Slow + 0.0001 < strategy.SlowTarget && metrics.Slow > 0) count++;
        if (projected.ArmorBreak + 0.0001 < strategy.ArmorBreakTarget && metrics.ArmorBreak > 0) count++;
        if (projected.Stun + 0.0001 < strategy.StunTarget && metrics.Stun > 0) count++;
        if (projected.ArmorReduction + 0.0001 < strategy.ArmorReductionTarget &&
            metrics.ArmorReduction > 0) count++;
        if (projected.MagicArmorReduction + 0.0001 < strategy.MagicArmorReductionTarget &&
            metrics.MagicArmorReduction > 0) count++;
        if (projected.SingleDamage + 0.0001 < strategy.SingleDamageTarget &&
            metrics.SingleDamage > 0) count++;
        if (projected.FinisherDamage + 0.0001 < strategy.FinisherDamageTarget &&
            metrics.FinisherDamage > 0) count++;
        if (projected.AirMovement + 0.0001 < strategy.AirMovementTarget &&
            metrics.AirMovement > 0) count++;
        if (projected.BossControl + 0.0001 < strategy.BossControlTarget && metrics.BossControl > 0) count++;
        if (projected.BerserkBossControl + 0.0001 < strategy.BerserkBossControlTarget &&
            metrics.BerserkBossControl > 0) count++;
        return count;
    }

    private bool IsCompatibleSupport(UnitDefinition goal,
        UnitDefinition candidate,
        IReadOnlyCollection<CraftCandidate> selected,
        IReadOnlyDictionary<string, int> inventory)
    {
        // 배 하나는 유닛 하나에만 들어간다. 이미 선택된 후보들이 보유한 배를 다
        // 예약했다면 추가 배 소비 후보는 함께 추천하지 않는다(유저 보고:
        // 해적선 1척에 모비딕·에넬이 동시 추천되던 문제).
        var candidateShips = RequiredShipCodes(candidate, inventory);
        if (candidateShips.Count > 0)
        {
            foreach (var shipCode in candidateShips.Distinct())
            {
                var ownedShips = inventory.GetValueOrDefault("rawcode:" + shipCode);
                var reserved = selected.Sum(item =>
                    RequiredShipCodes(item.Unit, inventory).Count(code =>
                        code.Equals(shipCode, StringComparison.Ordinal)));
                var required = candidateShips.Count(code =>
                    code.Equals(shipCode, StringComparison.Ordinal));
                if (reserved + required > ownedShips) return false;
            }
        }

        if (goal.Rawcodes.Contains("F90H", StringComparer.Ordinal) &&
            candidate.Rawcodes.Any(rawcode => rawcode is "F50h" or "O30h"))
        {
            var zoroHolderCodes = new[] { "F50h", "O30h" };
            var alreadyHasHolder = inventory.Keys
                                       .Where(id => inventory.GetValueOrDefault(id) > 0)
                                       .Select(catalog.Unit)
                                       .Any(unit => unit.Rawcodes.Any(zoroHolderCodes.Contains)) ||
                                   selected.Any(item => item.Unit.Rawcodes.Any(zoroHolderCodes.Contains));
            if (alreadyHasHolder) return false;
        }

        if (!goal.Id.Equals("yamato_transcendent", StringComparison.OrdinalIgnoreCase)) return true;
        var mainHolders = new[] { "dragon_legend", "bartolomeo_legend", "ivankov_hidden" };
        if (!mainHolders.Contains(candidate.Id, StringComparer.OrdinalIgnoreCase)) return true;

        var hasMainHolder = mainHolders.Any(id => inventory.GetValueOrDefault(id) > 0) ||
                            selected.Any(item => mainHolders.Contains(item.Unit.Id,
                                StringComparer.OrdinalIgnoreCase));
        if (hasMainHolder) return false;

        if (!candidate.Id.Equals("ivankov_hidden", StringComparison.OrdinalIgnoreCase)) return true;
        var hasGreenBlood = inventory.GetValueOrDefault("item_greenblood") > 0;
        var hasMobyDick = inventory.GetValueOrDefault("mobydick") > 0 ||
                          selected.Any(item => item.Unit.Id.Equals("mobydick",
                              StringComparison.OrdinalIgnoreCase));
        return hasGreenBlood && hasMobyDick;
    }

    private StrategyMetrics AggregateStrategyMetrics(IReadOnlyDictionary<string, int> inventory)
    {
        var result = new StrategyMetrics();
        foreach (var (unitId, count) in inventory.Where(pair => pair.Value > 0))
        {
            var unit = catalog.Unit(unitId);
            if (!CountsAsCompletedSupport(unit.Tier)) continue;
            result += StrategyMetricsFor(unit) * count;
        }
        return result;
    }

    private static StrategyMetrics StrategyMetricsFor(UnitDefinition unit)
    {
        var slow = AbilitySignedTotal(unit, "이동속도 감소", "발동이동속도 감소");
        var stun = AbilityTotal(unit, "스턴");
        var armor = AbilityTotal(unit, "방어력 감소", "발동방어력 감소", "중첩방어력 감소");
        var magicArmor = AbilityTotal(unit, "마법방어력 감소");
        var armorBreak = AbilityPresenceOrTotal(unit, "아머브레이크", "단일아머브레이크");
        var airMovement = AbilityPresenceOrTotal(unit, "공중이동");
        var boss = AbilityTotal(unit, "보스 잡기");
        var berserkBoss = AbilityTotal(unit, "광폭화 잡기");
        // 딜 유형은 수치보다 "몇 기 보유"가 중요하므로 존재를 1로 센다.
        var singleDamage = AbilityPresenceOrTotal(unit, "단일") > 0 ? 1 : 0;
        var finisherDamage = AbilityPresenceOrTotal(unit, "끝딜") > 0 ? 1 : 0;
        return new StrategyMetrics(slow, stun, armor, armorBreak, airMovement, boss, berserkBoss,
            magicArmor, singleDamage, finisherDamage);
    }

    private static GoalStrategyProfile? StrategyProfileFor(UnitDefinition goal,
        double committedStun = 0, string buildVariant = BuildVariants.AutoId)
    {
        var rawcode = goal.Rawcodes.FirstOrDefault() ?? "";
        // 2.314 recent-community profiles. A zero target means the selected top unit can
        // clear without reserving a separate support slot; the core 102 slow / 1.4 stun /
        // 211 armor targets still take precedence. Unknown physical tops stay conservative.
        if (goal.Id.Equals("yamato_transcendent", StringComparison.OrdinalIgnoreCase) ||
            rawcode.Equals("DB0H", StringComparison.Ordinal))
            return new GoalStrategyProfile(0, 0, StunBeforeSlow: true);
        if (rawcode.Equals("B90H", StringComparison.Ordinal)) // Usopp transcendent: self boss/berserk.
            return new GoalStrategyProfile(1, 1);
        if (rawcode.Equals("F90H", StringComparison.Ordinal)) // Zoro: Crocodile limit OR Bon Clay hidden.
            return new GoalStrategyProfile(0, 0, true, CommunityCoreTarget: 1);
        if (rawcode.Equals("A90H", StringComparison.Ordinal)) // Jinbe: self + one external armor break.
            return new GoalStrategyProfile(1, 1, ArmorBreakTarget: 2);
        if (rawcode.Equals("B50h", StringComparison.Ordinal)) // Cavendish eternal: no forced extra support.
            return new GoalStrategyProfile(0, 0);
        if (rawcode.Equals("490H", StringComparison.Ordinal)) // Basil transcendent: recent reports need help.
            return new GoalStrategyProfile(2, 1);
        if (rawcode.Equals("I70h", StringComparison.Ordinal)) // Katakuri covers both only with full armor.
            return new GoalStrategyProfile(1, 1);

        // 니카(루초 KB0H·뱀초 KB0H_): 신+ 216판 실측이 두 갈래다 — 이감 버전 167판
        // (스턴 1.6·이감 95)과 노이감 버전. 노이감은 유저 스펙대로 큰 스턴 지원
        // 페어(0.8+0.9 또는 0.9+0.9)를 니카 자체 1.1에 더해 짠다(총 목표 2.9).
        // 빌드 방향을 직접 고르거나, 자동이면 패의 스턴 1.8 이상에서 전환한다.
        if (goal.Rawcodes.Any(code => code is "KB0H" or "KB0H_"))
        {
            var noSlow = buildVariant == "noslow" ||
                         buildVariant != "slow" && committedStun >= NikaNoSlowCommitStun;
            return noSlow
                ? new GoalStrategyProfile(1, 1, SlowTarget: 40, StunTarget: 2.9, StunCap: 3.0)
                : new GoalStrategyProfile(1, 1, StunTarget: 1.6, StunCap: 1.7);
        }

        // 마딜 상위는 바제스 가능이라도 물딜 방깎 파이프라인을 타면 안 된다.
        // 신+ 상디초월 실측(92판): 이감 145·스턴 1.4·방깎 0·보잡/광잡 각 1.
        // 공속·마뎀증 버퍼는 수치 목표 대신 클리어 채용률 정렬로 채운다.
        // 딜 밸런스(고인물 검증): 상위가 단일이면 끝딜 한 기, 끝딜이면 단일 한 기를
        // 보완해 딜 구성을 맞춘다. 상위가 양쪽 다 갖추면 보완 목표는 없다.
        if (IsMagicDamageTier(goal.Tier))
        {
            var goalSingle = AbilityTotal(goal, "단일");
            var goalFinisher = AbilityTotal(goal, "끝딜");
            return new GoalStrategyProfile(1, 1, FillCommunitySupports: true,
                ArmorReductionTarget: 0, MagicArmorReductionTarget: MagicArmorSourceTarget,
                SingleDamageTarget: goalFinisher > 0 && goalSingle <= 0 ? 1 : 0,
                FinisherDamageTarget: goalSingle > 0 && goalFinisher <= 0 ? 1 : 0);
        }

        var isPhysicalTop = goal.OfficialAbilities.Any(ability =>
            ability.Name.Equals("바제스", StringComparison.Ordinal) &&
            ability.DisplayValue.Equals("가능", StringComparison.Ordinal));
        return isPhysicalTop ? new GoalStrategyProfile(1, 1) : null;
    }

    private static bool IsMagicDamageTier(string tier) => DamageTiers.IsMagic(tier);

    /// <summary>신+ 오로성(판별 전역 변수)에 맞춰 역할 목표를 보정한다.</summary>
    private static GoalStrategyProfile? ApplyGorosei(GoalStrategyProfile? strategy,
        GoroseiMode gorosei)
    {
        if (strategy is null || gorosei == GoroseiMode.None) return strategy;
        var adjusted = strategy.Value with
        {
            SlowTarget = GoroseiEffects.AdjustSlowTarget(strategy.Value.SlowTarget, gorosei),
            ArmorReductionTarget =
                GoroseiEffects.AdjustArmorTarget(strategy.Value.ArmorReductionTarget, gorosei),
            MagicArmorReductionTarget =
                GoroseiEffects.AdjustMagicArmorTarget(strategy.Value.MagicArmorReductionTarget,
                    gorosei)
        };
        // 새턴은 아군 공격력·폭뎀을 깎으므로 마딜 상위는 단일·끝딜을 모두 갖춘다.
        // (상위 자체 보유분은 projected에 합산되어 자동 충족된다.)
        if (gorosei == GoroseiMode.Saturn && adjusted.MagicArmorReductionTarget > 0)
            adjusted = adjusted with
            {
                SingleDamageTarget = Math.Max(1, adjusted.SingleDamageTarget),
                FinisherDamageTarget = Math.Max(1, adjusted.FinisherDamageTarget)
            };
        return adjusted;
    }

    private static double AbilityTotal(UnitDefinition unit, params string[] abilityNames) =>
        unit.OfficialAbilities
            .Where(ability => abilityNames.Contains(ability.Name, StringComparer.Ordinal))
            .Sum(ability => AbilityNumber(ability.DisplayValue));

    private static double AbilitySignedTotal(UnitDefinition unit, params string[] abilityNames) =>
        unit.OfficialAbilities
            .Where(ability => abilityNames.Contains(ability.Name, StringComparer.Ordinal))
            .Sum(ability => double.TryParse(ability.DisplayValue, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) ? value : 0);

    private static double AbilityPresenceOrTotal(UnitDefinition unit, params string[] abilityNames) =>
        unit.OfficialAbilities
            .Where(ability => abilityNames.Contains(ability.Name, StringComparer.Ordinal))
            .Sum(ability => Math.Max(1, AbilityNumber(ability.DisplayValue)));

    private static double AbilityNumber(string displayValue)
    {
        if (displayValue.Equals("가능", StringComparison.Ordinal)) return 1;
        return double.TryParse(displayValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Abs(value)
            : 0;
    }

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();

    public IReadOnlyList<string> RecipeLegendaryUnitIds(string goalUnitId) =>
        RecipeLegendaryIds(catalog.Unit(goalUnitId));

    /// <summary>
    /// 초월 조합식에 들어 있는 전설(직접 재료와 그 아래 트리).
    /// 스토리 진행을 위해 후보 보드에서 역할 패키지보다 앞에 둔다.
    /// </summary>
    private List<string> RecipeLegendaryIds(UnitDefinition goal)
    {
        if (BaseTier(goal.Tier) != "초월") return [];
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string unitId)
        {
            if (!seen.Add(unitId)) return;
            var unit = catalog.Unit(unitId);
            if (BaseTier(unit.Tier) == "전설")
                ids.Add(unit.Id);
            foreach (var childId in unit.Recipe.Keys)
                Visit(childId);
        }
        foreach (var childId in goal.Recipe.Keys)
            Visit(childId);
        return ids;
    }

    // 아이템(흑도 슈스이 방깎 6 등)도 완성 전력으로 역할 목표에 합산한다.
    private static bool CountsAsCompletedSupport(string tier) =>
        BaseTier(tier) is "전설" or "히든" or "변화된" or "왜곡됨" or "함선" or "해적선" or
            "초월" or "불멸" or "영원" or "제한됨" or "아이템";

    /// <summary>
    /// 긴급소집 와일드카드(특별함 선택 3장) 사용처. 추천 빌드(목표 카드 우선)에
    /// 부족한 특별함 재료부터 제작 수고가 큰 순으로 배정하고, 남는 장수는 신+
    /// 클리어 최종 조합에 남는 유틸 특별함(채용률 10퍼센트 이상)으로 채운다.
    /// RecommendNearestCrafts 직후 같은 패스에서 호출해야 조건부 프로필을 공유한다.
    /// </summary>
    public IReadOnlyList<EmergencySummonAdvice> RecommendEmergencySummons(
        IReadOnlyList<Recommendation> recommendations,
        IEnumerable<InventoryEntry> inventory)
    {
        const int wildcards = 3;
        var picks = new List<EmergencySummonAdvice>();
        var allocated = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var recommendation in recommendations)
        {
            foreach (var step in recommendation.RemainingCraftSteps
                         .Where(step => BaseTier(step.Tier) == "특별함")
                         .Where(step => step.MissingCount > 0)
                         .OrderByDescending(step => step.Ingredients
                             .Sum(ingredient => ingredient.RequiredCount)))
            {
                var used = picks.Sum(pick => pick.Count);
                if (used >= wildcards) return picks;
                var already = allocated.GetValueOrDefault(step.UnitId);
                var take = Math.Min(step.MissingCount - already, wildcards - used);
                if (take <= 0) continue;
                allocated[step.UnitId] = already + take;
                picks.Add(new EmergencySummonAdvice(step.UnitId, step.Name, take,
                    $"{recommendation.Route.Name} 재료"));
            }
        }

        var remaining = wildcards - picks.Sum(pick => pick.Count);
        if (remaining <= 0 || _activeClearProfile is null) return picks;
        var owned = inventory
            .GroupBy(entry => entry.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count),
                StringComparer.OrdinalIgnoreCase);
        foreach (var (unit, share) in catalog.AllUnits
                     .Where(unit => BaseTier(unit.Tier) == "특별함")
                     .Where(unit => owned.GetValueOrDefault(unit.Id) <= 0 &&
                                    !allocated.ContainsKey(unit.Id))
                     .Select(unit => (unit, Share: unit.Rawcodes
                         .Select(code => _activeClearProfile.SupportShare.GetValueOrDefault(code))
                         .DefaultIfEmpty()
                         .Max()))
                     .Where(pair => pair.Share >= 0.10)
                     .OrderByDescending(pair => pair.Share)
                     .Take(remaining))
            picks.Add(new EmergencySummonAdvice(unit.Id, unit.Name, 1,
                $"신+ 최종 조합 채용률 {Math.Round(share * 100)}퍼센트"));
        return picks;
    }

    private Recommendation EvaluateCraft(UnitDefinition unit,
        IReadOnlyDictionary<string, int> inventory,
        RecipeCompletionCalculator calculator) =>
        EvaluateCraft(unit, inventory, calculator, out _);

    /// <summary>remainingAfterBuild = 이 유닛의 빌드가 소비하고 남는 패(순위 캐스케이드용).</summary>
    private Recommendation EvaluateCraft(UnitDefinition unit,
        IReadOnlyDictionary<string, int> inventory,
        RecipeCompletionCalculator calculator,
        out Dictionary<string, int> remainingAfterBuild)
    {
        var progress = calculator.Calculate([unit.Id], inventory);
        var missing = progress.MissingLeaves.FirstOrDefault();
        var availability = inventory
            .Where(pair => pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var recipeTree = BuildRecipeTree(unit.Id, 1, availability,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        // BuildRecipeTree가 소비하고 남긴 잔여 패 — 아래 순위 완료율 계산의 입력이 된다.
        remainingAfterBuild = availability;
        var remainingSteps = BuildRemainingCraftSteps(recipeTree, inventory, calculator);
        var missingSpecials = CollectMissingSpecials(unit, inventory);
        var nextAction = missingSpecials.Count > 0
            ? "먼저 필요: " + string.Join(" · ", missingSpecials) +
              " — 없으면 마지막에 조합이 막힙니다"
            : missing is not null
                ? "부족한 패: " + string.Join(" · ", progress.MissingLeaves
                    .Select(leaf => $"{leaf.Name} ×{leaf.MissingCount}"))
                : remainingSteps.Count > 0
                    ? $"최하위 재료 확보 — 아래 {remainingSteps.Count}단계를 차례로 조합하면 완성"
                    : "지금 바로 조합할 수 있습니다.";
        return new Recommendation
        {
            Route = new RouteDefinition
            {
                Id = "craft:" + unit.Id,
                GoalUnitId = unit.Id,
                Name = unit.Name
            },
            Score = progress.CompletionRatio * 100,
            MissingUnits = progress.MissingLeaves.Select(leaf => leaf.Name).ToList(),
            Warnings = missingSpecials,
            MissingSpecials = missingSpecials,
            NextAction = nextAction,
            RecipeProgress = progress,
            CompositionUnits =
            [
                new CompositionUnitDetail
                {
                    UnitId = unit.Id,
                    Name = unit.Name,
                    Tier = unit.Tier,
                    Image = unit.Image,
                    RequiredCount = 1,
                    SuggestedCount = 1,
                    OwnedCount = Math.Min(1, Math.Max(0, inventory.GetValueOrDefault(unit.Id))),
                    IsGoal = true,
                    IsRequired = true,
                    Abilities = unit.OfficialAbilities,
                    Description = unit.Description
                }
            ],
            RecipeTree = recipeTree,
            RemainingCraftSteps = remainingSteps,
            CombineCommands = unit.CombineCommands
        };
    }

    private RecipeTreeNode BuildRecipeTree(string unitId, int requiredCount,
        IDictionary<string, int> availability, HashSet<string> visiting)
    {
        var unit = catalog.Unit(unitId);
        var available = Math.Max(0, availability.TryGetValue(unit.Id, out var count) ? count : 0);
        var owned = Math.Min(requiredCount, available);
        availability[unit.Id] = available - owned;
        var remaining = requiredCount - owned;
        var children = new List<RecipeTreeNode>();
        if (remaining > 0 && visiting.Add(unitId))
        {
            foreach (var (childId, childCount) in unit.Recipe
                         .Where(pair => pair.Value > 0)
                         .Where(pair => !IsResourcePseudo(catalog.Unit(pair.Key))))
            {
                var total = childCount > int.MaxValue / Math.Max(1, remaining)
                    ? int.MaxValue
                    : childCount * remaining;
                children.Add(BuildRecipeTree(childId, total, availability, visiting));
            }
            visiting.Remove(unitId);
        }

        return new RecipeTreeNode
        {
            UnitId = unit.Id,
            Name = unit.Name,
            Tier = unit.Tier,
            Image = unit.Image,
            RequiredCount = requiredCount,
            OwnedCount = owned,
            Children = children
        };
    }

    private List<RecipeCraftStep> BuildRemainingCraftSteps(RecipeTreeNode root,
        IReadOnlyDictionary<string, int> inventory, RecipeCompletionCalculator calculator)
    {
        var totals = new Dictionary<string, (RecipeTreeNode Node, long Required, long Owned)>(
            StringComparer.OrdinalIgnoreCase);
        var ingredientTotals = new Dictionary<string,
            Dictionary<string, (RecipeTreeNode Node, long Required, int SelectionOrder)>>(StringComparer.OrdinalIgnoreCase);
        // 목표 자신의 최종 조합도 하나의 단계다 — 재료가 다 모였을 때 "어떤 유닛을
        // 선택해 무슨 키를 누르는지"까지 카드에서 보이게 루트부터 방문한다(유저 요청).
        Visit(root);

        return totals.Values
            .Select(value => new RecipeCraftStep
            {
                UnitId = value.Node.UnitId,
                Name = value.Node.Name,
                Tier = value.Node.Tier,
                Image = value.Node.Image,
                RequiredCount = (int)Math.Min(int.MaxValue, value.Required),
                OwnedCount = (int)Math.Min(int.MaxValue, value.Owned),
                CombineKey = combineHotkeys
                    ?.FindByResult(catalog.Unit(value.Node.UnitId).Rawcodes)?.Key,
                CombineCommands = catalog.Unit(value.Node.UnitId).CombineCommands,
                // 남은 수량 기준 재료 완성률 — 드릴다운에서 하위 단계 %로 보여준다.
                CompletionRatio = calculator.Calculate(
                    Enumerable.Repeat(value.Node.UnitId,
                        (int)Math.Clamp(value.Required - value.Owned, 1, 50)),
                    inventory).CompletionRatio,
                Ingredients = ingredientTotals.GetValueOrDefault(value.Node.UnitId)?.Values
                    .Select(ingredient => new RecipeCraftIngredient
                    {
                        UnitId = ingredient.Node.UnitId,
                        Name = ingredient.Node.Name,
                        Tier = ingredient.Node.Tier,
                        RequiredCount = (int)Math.Min(int.MaxValue, ingredient.Required),
                        SelectionOrder = ingredient.SelectionOrder
                    })
                    .OrderBy(ingredient => ingredient.SelectionOrder)
                    .ToList() ?? []
            })
            .Where(step => step.MissingCount > 0)
            .OrderBy(step => CraftTierOrder(step.Tier))
            .ThenBy(step => step.Name, StringComparer.CurrentCulture)
            .ToList();

        void Visit(RecipeTreeNode node)
        {
            var tierOrder = CraftTierOrder(node.Tier);
            // 안흔함도 실제로 흔함 패를 선택해 조합하는 단계다. 최하위 재료 목록으로만
            // 남기지 말고 티모지지처럼 안흔함부터 모든 조합 단계를 보여준다.
            if (node.Children.Count > 0 && tierOrder >= 1 && node.OwnedCount < node.RequiredCount)
            {
                if (totals.TryGetValue(node.UnitId, out var current))
                    totals[node.UnitId] = (current.Node,
                        Math.Min(int.MaxValue, current.Required + node.RequiredCount),
                        Math.Min(int.MaxValue, current.Owned + node.OwnedCount));
                else
                    totals[node.UnitId] = (node, node.RequiredCount, node.OwnedCount);

                if (!ingredientTotals.TryGetValue(node.UnitId, out var ingredients))
                {
                    ingredients = new Dictionary<string, (RecipeTreeNode Node, long Required, int SelectionOrder)>(
                        StringComparer.OrdinalIgnoreCase);
                    ingredientTotals[node.UnitId] = ingredients;
                }
                for (var selectionOrder = 0; selectionOrder < node.Children.Count; selectionOrder++)
                {
                    var ingredient = node.Children[selectionOrder];
                    if (ingredients.TryGetValue(ingredient.UnitId, out var currentIngredient))
                        ingredients[ingredient.UnitId] = (currentIngredient.Node,
                            Math.Min(int.MaxValue, currentIngredient.Required + ingredient.RequiredCount),
                            Math.Min(currentIngredient.SelectionOrder, selectionOrder));
                    else
                        ingredients[ingredient.UnitId] = (ingredient, ingredient.RequiredCount, selectionOrder);
                }
            }
            foreach (var child in node.Children) Visit(child);
        }
    }

    private static int CraftTierOrder(string tier)
    {
        var baseTier = tier.Split('[', 2)[0].Trim();
        return baseTier switch
        {
            "흔함" => 0,
            "안흔함" => 1,
            "특별함" => 2,
            "희귀함" => 3,
            "신비함" => 4,
            "전설" => 5,
            "히든" => 6,
            "변화된" => 7,
            "왜곡됨" => 8,
            "초월" => 9,
            "불멸" => 10,
            "영원" => 11,
            "제한됨" => 12,
            _ => 4
        };
    }

    private static bool IsRecommendedCraftTier(string tier, bool allowsMultipleTopUnits)
    {
        var baseTier = tier.Split('[', 2)[0].Trim();
        // 세라핌은 그린블러드로 제작하는 지원 유닛 — 어떤 세라핌을 만드는지가
        // 목표별로 갈리므로(상디=S-베어, 징베=S-호크 실측) 조합 후보에 포함한다.
        if (baseTier is "전설" or "히든" or "변화된" or "왜곡됨" or "함선" or "해적선" or "세라핌")
            return true;
        return allowsMultipleTopUnits &&
               baseTier is "신비함" or "초월" or "불멸" or "영원" or "제한됨";
    }

    private static bool IsTopTier(string tier)
    {
        var baseTier = tier.Split('[', 2)[0].Trim();
        return baseTier is "신비함" or "초월" or "불멸" or "영원" or "제한됨";
    }

    private static bool IsResourcePseudo(UnitDefinition unit) =>
        unit.Tier.Equals("자원", StringComparison.OrdinalIgnoreCase) ||
        unit.Rawcodes.Any(rawcode => rawcode is "GOLD" or "LUMBER" or "POINT" or "RANDOM");

    private Recommendation Evaluate(
        RouteDefinition route,
        IReadOnlyDictionary<string, int> inventory,
        IReadOnlyDictionary<UnitRole, double> ownedRoles,
        RecipeCompletionCalculator recipeCalculator)
    {
        var required = route.RequiredUnits.Select(catalog.Unit).ToList();
        var ready = new List<string>();
        var missing = new List<string>();
        var supportProgress = recipeCalculator.Calculate(route.RequiredUnits, inventory);
        var compositionIds = new[] { route.GoalUnitId }.Concat(route.RequiredUnits).ToList();
        var recipeProgress = recipeCalculator.Calculate(compositionIds, inventory);

        foreach (var unit in required)
        {
            if (inventory.GetValueOrDefault(unit.Id) > 0)
            {
                ready.Add(unit.Name);
                continue;
            }

            var unitProgress = recipeCalculator.Calculate([unit.Id], inventory);
            if (unitProgress.RequiredLeafCount <= 1 && unit.Recipe.Count == 0)
            {
                missing.Add(unit.Name);
                continue;
            }
            missing.Add($"{unit.Name} ({unitProgress.OwnedLeafCount}/{unitProgress.RequiredLeafCount})");
        }

        var projectedRoles = new Dictionary<UnitRole, double>(ownedRoles);
        foreach (var unit in required.Where(x => inventory.GetValueOrDefault(x.Id) == 0))
            foreach (var role in unit.Roles)
                projectedRoles[role.Role] = projectedRoles.GetValueOrDefault(role.Role) + role.Value;

        var roleStatuses = route.DesiredRoles
            .Select(x => new RoleStatus(x.Key, projectedRoles.GetValueOrDefault(x.Key), x.Value))
            .ToList();
        var roleFit = roleStatuses.Count == 0 ? 1 : roleStatuses.Average(x => x.Ratio);
        var completion = supportProgress.CompletionRatio;

        var warnings = new List<string>();
        var penalty = 0d;
        foreach (var pair in route.ForbiddenTogether)
        {
            var ids = pair.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length > 1 && ids.All(x => inventory.GetValueOrDefault(x) > 0))
            {
                penalty += 14;
                warnings.Add("홀딩 과투자 가능성");
            }
        }

        var holdingTarget = route.DesiredRoles.GetValueOrDefault(UnitRole.Holding);
        var holdingValue = projectedRoles.GetValueOrDefault(UnitRole.Holding);
        if (holdingTarget > 0 && holdingValue > holdingTarget * 1.65)
        {
            penalty += Math.Min(18, (holdingValue / holdingTarget - 1.65) * 12);
            warnings.Add("홀딩이 충분합니다. 남는 패는 방깎/딜에 투자하세요.");
        }

        var requiredTagsAvailable = route.RequiredTags.Count == 0 || route.RequiredTags.All(tag =>
            inventory.Keys.Select(catalog.Unit).Any(x => x.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        if (!requiredTagsAvailable)
        {
            penalty += 20;
            warnings.Add($"조건 필요: {string.Join(", ", route.RequiredTags.Select(KoreanLabels.Tag))}");
        }

        var score = Math.Clamp(route.BaseScore + completion * CompletionWeight + roleFit * RoleWeight - penalty, 0, 100);
        var weakest = roleStatuses.OrderBy(x => x.Ratio).FirstOrDefault();
        var next = missing.Count > 0
            ? $"우선 확보: {missing[0]}"
            : weakest is { Ratio: < 1 }
                ? $"다음 보강: {RoleLabels.Name(weakest.Role)}"
                : "구성 완성 — 딜/유틸 보강";

        return new Recommendation
        {
            Route = route,
            Score = score,
            ReadyUnits = ready,
            MissingUnits = missing,
            Warnings = warnings.Distinct().ToList(),
            Roles = roleStatuses,
            NextAction = next,
            RecipeProgress = recipeProgress,
            CompositionUnits = CompositionUnits(route, inventory),
            CombineCommands = catalog.Unit(route.GoalUnitId).CombineCommands
        };
    }

    private List<CompositionUnitDetail> CompositionUnits(RouteDefinition route,
        IReadOnlyDictionary<string, int> inventory)
    {
        var specifications = new List<(string UnitId, bool IsGoal, bool IsRequired)>
        {
            (route.GoalUnitId, true, true)
        };
        specifications.AddRange(route.RequiredUnits.Select(id => (id, false, true)));
        specifications.AddRange(route.OptionalUnits.Select(id => (id, false, false)));

        return specifications
            .GroupBy(item => item.UnitId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var unit = catalog.Unit(group.Key);
                var isGoal = group.Any(x => x.IsGoal);
                var isRequired = group.Any(x => x.IsRequired);
                var suggestedCount = group.Count();
                return new CompositionUnitDetail
                {
                    UnitId = unit.Id,
                    Name = unit.Name,
                    Tier = unit.Tier,
                    Image = unit.Image,
                    RequiredCount = isRequired ? group.Count(x => x.IsRequired) : 0,
                    SuggestedCount = suggestedCount,
                    OwnedCount = Math.Min(suggestedCount, Math.Max(0, inventory.GetValueOrDefault(unit.Id))),
                    IsGoal = isGoal,
                    IsRequired = isRequired,
                    Abilities = unit.OfficialAbilities,
                    Description = unit.Description
                };
            })
            .OrderByDescending(unit => unit.IsGoal)
            .ThenByDescending(unit => unit.IsRequired)
            .ThenBy(unit => unit.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private Dictionary<UnitRole, double> AggregateRoles(IReadOnlyDictionary<string, int> inventory)
    {
        var values = new Dictionary<UnitRole, double>();
        foreach (var (id, count) in inventory)
        {
            foreach (var role in catalog.Unit(id).Roles)
                values[role.Role] = values.GetValueOrDefault(role.Role) + role.Value * count;
        }
        return values;
    }

    private sealed record CraftCandidate(UnitDefinition Unit, Recommendation Recommendation,
        StrategyMetrics Metrics);

    private readonly record struct GoalStrategyProfile(double BossControlTarget,
        double BerserkBossControlTarget, bool OptionalBossSupportAfterCore = false,
        bool FillCommunitySupports = false, double SlowTarget = FullSlowTarget,
        double StunTarget = StableStunTarget, double ArmorReductionTarget = FullArmorReductionTarget,
        double ArmorBreakTarget = 0, int CommunityCoreTarget = 0, bool StunBeforeSlow = true,
        double AirMovementTarget = 1, double MagicArmorReductionTarget = 0,
        double SingleDamageTarget = 0, double FinisherDamageTarget = 0,
        double StunCap = MaximumUsefulStun);

    private readonly record struct StrategyMetrics(double Slow = 0, double Stun = 0,
        double ArmorReduction = 0, double ArmorBreak = 0, double AirMovement = 0,
        double BossControl = 0, double BerserkBossControl = 0, double MagicArmorReduction = 0,
        double SingleDamage = 0, double FinisherDamage = 0)
    {
        public bool HasAny => Total > 0;
        public double Total => Slow + Stun + ArmorReduction + ArmorBreak + AirMovement +
                               BossControl + BerserkBossControl + MagicArmorReduction +
                               SingleDamage + FinisherDamage;

        public static StrategyMetrics operator +(StrategyMetrics left, StrategyMetrics right) =>
            new(left.Slow + right.Slow, left.Stun + right.Stun,
                left.ArmorReduction + right.ArmorReduction, left.ArmorBreak + right.ArmorBreak,
                left.AirMovement + right.AirMovement,
                left.BossControl + right.BossControl,
                left.BerserkBossControl + right.BerserkBossControl,
                left.MagicArmorReduction + right.MagicArmorReduction,
                left.SingleDamage + right.SingleDamage,
                left.FinisherDamage + right.FinisherDamage);

        public static StrategyMetrics operator *(StrategyMetrics value, int multiplier) =>
            new(value.Slow * multiplier, value.Stun * multiplier,
                value.ArmorReduction * multiplier, value.ArmorBreak * multiplier,
                value.AirMovement * multiplier,
                value.BossControl * multiplier,
                value.BerserkBossControl * multiplier,
                value.MagicArmorReduction * multiplier,
                value.SingleDamage * multiplier,
                value.FinisherDamage * multiplier);
    }
}

public static class RoleLabels
{
    public static string Name(UnitRole role) => role switch
    {
        UnitRole.Holding => "홀딩",
        UnitRole.Slow => "이감",
        UnitRole.ArmorReduction => "방깎",
        UnitRole.AttackBoost => "공증",
        UnitRole.AreaControl => "광잡",
        UnitRole.BossControl => "보잡",
        UnitRole.Damage => "딜",
        _ => "지원"
    };
}
