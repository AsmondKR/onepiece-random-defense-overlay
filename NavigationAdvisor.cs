namespace OrandOverlay;

public sealed record NavigationAdvice(
    string OptionId,
    string Name,
    string Reason,
    IReadOnlyList<string> FollowUps);

/// <summary>
/// 21라운드 전에 패를 보고 초보가 쓰기 쉬운 항법만 2~3개 띄운다.
/// 연속베팅=배·중급도박으로 패 풀기/불리기, 긴급소집=겹친 패 강제 풀기,
/// 특성공학=특포가 많이 필요한 상위, 역발상=아이템 상위 대깨(레일리·피셔),
/// 일석이조=전설·상위를 많이 짤 수 있을 때,
/// 바운티헌터=거프 불멸처럼 라인딜이 미친 상위 각.
/// </summary>
public sealed class NavigationAdvisor(DataCatalog catalog)
{
    public const int DecisionRound = 21;
    public const int SpecialPileCount = 4;

    private static readonly string[] TraitHungryUserRawcodes =
        ["5B0H", "Q80h", "750h"]; // 키자루 초월 · 알비다 제한 · 비비 영원
    private static readonly string[] KizaruRawcodes = ["5B0H"];
    private static readonly string[] ShipFollowUps = ["Q30h", "E50h", "U30h"]; // 모비딕 · 에넬 · 레드포스
    private static readonly string[] LegendTiers = ["전설", "히든", "변화된", "왜곡됨"];
    private static readonly string[] TopTiers = ["초월", "불멸", "영원", "제한됨", "신비함"];
    private static readonly string[] OverlapTiers = ["특별함", "안흔함", "희귀함"];
    // 바운티헌터용 라인딜 깡패. 43747·ordsearch 기준 거프 불멸만 솔딜 스플로
    // 라인을 녹인다. 시키=함대, 쵸파=버퍼, 상디/아카초=준수 라인딜+유틸이라 제외.
    private static readonly string[] LineDpsMonsterRawcodes = ["C40h"]; // 거프 불멸

    public IReadOnlyList<NavigationAdvice> Evaluate(
        IEnumerable<InventoryEntry> inventory,
        UnitDefinition? goal,
        int round,
        int take = 3)
    {
        if (round >= DecisionRound) return [];
        var owned = inventory
            .Where(entry => entry.Count > 0)
            .GroupBy(entry => entry.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count),
                StringComparer.OrdinalIgnoreCase);
        if (owned.Count == 0) return [];

        var scored = new List<(int Score, NavigationAdvice Advice)>();
        Add(scored, TraitEngineering(owned, goal));
        Add(scored, ReverseThinking(owned, goal));
        Add(scored, EmergencyCall(owned));
        Add(scored, ContinuousBetting(owned));
        Add(scored, DoubleBenefit(owned, goal));
        Add(scored, BountyHunter(owned, goal));
        return scored
            .OrderByDescending(item => item.Score)
            .Select(item => item.Advice)
            .DistinctBy(item => item.OptionId)
            .Take(take)
            .ToList();
    }

    public static string FormatHint(IReadOnlyList<NavigationAdvice> advice)
    {
        if (advice.Count == 0) return "";
        var ranks = string.Join("  ·  ",
            advice.Select((item, index) => $"{index + 1} {item.Name}({item.Reason})"));
        var follow = advice
            .SelectMany(item => item.FollowUps)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var hint = $"21라 항법  ·  {ranks}";
        if (follow.Count > 0)
            hint += "  ·  " + string.Join(" · ", follow);
        return hint;
    }

    private (int Score, NavigationAdvice Advice)? TraitEngineering(
        IReadOnlyDictionary<string, int> owned, UnitDefinition? goal)
    {
        var hits = TraitHungryUserRawcodes
            .Select(code => catalog.Unit("rawcode:" + code))
            .Where(unit => unit.Name != "이름 미등록 유닛")
            .Select(unit => (Unit: unit, Ready: Completion(unit, owned)))
            .Where(item => Owns(owned, item.Unit) || item.Ready >= 0.35 ||
                           GoalHas(goal, item.Unit))
            .OrderByDescending(item => GoalHas(goal, item.Unit) ? 1 : 0)
            .ThenByDescending(item => item.Ready)
            .ToList();
        if (hits.Count == 0 && goal is not null &&
            goal.Rawcodes.Any(code => code is "5B0H" or "Q80h" or "750h"))
            hits.Add((goal, 1));
        if (hits.Count == 0) return null;
        var name = hits[0].Unit.Name;
        var score = GoalHas(goal, hits[0].Unit) ? 90 : 70 + (int)(hits[0].Ready * 10);
        return (score, new NavigationAdvice("AlliedForces.TraitEngineering", "특성공학",
            $"{name} 특포", []));
    }

    private (int Score, NavigationAdvice Advice)? ReverseThinking(
        IReadOnlyDictionary<string, int> owned, UnitDefinition? goal)
    {
        // 키자루 각을 역발상으로 만들면 특강을 거의 못 해서 강점이 없다.
        if (goal is not null && goal.Rawcodes.Any(KizaruRawcodes.Contains)) return null;
        var itemTop = goal is not null && RecipeNeedsItem(goal)
            ? goal
            : null;
        if (itemTop is null) return null;
        var tops = CountTiers(owned, TopTiers);
        if (tops >= 2) return null;
        var fisher = catalog.Unit("rawcode:740h").Name;
        var follow = fisher.StartsWith("이름 미등록", StringComparison.Ordinal)
            ? Array.Empty<string>()
            : new[] { $"덤 {fisher}" };
        return (72, new NavigationAdvice("BestHelp.ReverseThinking", "역발상",
            $"{itemTop.Name} 아이템 대깨 · 레일리", follow));
    }

    private (int Score, NavigationAdvice Advice)? EmergencyCall(
        IReadOnlyDictionary<string, int> owned)
    {
        var overlapped = owned
            .Where(pair => pair.Value >= 2 &&
                           OverlapTiers.Contains(BaseTier(catalog.Unit(pair.Key).Tier)))
            .Sum(pair => pair.Value - 1);
        if (overlapped < 1) return null;
        return (60 + overlapped, new NavigationAdvice("AlliedForces.EmergencyCall", "긴급소집",
            $"겹친 패 {overlapped}장 강제 풀기", []));
    }

    private (int Score, NavigationAdvice Advice)? ContinuousBetting(
        IReadOnlyDictionary<string, int> owned)
    {
        var specials = SpecialCount(owned);
        if (specials < SpecialPileCount) return null;
        var ships = ShipFollowUps
            .Select(code => catalog.Unit("rawcode:" + code).Name)
            .Where(name => name.Length > 0 && !name.StartsWith("이름 미등록", StringComparison.Ordinal))
            .ToList();
        return (50 + specials, new NavigationAdvice("Gambler.ContinuousBetting", "연속베팅",
            "중급도박으로 패 풀기·불리기", ships.Select(name => "배 뜨면 " + name).ToList()));
    }

    private (int Score, NavigationAdvice Advice)? DoubleBenefit(
        IReadOnlyDictionary<string, int> owned, UnitDefinition? goal)
    {
        var legends = CountTiers(owned, LegendTiers);
        var ready = catalog.AllUnits
            .Where(unit => LegendTiers.Contains(BaseTier(unit.Tier)))
            .Where(unit => !Owns(owned, unit))
            .Count(unit => Completion(unit, owned) >= 0.5);
        var tops = CountTiers(owned, TopTiers);
        var goalIsTop = goal is not null && TopTiers.Contains(BaseTier(goal.Tier));
        var pool = legends + ready;
        if (pool < 3 && !(pool >= 2 && (tops >= 1 || goalIsTop))) return null;
        return (55 + pool, new NavigationAdvice("AlliedForces.DoubleBenefit", "일석이조",
            $"전설급 {pool}기 · 상위 많이", []));
    }

    private (int Score, NavigationAdvice Advice)? BountyHunter(
        IReadOnlyDictionary<string, int> owned, UnitDefinition? goal)
    {
        // 최상위 1기 항법. 거프 불멸처럼 라인딜이 미친 유닛을 솔딜로 갈 때.
        if (CountTiers(owned, TopTiers) >= 2) return null;
        var hits = LineDpsMonsterRawcodes
            .Select(code => catalog.Unit("rawcode:" + code))
            .Where(unit => unit.Name != "이름 미등록 유닛")
            .Select(unit => (Unit: unit, Ready: Completion(unit, owned)))
            .Where(item => Owns(owned, item.Unit) || item.Ready >= 0.35 ||
                           GoalHas(goal, item.Unit))
            .OrderByDescending(item => GoalHas(goal, item.Unit) ? 1 : 0)
            .ThenByDescending(item => item.Ready)
            .ToList();
        if (hits.Count == 0) return null;
        var name = hits[0].Unit.Name;
        var score = GoalHas(goal, hits[0].Unit) ? 82 : 70 + (int)(hits[0].Ready * 10);
        return (score, new NavigationAdvice("PathOfKings.BountyHunter", "바운티헌터",
            $"{name} 라인딜", []));
    }

    private bool RecipeNeedsItem(UnitDefinition unit)
    {
        var found = false;
        Walk(unit, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return found;

        void Walk(UnitDefinition current, HashSet<string> visiting)
        {
            if (found || !visiting.Add(current.Id)) return;
            if (BaseTier(current.Tier) == "아이템")
            {
                found = true;
                return;
            }
            foreach (var childId in current.Recipe.Keys)
            {
                var child = catalog.Unit(childId);
                if (child.Rawcodes.Any(code => code is "GOLD" or "LUMBER" or "POINT" or "RANDOM"))
                    continue;
                Walk(child, visiting);
            }
        }
    }

    private int CountTiers(IReadOnlyDictionary<string, int> owned, string[] tiers) => owned
        .Count(pair => pair.Value > 0 && tiers.Contains(BaseTier(catalog.Unit(pair.Key).Tier)));

    private double Completion(UnitDefinition unit, IReadOnlyDictionary<string, int> owned)
    {
        if (unit.Recipe.Count == 0) return Owns(owned, unit) ? 1 : 0;
        return new RecipeCompletionCalculator(catalog.Unit)
            .Calculate([unit.Id], owned).CompletionRatio;
    }

    private static bool Owns(IReadOnlyDictionary<string, int> owned, UnitDefinition unit) =>
        owned.GetValueOrDefault(unit.Id) > 0 ||
        unit.Rawcodes.Any(code => owned.GetValueOrDefault("rawcode:" + code) > 0);

    private static bool GoalHas(UnitDefinition? goal, UnitDefinition unit) =>
        goal is not null &&
        (goal.Id.Equals(unit.Id, StringComparison.OrdinalIgnoreCase) ||
         goal.Rawcodes.Any(code => unit.Rawcodes.Contains(code, StringComparer.Ordinal)));

    private static void Add(List<(int Score, NavigationAdvice Advice)> sink,
        (int Score, NavigationAdvice Advice)? item)
    {
        if (item is { } value) sink.Add(value);
    }

    private int SpecialCount(IReadOnlyDictionary<string, int> owned) => owned
        .Where(pair => BaseTier(catalog.Unit(pair.Key).Tier) == "특별함")
        .Sum(pair => pair.Value);

    private static string BaseTier(string tier) => tier.Split('[', 2)[0].Trim();
}
