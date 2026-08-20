using System.Security.Cryptography;
using System.Text.Json;
using OrandOverlay;

var catalog = new DataCatalog();
catalog.Load();

if (args.Any(x => x.Equals("--watch", StringComparison.OrdinalIgnoreCase)))
{
    var recognizer = new WarcraftMemoryRecognitionService(catalog);
    string? lastSignature = null;
    Console.WriteLine("WATCH started · state/Luffy/Yamato changes only · Ctrl+C to stop");
    while (true)
    {
        try
        {
            var current = await recognizer.RecognizeAsync(new AppSettings(), CancellationToken.None);
            var luffy = current.Entries.FirstOrDefault(x => x.UnitId == "luffy_common")?.Count ?? 0;
            var yamato = current.Entries.FirstOrDefault(x =>
                x.UnitId == "yamato_transcendent")?.Count ?? 0;
            var signature = $"{current.State}|{luffy}|{yamato}|{current.Diagnostics.ProcessId}|" +
                            $"{current.Diagnostics.ResolvedListAddress}";
            if (!signature.Equals(lastSignature, StringComparison.Ordinal))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] state={current.State} · 루피={luffy} · 야마토={yamato} · " +
                                  $"패={current.Entries.Sum(x => x.Count)} · {current.Status}");
                Console.WriteLine(current.Diagnostics.DisplayText);
                lastSignature = signature;
            }
        }
        catch (Exception exception)
        {
            var signature = "exception|" + exception.GetType().Name + "|" + exception.Message;
            if (!signature.Equals(lastSignature, StringComparison.Ordinal))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {signature}");
                lastSignature = signature;
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(1.2));
    }
}

if (args.Any(x => x.Equals("--live", StringComparison.OrdinalIgnoreCase)))
{
    var live = await new WarcraftMemoryRecognitionService(catalog)
        .RecognizeAsync(new AppSettings(), CancellationToken.None);
    Console.WriteLine($"LIVE state={live.State} entries={live.Entries.Sum(x => x.Count)} status={live.Status}");
    Console.WriteLine(live.Diagnostics.DisplayText);
    foreach (var entry in live.Entries.OrderBy(x => catalog.Unit(x.UnitId).Name))
        Console.WriteLine($"  {catalog.Unit(entry.UnitId).Name} ({entry.UnitId}) x{entry.Count}");
    var liveStats = new InventoryStatsCalculator(catalog).Calculate(live.Entries);
    Console.WriteLine($"  현재수치: 스턴 {liveStats.Stun:0.##}/1.4 · " +
                      $"이감 {liveStats.TotalSlow:0.##}/102 · " +
                      $"방깎 {liveStats.TotalArmorReduction:0.##}/211 " +
                      $"(고정 {liveStats.ArmorReduction:0.##}, 발동 {liveStats.TriggeredArmorReduction:0.##}, " +
                      $"중첩 {liveStats.StackingArmorReduction:0.##}) · 암브 {liveStats.ArmorBreakProviders}기");
    var liveCrafts = new RecommendationEngine(catalog)
        .RecommendNearestCrafts(new AppSettings().GoalUnitId, live.Entries);
    var liveRerolls = new RareRerollAdvisor(catalog).Evaluate(live.Entries, liveCrafts);
    for (var i = 0; i < liveCrafts.Count; i++)
    {
        Console.WriteLine($"  추천 {i + 1}: " +
                          $"{RecommendationPresentation.CraftUnitName(liveCrafts[i].CompositionUnits[0])} " +
                          RecommendationPresentation.CompletionPercent(liveCrafts[i].RecipeProgress));
        foreach (var step in liveCrafts[i].RemainingCraftSteps)
        {
            Console.WriteLine($"    조합: {RecommendationPresentation.CraftUnitName(step.Name, step.Tier)} " +
                              $"부족 {step.MissingCount}");
            Console.WriteLine("      " + RecommendationPresentation.CraftIngredientLine(step));
        }
    }
    Console.WriteLine(liveRerolls.Count == 0
        ? "  희귀패 리롤 후보: 없음"
        : "  희귀패 리롤 후보: " + string.Join(", ", liveRerolls.Select(item =>
            $"{item.Name} ×{item.RerollCount}")));
    return;
}

if (args.Any(x => x.Equals("--order-check", StringComparison.OrdinalIgnoreCase)))
{
    var orderStats = ClearBuildStats.Load(
        [Path.Combine(AppContext.BaseDirectory, "Data", "tmo-clear-samples.json")]);
    var orderEngine = new RecommendationEngine(catalog, orderStats);
    foreach (var (goalId, navigationId, ownedIds) in new[]
             {
                 ("yamato_transcendent", "PathOfKings.BountyHunter", Array.Empty<string>()),
                 ("rawcode:F90H", "PathOfKings.BountyHunter", Array.Empty<string>()),
                 ("rawcode:A90H", "PathOfKings.BountyHunter", Array.Empty<string>()),
                 ("rawcode:H90H", "AlliedForces.EmergencyCall", Array.Empty<string>()),
                 ("rawcode:H90H", "AlliedForces.EmergencyCall", new[] { "rawcode:E50h" }),
                 ("rawcode:H90H", "AlliedForces.EmergencyCall", new[] { "rawcode:J30h" }),
                 ("rawcode:2B0H", "AlliedForces.EmergencyCall", Array.Empty<string>()),
                 ("rawcode:Q40h", "AlliedForces.EmergencyCall", Array.Empty<string>())
             })
    {
        var picks = orderEngine.RecommendNearestCrafts(goalId, Inventory(ownedIds), 8, navigationId);
        var anchorLabel = picks.Select(item => item.ClearEvidence?.AnchorLabel)
            .FirstOrDefault(label => label is not null);
        Console.WriteLine(catalog.Unit(goalId).Name +
                          (anchorLabel is null ? "" : $" ({anchorLabel} 동반)") + ": " +
                          string.Join(" > ", picks.Skip(1).Select(item =>
                              $"{item.CompositionUnits[0].Name}({item.ClearEvidence?.SharePercent ?? 0}%)")));
        if (navigationId != "AlliedForces.EmergencyCall" || ownedIds.Length > 0) continue;
        Console.WriteLine("  와일드카드: " + string.Join(" · ", orderEngine
            .RecommendEmergencySummons(picks, Inventory(ownedIds))
            .Select(item => $"{item.Name}×{item.Count}({item.Reason})")));
    }
    return;
}

var engine = new RecommendationEngine(catalog);

var empty = engine.Recommend("yamato_transcendent", []);
Assert(empty.Count == 3, "야마토 후보 3개 생성");
Assert(empty[0].Route.Id == "yamato_dragon", "조건 없는 기본 루트는 드래곤 전설");

var demo = Inventory("dragon_legend", "slow_support", "armor_support", "area_support");
var demoResult = engine.Recommend("yamato_transcendent", demo);
Assert(demoResult[0].Route.Id == "yamato_dragon", "드전 보유 시 드전 루트 1위");
Assert(demoResult[0].Score >= 85, "완성도 높은 루트 점수");

var conditional = Inventory("mobydick", "item_greenblood", "ivankov_hidden", "slow_support", "armor_support");
var conditionalResult = engine.Recommend("yamato_transcendent", conditional);
Assert(conditionalResult[0].Route.Id == "yamato_greenblood_ivankov", "조건 충족 시 그블 이완히 1위");

var overHolding = Inventory("dragon_legend", "bartolomeo_legend");
var overHoldingResult = engine.Recommend("yamato_transcendent", overHolding);
Assert(overHoldingResult.SelectMany(x => x.Warnings).Any(x => x.Contains("과투자")), "중복 홀딩 경고");

var inventoryStats = new InventoryStatsCalculator(catalog).Calculate(Inventory(
    "dragon_legend", "rawcode:HA0h", "mobydick", "rawcode:IC0h", "rawcode:U30h"));
Assert(Math.Abs(inventoryStats.Stun - 1.3) < 0.001 &&
       Math.Abs(inventoryStats.TotalSlow - 60) < 0.001 &&
       Math.Abs(inventoryStats.ArmorReduction - 35) < 0.001 &&
       Math.Abs(inventoryStats.TriggeredArmorReduction - 50) < 0.001 &&
       Math.Abs(inventoryStats.TotalArmorReduction - 85) < 0.001 &&
       Math.Abs(inventoryStats.TotalAttackBoost - 60) < 0.001 &&
       Math.Abs(inventoryStats.AttackSpeed - 5) < 0.001,
    "현재 패의 스턴·이감·고정/발동 방깎·공증·공속을 실제 능력 데이터로 합산");
Assert(inventoryStats.ArmorBreakProviders == 1 && inventoryStats.BossControlProviders == 1 &&
       inventoryStats.BerserkControlProviders == 1 &&
       inventoryStats.AirMovementProviders >= 1 &&
       Math.Abs(inventoryStats.HealthRegen - 2.25) < 0.001 &&
       Math.Abs(inventoryStats.ManaRegen - 1) < 0.001,
    "암브는 방깎에 오합산하지 않고 공중이동 등 특수 역할·재생 수치를 집계");
var kalgaraStats = new InventoryStatsCalculator(catalog).Calculate(Inventory("kalgara"));
Assert(Math.Abs(kalgaraStats.ArmorReduction - 20) < 0.001 &&
       Math.Abs(kalgaraStats.SingleArmorReduction - 20) < 0.001 &&
       Math.Abs(kalgaraStats.TotalArmorReduction - 20) < 0.001,
    "단일 방깎은 표시하되 라인 풀방깎 211 합계에서는 제외");

var dragonLeafRequirements = new RecipeCompletionCalculator(catalog.Unit)
    .Calculate(["dragon_legend"], new Dictionary<string, int>())
    .Leaves;
var dragonLeafInventory = dragonLeafRequirements.Select(leaf => new InventoryEntry
{
    UnitId = leaf.UnitId,
    Count = checked((int)leaf.RequiredCount)
}).ToList();
var nearestCrafts = engine.RecommendNearestCrafts("yamato_transcendent", dragonLeafInventory, 200);
Assert(nearestCrafts[0].Route.GoalUnitId == "yamato_transcendent",
    "선택한 야마토 초월을 제작 거리와 무관하게 첫 번째로 고정");
Assert(nearestCrafts.Skip(1).All(item =>
    !new[] { "신비함", "초월", "불멸", "영원", "제한" }.Any(blocked =>
        item.CompositionUnits[0].Tier.StartsWith(blocked, StringComparison.OrdinalIgnoreCase))),
    "패왕의길에서는 선택 목표 외 최상위 유닛을 추천 후보에서 제외");
var yamatoWithoutPirateShip = engine.RecommendNearestCrafts("yamato_transcendent", [], 8);
Assert(yamatoWithoutPirateShip.All(item =>
        !item.Route.GoalUnitId.Equals("mobydick", StringComparison.OrdinalIgnoreCase)),
    "해적선이 없는 패에서는 모비딕호를 추천 후보에서 제외");
// 표시 순서는 채용률이 정하지만, 선택된 지원에는 스턴 1.4 패키지가 유지되어야 한다.
var yamatoStunSupports = yamatoWithoutPirateShip.Skip(1)
    .Where(item => AbilityValue(item.CompositionUnits[0], "스턴") > 0)
    .ToList();
Assert(yamatoStunSupports.Count > 0 &&
       yamatoStunSupports.Sum(item => AbilityValue(item.CompositionUnits[0], "스턴")) is >= 1.3 and <= 1.5,
    "야마토 초월 추천은 채용률 순서와 무관하게 스턴 1.4 패키지를 유지");
var yamatoSlowPriority = engine.RecommendNearestCrafts("yamato_transcendent",
    Inventory("rawcode:O30h", "rawcode:Y30h", "rawcode:IC0h"), 3);
Assert(yamatoSlowPriority.Count > 1 && yamatoSlowPriority[1].Route.GoalUnitId == "rawcode:V50h",
    "야마토는 스턴 완성 뒤 순수 50이감 스모커보다 최근 커뮤니티의 에이스 왜곡을 우선");
var yamatoSignedSlow = engine.RecommendNearestCrafts("yamato_transcendent",
    Inventory("rawcode:O30h", "rawcode:Y30h", "rawcode:IC0h", "rawcode:V20h",
        "mobydick", "rawcode:W50h"), 2);
Assert(yamatoSignedSlow.Count > 1 && yamatoSignedSlow[1].CompositionUnits[0].Abilities.Any(ability =>
        ability.Name is "이동속도 감소" or "발동이동속도 감소"),
    "야마토의 적 이동속도 증가를 음수 이감으로 반영해 실제 풀이감 102까지 추가 보강");
var communityRankedYamato = engine.RecommendNearestCrafts("yamato_transcendent",
    Inventory("rawcode:060h"), 8);
Assert(communityRankedYamato[0].Route.GoalUnitId == "yamato_transcendent" &&
       communityRankedYamato.Count > 1 &&
       communityRankedYamato[1].Route.GoalUnitId == "mobydick" &&
       communityRankedYamato.Skip(1).Sum(item =>
           AbilityValue(item.CompositionUnits[0], "스턴")) is >= 1.3 and <= 1.5,
    "해적선 보유 시 채용률 1위 모비딕을 목표 다음으로 올리고 스턴 1.4 패키지는 유지");
var craftedYamatoRecommendations = engine.RecommendNearestCrafts("yamato_transcendent",
    Inventory("yamato_transcendent"), 8);
Assert(craftedYamatoRecommendations.Count > 0 && craftedYamatoRecommendations.All(item =>
        !item.Route.GoalUnitId.Equals("yamato_transcendent", StringComparison.OrdinalIgnoreCase)),
    "이미 조합해 보유한 목표 유닛은 오버레이 제작 후보에서 즉시 제거");
var completedTopTracker = new CompletedTopUnitTracker(catalog);
completedTopTracker.Observe(Inventory("yamato_transcendent"));
var temporaryYamatoDropout = completedTopTracker.Apply([]);
Assert(temporaryYamatoDropout.Any(entry => entry.UnitId == "yamato_transcendent") &&
       engine.RecommendNearestCrafts("yamato_transcendent", temporaryYamatoDropout, 8)
           .All(item => item.Route.GoalUnitId != "yamato_transcendent"),
    "한 번 확인한 완성 상위 유닛은 같은 게임의 일시 메모리 누락에도 다시 추천하지 않음");
completedTopTracker.Reset();
Assert(completedTopTracker.Apply([]).Count == 0,
    "게임 세션 경계에서는 완성 상위 유닛 잠금을 초기화");
var nearestDragon = engine.RecommendNearestCrafts("dragon_legend", dragonLeafInventory, 1)[0];
Assert(nearestDragon.Score == 100 && nearestDragon.RecipeTree is { Children.Count: > 0 },
    "보유 최하위 패로 완성 가능한 드래곤을 100퍼센트로 계산하고 재귀 조합 트리 제공");
var rareStoryInventory = Inventory("rawcode:A20h", "rawcode:A20h", "rawcode:X90h");
var dragonPlanForReroll = engine.RecommendNearestCrafts("dragon_legend", rareStoryInventory, 1);
var rareRerolls = new RareRerollAdvisor(catalog).Evaluate(rareStoryInventory, dragonPlanForReroll);
Assert(rareRerolls.Any(item => item.UnitId == "rawcode:A20h" && item.NeededCount == 1 &&
                               item.RerollCount == 1) &&
       rareRerolls.All(item => item.UnitId != "rawcode:X90h"),
    "필요 희귀패의 초과분만 리롤 후보, 지원 스탯(이감·방깎) 있는 미사용 희귀패는 보존");
var exactRareInventory = Inventory("rawcode:A20h");
var exactRarePlan = engine.RecommendNearestCrafts("dragon_legend", exactRareInventory, 1);
Assert(new RareRerollAdvisor(catalog).Evaluate(exactRareInventory, exactRarePlan)
        .All(item => item.UnitId != "rawcode:A20h"),
    "추천 제작에 정확히 필요한 희귀패는 리롤 후보에서 제외");
var afterFullSlow = engine.RecommendNearestCrafts("yamato_transcendent",
    Inventory("mobydick", "mobydick", "mobydick"), 2)[1].CompositionUnits[0];
Assert(afterFullSlow.Abilities.Any(ability => ability.Name == "스턴"),
    "43747 풀이감 102 충족 뒤에는 스턴 1.4 보강 후보를 선택");
var afterStableStun = engine.RecommendNearestCrafts("yamato_transcendent",
    Inventory("rawcode:V20h", "rawcode:V20h", "rawcode:V20h", "dragon_legend", "rawcode:O30h"), 2)[1]
    .CompositionUnits[0];
Assert(afterStableStun.Abilities.Any(ability =>
        ability.Name is "방어력 감소" or "발동방어력 감소" or "중첩방어력 감소"),
    "풀이감과 스턴 충족 뒤에는 방깎 후보를 선택");
Assert(afterStableStun.Abilities.Any(ability => ability.Name == "공중이동"),
    "방깎 단계에서는 크립·정의의 문·해왕류·황금종용 공중이동 한 기를 먼저 겸함");
var afterFullArmor = engine.RecommendNearestCrafts("yamato_transcendent",
    Inventory("mobydick", "mobydick", "mobydick", "dragon_legend", "rawcode:O30h",
        "mihawk_hidden", "mihawk_hidden", "mihawk_hidden"), 2)[1].CompositionUnits[0];
Assert(afterFullArmor.Abilities.Any(ability =>
        ability.Name is "방어력 감소" or "발동방어력 감소" or "중첩방어력 감소"),
    "풀이감·스턴 충족 뒤에는 풀방깎 211까지 방깎 후보를 우선");
Assert(catalog.Unit("mobydick").Name == "모비딕호" && catalog.Unit("mobydick").Tier == "해적선",
    "모비딕호 등급을 해적선으로 표시");
var cappedStunRecommendations = engine.RecommendNearestCrafts("yamato_transcendent",
    Inventory("mobydick", "mobydick", "mobydick"), 8);
var cappedStunTotal = cappedStunRecommendations.Skip(1)
    .Sum(item => AbilityValue(item.CompositionUnits[0], "스턴"));
Assert(cappedStunTotal >= 1.3 && cappedStunTotal <= 1.5 &&
       !(cappedStunRecommendations.Any(item => item.Route.GoalUnitId == "bartolomeo_legend") &&
         cappedStunRecommendations.Any(item => item.Route.GoalUnitId == "rawcode:Q20h")),
    "스턴 조합을 1.4 근처로 맞추고 바톨 전설과 라분 전설 과투자를 방지");
var fullYamatoCoreInventory = Inventory("mobydick", "mobydick", "mobydick", "dragon_legend", "rawcode:O30h",
    "mihawk_hidden", "mihawk_hidden", "mihawk_hidden", "mihawk_hidden",
    "mihawk_hidden", "mihawk_hidden", "mihawk_hidden", "mihawk_hidden");
var cappedArmorRecommendations = engine.RecommendNearestCrafts("yamato_transcendent",
    fullYamatoCoreInventory, 8);
Assert(cappedArmorRecommendations.Count == 1,
    "풀이감 102·스턴 1.4·방깎 211을 넘기면 방깎과 보잡을 더 강제하지 않음");
var belowFullArmorRecommendations = engine.RecommendNearestCrafts("yamato_transcendent",
    Inventory("mobydick", "mobydick", "mobydick", "dragon_legend", "rawcode:O30h",
        "mihawk_hidden", "mihawk_hidden", "mihawk_hidden"), 3);
Assert(belowFullArmorRecommendations.Skip(1).Any(item => item.CompositionUnits[0].Abilities.Any(ability =>
        ability.Name is "방어력 감소" or "발동방어력 감소" or "중첩방어력 감소")),
    "방깎 211 전에는 현재 패에서 가까운 방깎 후보를 계속 추천");
var usoppProfile = engine.RecommendNearestCrafts("rawcode:B90H", fullYamatoCoreInventory, 8);
Assert(usoppProfile.Count == 1 &&
       usoppProfile[0].CompositionUnits[0].Abilities.Any(ability => ability.Name == "보스 잡기") &&
       usoppProfile[0].CompositionUnits[0].Abilities.Any(ability => ability.Name == "광폭화 잡기"),
    "우솝 초월은 자체 보잡·광보잡을 인정해 별도 보조를 강제하지 않음");
var usoppDaekkae = engine.RecommendNearestCrafts("rawcode:B90H", [], 8);
var usoppDaekkaeIds = usoppDaekkae.Select(item => item.Route.GoalUnitId).ToList();
Console.WriteLine("우솝 대깨 순서: " + string.Join(" > ", usoppDaekkae.Select(item =>
    RecommendationPresentation.CraftUnitName(item.CompositionUnits[0]))));
Assert(usoppDaekkaeIds.FirstOrDefault() == "rawcode:B90H" &&
       usoppDaekkaeIds.Contains("rawcode:M30h") &&
       usoppDaekkaeIds.Contains("rawcode:O30h"),
    "우솝 대깨도 스턴 1.4를 먼저 맞춘 뒤 최근 빈도의 사보·봉쿠레 축을 반영");
var usoppRecommendedStun = usoppDaekkae.Skip(1)
    .Sum(item => AbilityValue(item.CompositionUnits[0], "스턴"));
Assert(usoppRecommendedStun >= 1.3 && usoppRecommendedStun <= 1.5,
    "우솝 대깨 스턴 보강도 1.4 근처에서 멈춤");
var zoroProfile = engine.RecommendNearestCrafts("rawcode:F90H", fullYamatoCoreInventory, 8);
Assert(zoroProfile.Count == 2 && zoroProfile.Skip(1).Any(item =>
        item.CompositionUnits[0].Abilities.Any(ability =>
            ability.Name is "보스 잡기" or "광폭화 잡기")),
    "조로 초월은 핵심 수치 완성 뒤 고점용 보잡 후보 한 기만 선택적으로 제시");
var zoroOneTop = engine.RecommendNearestCrafts("rawcode:F90H", [], 8,
    "PathOfKings.BountyHunter");
Assert(zoroOneTop.Count > 1 && zoroOneTop[1].Route.GoalUnitId == "rawcode:O30h" &&
       zoroOneTop.All(item => item.Route.GoalUnitId != "rawcode:F50h"),
    "조로 초월 1상위 항법은 크로커다일 제한을 막고 봉쿠레 히든을 첫 파트너로 추천");
var zoroMultiTop = engine.RecommendNearestCrafts("rawcode:F90H", [], 8,
    "AlliedForces.DoubleBenefit");
Assert(zoroMultiTop.Count > 1 &&
       zoroMultiTop.Count(item => item.Route.GoalUnitId is "rawcode:F50h" or "rawcode:O30h") == 1,
    "조로 초월 다상위도 스턴을 먼저 맞추고 크제·봉히 동시 과투자를 방지");
var jinbeOneTop = engine.RecommendNearestCrafts("rawcode:A90H", [], 8,
    "PathOfKings.BountyHunter");
var jinbeStunPackage = jinbeOneTop.Skip(1)
    .Where(item => AbilityValue(item.CompositionUnits[0], "스턴") > 0).ToList();
Assert(jinbeStunPackage.Sum(item => AbilityValue(item.CompositionUnits[0], "스턴")) is >= 1.3 and <= 1.5 &&
       jinbeOneTop.Any(item => item.Route.GoalUnitId == "rawcode:V20h") &&
       jinbeOneTop.Count(item => item.CompositionUnits[0].Abilities.Any(ability =>
           ability.Name is "아머브레이크" or "단일아머브레이크")) >= 2,
    "징베 초월 1상위도 스턴 1.4 패키지와 자체 암브 외 스모커 전설 한 기를 확보");
var jinbeMultiTop = engine.RecommendNearestCrafts("rawcode:A90H", [], 200,
    "AlliedForces.DoubleBenefit");
Assert(jinbeMultiTop.Count > 1 &&
       jinbeMultiTop.All(item => item.Route.GoalUnitId != "rawcode:Q80h"),
    "징베 다상위는 첫 상위 특강을 전제로 특성공학이 아니면 알비다를 추천하지 않음");
var jinbeTraitEng = engine.RecommendNearestCrafts("rawcode:A90H", [], 200,
    "AlliedForces.TraitEngineering");
Assert(jinbeTraitEng.Any(item => item.Route.GoalUnitId == "rawcode:Q80h"),
    "징베 특성공학은 특포가 넉넉해서 알비다를 추천한다");
var jinbeWithSparePoints = engine.RecommendNearestCrafts("rawcode:A90H",
    [new InventoryEntry { UnitId = "rawcode:POINT", Count = 8 }], 200,
    "AlliedForces.DoubleBenefit");
Assert(jinbeWithSparePoints.Any(item => item.Route.GoalUnitId == "rawcode:Q80h"),
    "특포가 첫 상위 특강(4) 이후에도 알비다(4)만큼 남으면 추천한다");
var jinbeWithExactEnhance = engine.RecommendNearestCrafts("rawcode:A90H",
    [new InventoryEntry { UnitId = "rawcode:POINT", Count = 4 }], 200,
    "AlliedForces.DoubleBenefit");
Assert(jinbeWithExactEnhance.All(item => item.Route.GoalUnitId != "rawcode:Q80h"),
    "특포 4개는 첫 상위 특강에 쓰이므로 알비다를 추천하지 않음");
var doflamingoBounty = engine.RecommendNearestCrafts("rawcode:E90H", [], 200,
    "PathOfKings.BountyHunter");
Assert(doflamingoBounty.All(item => item.Route.GoalUnitId != "rawcode:Q80h"),
    "도초 바헌은 특포 4개가 필요한 알비다를 추천하지 않음");
var doflamingoMulti = engine.RecommendNearestCrafts("rawcode:E90H", [], 200,
    "AlliedForces.DoubleBenefit");
Assert(doflamingoMulti.Any(item => item.Route.GoalUnitId == "rawcode:Q80h") is false,
    "도초 다상위도 특성공학이 아니면 알비다 제한을 추천하지 않음");
var nikaGoal = engine.RecommendNearestCrafts("rawcode:KB0H", [], 1)[0];
Assert(nikaGoal.Warnings.Any(warning => warning.Contains("태양신", StringComparison.Ordinal)),
    "니카 목표는 태양신의 흔적이 없으면 먼저 경고");
var nikaSupports = engine.RecommendNearestCrafts("yamato_transcendent", [], 200,
    "AlliedForces.DoubleBenefit");
Assert(nikaSupports.All(item => item.Route.GoalUnitId != "rawcode:KB0H"),
    "태양신의 흔적 없이 니카를 지원 후보에서 제외");
var enelGoal = engine.RecommendNearestCrafts("rawcode:E50h", [], 1)[0];
Assert(enelGoal.Warnings.Any(warning => warning.Contains("해적선", StringComparison.Ordinal)),
    "갓에넬 목표는 해적선이 없으면 먼저 경고");
var basilProfile = engine.RecommendNearestCrafts("rawcode:490H", fullYamatoCoreInventory, 8);
Assert(basilProfile.Skip(1).Count(item => item.CompositionUnits[0].Abilities.Any(ability =>
           ability.Name == "보스 잡기")) >= 2,
    "바질 초월은 최근 2.314 사례에 따라 보잡 보조 두 기를 목표로 함");
var alliedForcesRecommendations = engine.RecommendNearestCrafts("yamato_transcendent", [], 200,
    "AlliedForces");
Assert(alliedForcesRecommendations.Skip(1).Any(item => new[]
    {
        "신비함", "초월", "불멸", "영원", "제한됨"
    }.Any(tier => item.CompositionUnits[0].Tier.StartsWith(tier, StringComparison.OrdinalIgnoreCase))),
    "연합세력 등 다상위 항법에서는 다른 최상위 유닛도 전략 후보로 허용");
Assert(NavigationProfiles.Categories.Count == 5 &&
       NavigationProfiles.Categories.All(category => NavigationProfiles.ForCategory(category.Id).Count == 3) &&
       NavigationProfiles.Options.Select(option => option.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 15,
    "항법 5개 카테고리를 세부 항법 3개씩 총 15개로 분리");
Assert(NavigationProfiles.ForCategory("AlliedForces").Select(option => option.Name)
           .SequenceEqual(["일석이조", "긴급소집", "특성공학"]) &&
       NavigationProfiles.ForCategory("Gambler").Select(option => option.Name)
           .SequenceEqual(["카지노", "리스크헷지", "연속베팅"]),
    "2.314 맵 기준 세부 항법 이름과 순서 보존");
Assert(NavigationProfiles.Find("PathOfKings").Id == "PathOfKings.BountyHunter" &&
       NavigationProfiles.Find("PathOfKings.BountyHunter").TopUnitLimit == 1 &&
       NavigationProfiles.Find("PathOfKings.MartialLaw").TopUnitLimit == 0,
    "기존 저장 항법 이전 및 패왕의길 최상위 제한 반영");
var martialLawRecommendations = engine.RecommendNearestCrafts("yamato_transcendent", [], 8,
    "PathOfKings.MartialLaw");
Assert(martialLawRecommendations.All(item =>
        !item.Route.GoalUnitId.Equals("yamato_transcendent", StringComparison.OrdinalIgnoreCase)),
    "계엄령에서는 조합 불가능한 목표 최상위를 추천하지 않음");
Assert(nearestCrafts.All(item => !string.IsNullOrWhiteSpace(item.CompositionUnits[0].Image)) &&
       nearestCrafts.SelectMany(item => item.RecipeProgress.Leaves)
           .All(leaf => !string.IsNullOrWhiteSpace(leaf.Image)),
    "남은 조합과 최하위 재료에 티모지지 공식 유닛 이미지 연결");
Assert(RecommendationPresentation.CraftUnitName(nearestCrafts[0].CompositionUnits[0]) == "야마토 - 초월" &&
       RecommendationPresentation.CompletionPercent(nearestDragon.RecipeProgress) == "100%",
    "유닛명-등급과 제작 완성도를 퍼센트로 표시");
var doflamingoChanged = engine.RecommendNearestCrafts("rawcode:S50h", [], 1)[0];
Assert(doflamingoChanged.RemainingCraftSteps.Any(step =>
           step.Tier.StartsWith("안흔함", StringComparison.OrdinalIgnoreCase)) &&
       doflamingoChanged.RemainingCraftSteps.First().Tier.StartsWith("안흔함", StringComparison.OrdinalIgnoreCase),
    "남은 조합을 안흔함 단계부터 표시");
var firstRareStep = doflamingoChanged.RemainingCraftSteps.FindIndex(step =>
    step.Tier.StartsWith("희귀함", StringComparison.OrdinalIgnoreCase));
Assert(firstRareStep > 0 &&
       doflamingoChanged.RemainingCraftSteps.Take(firstRareStep).All(step =>
           step.Tier.StartsWith("안흔함", StringComparison.OrdinalIgnoreCase) ||
           step.Tier.StartsWith("특별함", StringComparison.OrdinalIgnoreCase)) &&
       doflamingoChanged.RemainingCraftSteps.Skip(firstRareStep)
           .All(step => !step.Tier.StartsWith("안흔함", StringComparison.OrdinalIgnoreCase) &&
                        !step.Tier.StartsWith("특별함", StringComparison.OrdinalIgnoreCase)),
    "남은 조합을 안흔함·특별함·희귀함·상위 등급 순서로 표시");
var doflamingoWithRareOwned = engine.RecommendNearestCrafts("rawcode:S50h",
    [new InventoryEntry { UnitId = "rawcode:L10h", Count = 1 }], 1)[0];
Assert(doflamingoWithRareOwned.RemainingCraftSteps.All(step =>
        step.UnitId != "rawcode:L10h" && !step.Tier.StartsWith("특별함", StringComparison.OrdinalIgnoreCase)),
    "보유한 희귀 재료는 해당 단계와 하위 특별함 조합을 함께 건너뜀");
var doflamingoRareStep = doflamingoChanged.RemainingCraftSteps.Single(step =>
    step.UnitId == "rawcode:L10h");
var doflamingoClickGuide = RecommendationPresentation.CraftIngredientLine(doflamingoRareStep);
Assert(doflamingoClickGuide.StartsWith("선택할 유닛:") && doflamingoClickGuide.Contains("로브 루치") &&
       doflamingoClickGuide.Contains("함께 조합:") && doflamingoClickGuide.Contains("쵸파 가드 포인트") &&
       !doflamingoClickGuide.Contains('×') && !KoreanLabels.ContainsLatin(doflamingoClickGuide),
    "각 조합 단계에 실제로 선택할 직전 재료 유닛을 한글로 표시");
var yamatoEmpty = engine.RecommendNearestCrafts("yamato_transcendent", [], 1)[0];
var enelSpecialStep = yamatoEmpty.RemainingCraftSteps.Single(step => step.UnitId == "rawcode:Q00h");
var enelClickGuide = RecommendationPresentation.CraftIngredientLine(enelSpecialStep);
Assert(enelClickGuide.StartsWith("선택할 유닛: 저격왕 우솝", StringComparison.Ordinal) &&
       enelClickGuide.Contains("함께 조합: 베포 / 상디", StringComparison.Ordinal) &&
       !enelClickGuide.Contains('×'),
    "공식 조합식 순서를 보존하고 조합 안내의 중복 수량 표시는 제거");
Assert(doflamingoChanged.RemainingCraftSteps.All(step =>
        !RecommendationPresentation.CraftIngredientLine(step).Contains('×')),
    "선택·함께 조합에는 수량을 숨기고 남은 제작에만 수량을 표시");

Assert(RawcodeCodec.TryParse("300h", out var luffyRawcode) && luffyRawcode == 0x68303033,
    "rawcode 4CC 변환");
Assert(RawcodeCodec.Format(0x68303033) == "300h", "rawcode 메모리 바이트 순서 왕복");
Assert(!RawcodeCodec.TryParse("too-long", out _), "잘못된 rawcode 차단");
Assert(catalog.RawcodeCatalog.Count >= 250, "TMO rawcode 이름 카탈로그 로드");
Assert(catalog.Unit("rawcode:200h").Name.Contains("조로"), "동적 rawcode 유닛 이름 표시");
Assert(catalog.Unit("item_greenblood").Name == "그린블러드" &&
       KoreanLabels.RemoveLatin("Green Blood") == "그린블러드",
    "그린블러드 특수 아이템을 한글로 표시");
var catalogDisplayNames = catalog.RawcodeCatalog.Keys.Select(code => catalog.Unit("rawcode:" + code).Name).ToList();
Assert(catalogDisplayNames.All(name => !KoreanLabels.ContainsLatin(name) && !name.Contains('[')),
    "모든 카탈로그 패에서 영문과 내부 코드를 숨김");
var greenBloodWarning = empty.Single(x => x.Route.Id == "yamato_greenblood_ivankov").Warnings;
Assert(greenBloodWarning.Any(x => x.Contains("그린블러드") && x.Contains("체력 회복")) &&
       greenBloodWarning.All(x => !x.Contains("greenblood", StringComparison.OrdinalIgnoreCase) &&
                                  !x.Contains("health-recovery", StringComparison.OrdinalIgnoreCase)),
    "추천 경고에 내부 영문 태그 대신 한글 조건을 표시");
RawcodeCodec.TryParse("200h", out var zoroRawcode);
RawcodeCodec.TryParse("ZZZZ", out var unknownRawcode);
var mappedRawcodes = new RawcodeUnitMap(catalog).Map(new Dictionary<uint, int>
{
    [luffyRawcode] = 2,
    [zoroRawcode] = 7,
    [unknownRawcode] = 1
});
// 맵 컨트롤러·헬퍼 CUnit도 로컬 소유로 풀에 들어 있으므로, 어느 데이터에도 없는 rawcode는
// 패에서 빼고 진단으로만 남긴다. 카탈로그에 이름이 있는 카드는 그대로 패에 들어간다.
Assert(mappedRawcodes.Entries.Sum(x => x.Count) == 9, "미등록 rawcode는 패 수량에서 제외");
Assert(mappedRawcodes.Entries.All(x => !x.UnitId.Contains("ZZZZ", StringComparison.OrdinalIgnoreCase)),
    "미등록 rawcode는 패 목록에 표시되지 않음");
Assert(mappedRawcodes.UnknownCount == 1 && mappedRawcodes.UnknownRawcodes.Contains("ZZZZ"),
    "미등록 rawcode는 진단에 남아 별칭 등록 근거가 됨");

// 중앙에서 성장 중인 특별함 유닛은 중립 소유라 로컬 필터에 걸러진다.
// 티어로 후보를 가려내고, 판에 하나뿐일 때만 로컬 패로 인정한다(오귀속 방지).
var growthMap = new RawcodeUnitMap(catalog);
RawcodeCodec.TryParse("510h", out var buggyMagitan);
RawcodeCodec.TryParse("710h", out var kumaSpecial);
Assert(growthMap.IsGrowthUnit(buggyMagitan) && growthMap.IsGrowthUnit(kumaSpecial),
    "특별함 티어 유닛을 성장형 후보로 인식(버기 마기탄·바솔로뮤 쿠마)");
Assert(!growthMap.IsGrowthUnit(zoroRawcode) && !growthMap.IsGrowthUnit(luffyRawcode),
    "흔함 유닛은 성장형 후보가 아님");
RawcodeCodec.TryParse("060h", out var pirateShip);
Assert(!growthMap.IsGrowthUnit(pirateShip), "중립 획득물(해적선)은 성장형 후보가 아님");
Assert(mappedRawcodes.Entries.Any(x => x.UnitId == "luffy_common" && x.Count == 2), "추천 유닛 ID로 rawcode 연결");
Assert(mappedRawcodes.Entries.Any(x => x.UnitId == "rawcode:200h" && x.Count == 7), "이름 카탈로그 유닛을 동적 ID로 보존");
Assert(mappedRawcodes.CatalogNamedCount == 7 && mappedRawcodes.UnknownCount == 1, "이름 매핑과 완전 미등록 rawcode 구분");
var rawcodeMap = new RawcodeUnitMap(catalog);
Assert(rawcodeMap.Error is null, "번들 카드 카탈로그 무결성 검증");
RawcodeCodec.TryParse("260h", out var helperRawcode);
Assert(rawcodeMap.IsRecognizedCard(luffyRawcode) && rawcodeMap.IsRecognizedCard(zoroRawcode) &&
       !rawcodeMap.IsRecognizedCard(helperRawcode),
    "카드 카탈로그와 맵 내부 helper CUnit 구분");
RawcodeCodec.TryParse("H00I", out var itemRawcode);
RawcodeCodec.TryParse("GOLD", out var resourceRawcode);
Assert(rawcodeMap.IsRecognizedCard(itemRawcode) && !rawcodeMap.IsRecognizedCard(resourceRawcode),
    "인벤토리 아이템은 포함하고 자원 pseudo rawcode는 제외");
var strategicRawcodes = new Dictionary<string, string>
{
    ["DB0H"] = "yamato_transcendent",
    ["Y30h"] = "ivankov_hidden",
    ["Q30h"] = "mobydick",
    ["W20h"] = "dragon_legend",
    ["Z20h"] = "bartolomeo_legend",
    ["340h"] = "mihawk_hidden",
    ["F30h"] = "kalgara"
};
var strategicCounts = strategicRawcodes.Keys.ToDictionary(
    text => { RawcodeCodec.TryParse(text, out var code); return code; }, _ => 1);
var strategicMapped = rawcodeMap.Map(strategicCounts);
Assert(strategicRawcodes.Values.All(id => strategicMapped.Entries.Any(x => x.UnitId == id && x.Count == 1)),
    "야마토 핵심 상위 유닛 rawcode를 추천 엔진 ID로 연결");

var catalogYamato = catalog.Unit("yamato_transcendent");
Assert(catalogYamato.Recipe.Count > 0 && !string.IsNullOrWhiteSpace(catalogYamato.Image) &&
       catalog.RawcodeCatalog.ContainsKey("LUMBER"),
    "공식 카탈로그 recipe/image와 자원 pseudo 항목 파싱");
Assert(catalogYamato.OfficialAbilities.Any(x => x.Name == "바제스" && x.DisplayValue == "가능") &&
       catalog.Unit("dragon_legend").OfficialAbilities.Any(x => x.Name == "스턴" && x.DisplayValue == "0.9") &&
       !string.IsNullOrWhiteSpace(catalogYamato.Description),
    "공식 능력 bool/숫자 값을 표시 문자열로 연결하고 설명 제공");
Assert(catalog.RawcodeCatalog.Values.Count(x => x.Abilities.Count > 0) >= 180,
    "티모지지 43747 능력 데이터를 기본 카탈로그 위에 병합");
Assert(catalog.Unit("rawcode:O30h").OfficialAbilities.Any(x => x.Name == "스턴" && x.DisplayValue == "0.5") &&
       catalog.Unit("ivankov_hidden").OfficialAbilities.Any(x => x.Name == "스턴" && x.DisplayValue == "0.5") &&
       catalog.Unit("bartolomeo_legend").OfficialAbilities.Any(x => x.Name == "스턴" && x.DisplayValue == "1") &&
       catalog.Unit("rawcode:Q20h").OfficialAbilities.Any(x => x.Name == "스턴" && x.DisplayValue == "0.9"),
    "43747 기준 봉쿠레·이완코브·바르톨로메오·라분 스턴 수치를 적용");
Assert(catalog.Unit("rawcode:V50h").Tier == "왜곡됨" &&
       catalog.Unit("rawcode:IC0h").Tier == "왜곡됨" &&
       catalog.Unit("rawcode:V30h").Tier == "왜곡됨" &&
       catalog.Unit("rawcode:840h").Tier == "왜곡됨",
    "43747 기준 에이스·퀸·코알라·페로나의 폐기된 구등급을 왜곡됨으로 교정");
var distortedAce = catalog.Unit("rawcode:V50h");
Assert(distortedAce.Recipe.Count == 4 &&
       distortedAce.Recipe.GetValueOrDefault("rawcode:O20h") == 1 &&
       distortedAce.Recipe.GetValueOrDefault("rawcode:Z10h") == 1 &&
       distortedAce.Recipe.GetValueOrDefault("rawcode:210h") == 1 &&
       distortedAce.Recipe.GetValueOrDefault("rawcode:LUMBER") == 3 &&
       distortedAce.Description.Contains("해적왕의 아들", StringComparison.Ordinal),
    "에이스 왜곡됨을 에이스 전설·마젤란·스쿼드·목재3과 해적왕의 아들 조건으로 교정");
Assert(catalog.Unit("mobydick").Tier == "해적선" &&
       catalog.Unit("rawcode:U30h").Tier == "해적선",
    "43747 해적선 장식 문자는 제거하고 앱의 정식 등급명으로 유지");
Assert(catalog.Unit("yamato_transcendent").OfficialAbilities.Any(x =>
        x.Name == "이동속도 감소" && x.DisplayValue == "-10"),
    "43747 기준 야마토 이동속도 보정 수치를 적용");
var dragonAbilityText = RecommendationPresentation.AbilitySummary(new CompositionUnitDetail
{
    UnitId = "dragon_legend",
    Name = "드래곤 전설",
    Abilities = catalog.Unit("dragon_legend").OfficialAbilities
});
Assert(dragonAbilityText.Contains("스턴 0.9") && dragonAbilityText.Contains("이감 10") &&
       dragonAbilityText.Contains("방깎 10") && dragonAbilityText.Contains("공속 5") &&
       dragonAbilityText.Contains("공증 25"),
    "드래곤 전설 실제 능력을 티모지지 수치로 표시");
var dragonRecommendationEffect = RecommendationPresentation.RecommendationEffectLine(
    new CompositionUnitDetail
    {
        UnitId = "dragon_legend",
        Name = "드래곤 전설",
        Abilities = catalog.Unit("dragon_legend").OfficialAbilities
    });
Assert(!dragonRecommendationEffect.StartsWith("효과", StringComparison.Ordinal) &&
       dragonRecommendationEffect.Contains("스턴 0.9", StringComparison.Ordinal) &&
       dragonRecommendationEffect.Contains("방깎 10", StringComparison.Ordinal),
    "추천 카드를 펼치지 않아도 기물의 실제 효과 요약을 접두어 없이 표시");
Assert(!dragonAbilityText.Contains('%') && !KoreanLabels.ContainsLatin(dragonAbilityText),
    "능력 표시에 백분율과 영문 내부 키를 노출하지 않음");
var safeOfficialDescriptions = catalog.RawcodeCatalog.Keys
    .Select(code => catalog.Unit("rawcode:" + code).Description)
    .Where(text => !string.IsNullOrWhiteSpace(text))
    .ToList();
Assert(safeOfficialDescriptions.All(text => !text.Contains('%') && !KoreanLabels.ContainsLatin(text)),
    "공식 설명의 백분율 기호와 영문 명령 키를 사용자 표시에서 정제");
Assert(RecommendationPresentation.SafeDescription("공격 20% 강화 LV2") == "공격 20퍼센트 강화 2",
    "실제 효과의 퍼센트 단위는 한글 단어로 표시");
var recipeFixture = new Dictionary<string, UnitDefinition>(StringComparer.OrdinalIgnoreCase)
{
    ["leaf_a"] = TestUnit("leaf_a", "재료 가"),
    ["leaf_b"] = TestUnit("leaf_b", "재료 나"),
    ["middle"] = TestUnit("middle", "중간 패", new Dictionary<string, int>
    {
        ["leaf_a"] = 2,
        ["leaf_b"] = 1
    }),
    ["top"] = TestUnit("top", "상위 패", new Dictionary<string, int>
    {
        ["middle"] = 2,
        ["leaf_a"] = 1,
        ["rawcode:GOLD"] = 999,
        ["rawcode:RANDOM"] = 1
    }),
    ["side"] = TestUnit("side", "보조 패", new Dictionary<string, int> { ["middle"] = 1 }),
    ["rawcode:GOLD"] = TestUnit("rawcode:GOLD", "금화", tier: "자원", rawcodes: ["GOLD"]),
    ["rawcode:RANDOM"] = TestUnit("rawcode:RANDOM", "랜덤유닛", tier: "기타", rawcodes: ["RANDOM"])
};
var recipeCalculator = new RecipeCompletionCalculator(id => recipeFixture.TryGetValue(id, out var unit)
    ? unit
    : TestUnit(id, "미등록 패"));
var emptyRecipeProgress = recipeCalculator.Calculate(["top", "side"],
    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
Assert(emptyRecipeProgress.RequiredLeafCount == 10 && emptyRecipeProgress.OwnedLeafCount == 0 &&
       emptyRecipeProgress.MissingLeaves.Single(x => x.UnitId == "leaf_a").MissingCount == 7 &&
       emptyRecipeProgress.MissingLeaves.Single(x => x.UnitId == "leaf_b").MissingCount == 3,
    "재귀 조합식을 최하위 패와 정확한 필요 수량으로 전개");
Assert(emptyRecipeProgress.Leaves.All(x => !x.UnitId.Contains("GOLD") && !x.UnitId.Contains("RANDOM")),
    "금화/목재/포인트/랜덤 pseudo는 패 완성도 분모에서 제외");
var allocatedRecipeProgress = recipeCalculator.Calculate(["top", "side"],
    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["middle"] = 1,
        ["leaf_a"] = 3,
        ["leaf_b"] = 1
    });
Assert(allocatedRecipeProgress.RequiredLeafCount == 10 && allocatedRecipeProgress.OwnedLeafCount == 7 &&
       allocatedRecipeProgress.MissingLeaves.Single(x => x.UnitId == "leaf_a").MissingCount == 2 &&
       allocatedRecipeProgress.MissingLeaves.Single(x => x.UnitId == "leaf_b").MissingCount == 1,
    "중간 패를 하위 재료로 환산하고 여러 분기에서 보유 패를 중복 소비하지 않음");
var completedUpperProgress = recipeCalculator.Calculate(["top"],
    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["top"] = 1,
        ["leaf_a"] = 100
    });
Assert(completedUpperProgress.RequiredLeafCount == 7 && completedUpperProgress.OwnedLeafCount == 7 &&
       completedUpperProgress.MissingLeaves.Count == 0 && completedUpperProgress.CompletionRatio == 1,
    "완성된 상위 패가 전체 하위 트리를 대체하고 남는 재료는 과집계하지 않음");

var emptyDragon = empty.Single(x => x.Route.Id == "yamato_dragon");
var goalOwnedDragon = engine.Recommend("yamato_transcendent", Inventory("yamato_transcendent"))
    .Single(x => x.Route.Id == "yamato_dragon");
Assert(emptyDragon.RecipeProgress.RequiredLeafCount > 0 && emptyDragon.RecipeProgress.OwnedLeafCount == 0 &&
       goalOwnedDragon.RecipeProgress.RequiredLeafCount == emptyDragon.RecipeProgress.RequiredLeafCount &&
       goalOwnedDragon.RecipeProgress.OwnedLeafCount > 0,
    "추천 진행 분모에 목표 유닛을 포함하고 목표 보유 시 해당 서브트리 완료 처리");
Assert(emptyDragon.CompositionUnits.Any(x => x.IsGoal && x.UnitId == "yamato_transcendent") &&
       emptyDragon.CompositionUnits.Any(x => x.IsRequired && x.UnitId == "dragon_legend") &&
       emptyDragon.CompositionUnits.Count(x => x.IsOptional) == 2 &&
       emptyDragon.CompositionUnits.Single(x => x.UnitId == "yamato_transcendent").Abilities.Count > 0,
    "목표/필수/보강 구성 유닛과 공식 능력 수치를 추천 결과에 제공");

var verifiedProfile = ValidMemoryProfile(enabled: true, verified: true);
Assert(MemoryProfileValidator.CanActivate(verifiedProfile, out var verifiedErrors) && verifiedErrors.Count == 0,
    "검증 프로필 활성화 허용");
var disabledProfile = ValidMemoryProfile(enabled: false, verified: true);
Assert(!MemoryProfileValidator.CanActivate(disabledProfile, out _), "비활성 프로필 차단");
var unverifiedProfile = ValidMemoryProfile(enabled: true, verified: false);
Assert(!MemoryProfileValidator.CanActivate(unverifiedProfile, out _), "미검증 프로필 차단");
Assert(!MemoryProfileValidator.CanActivate(ValidMemoryProfile(true, true, -0.1), out _),
    "잘못된 rawcode 일치율 프로필 차단");

// 실측 빌드에 핀된 번들 메모리 프로필 고정(Data/memory-profiles.json).
// 오프셋 출처: work/war3_objects_findings.md — GetUnitPool은 worldFrame+0xB98(count)/+0xBA0(pool),
// CUnit rawcode는 +0x178, owner 바이트는 +0x1C0. sha256은 설치된 Warcraft III.exe 실측값.
const string PinnedWarcraftSha256 = "682C12552CA05E43C5FED2340EA132D3B06FE068E676DB7D1F5623D8D4633229";
var bundledProfilePath = Path.Combine(AppContext.BaseDirectory, "Data", "memory-profiles.json");
Assert(File.Exists(bundledProfilePath), "번들 메모리 프로필 파일이 배포본에 포함됨");
var bundledProfiles = JsonSerializer.Deserialize<List<MemoryProfile>>(File.ReadAllText(bundledProfilePath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
Assert(bundledProfiles.Count == 1, "번들 프로필은 실측된 빌드 1개만 핀");
var pinnedProfile = bundledProfiles[0];
Assert(pinnedProfile.FileVersion == "2.0.4.23745" && pinnedProfile.ModuleName == "Warcraft III.exe",
    "핀된 프로필이 실측 Warcraft III 빌드를 가리킴");
Assert(string.Equals(pinnedProfile.ExecutableSha256, PinnedWarcraftSha256, StringComparison.OrdinalIgnoreCase),
    "핀된 프로필 sha256이 실측 해시와 일치");
Assert(string.Equals(pinnedProfile.Sha256, PinnedWarcraftSha256, StringComparison.OrdinalIgnoreCase),
    "핀된 프로필이 정식 sha256 필드로 빌드 해시를 보관");
Assert(File.ReadAllText(bundledProfilePath).Contains("\"sha256\"", StringComparison.Ordinal),
    "번들 프로필 JSON이 sha256 필드명을 사용");
Assert(string.Equals(JsonSerializer.Deserialize<MemoryProfile>(
        $"{{\"executableSha256\":\"{PinnedWarcraftSha256}\"}}",
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.ExecutableSha256,
    PinnedWarcraftSha256, StringComparison.OrdinalIgnoreCase),
    "구 스키마 executableSha256 별칭도 실효 해시로 해석됨");
Assert(!MemoryProfileValidator.CanActivate(ValidMemoryProfile(true, true, sha256: ""), out _),
    "sha256이 비어 있으면 프로필 활성화를 하드 차단");
// verified=true는 실전 대조를 통과한 뒤에만 플립한다. 2026-08-19 라이브 세션에서 통과해 핀됨.
Assert(pinnedProfile.Enabled && pinnedProfile.Verified, "핀된 프로필이 enabled=true, verified=true");
Assert(MemoryProfileValidator.CanActivate(pinnedProfile, out var pinnedErrors) && pinnedErrors.Count == 0,
    "검증된 프로필이 하드 게이트를 통과해 단독 인식에 활성화됨");
var pinnedNode = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(pinnedProfile))!.AsObject();
pinnedNode[pinnedNode.ContainsKey("verified") ? "verified" : "Verified"] = false;
var pinnedUnverifiedCopy = JsonSerializer.Deserialize<MemoryProfile>(
    pinnedNode.ToJsonString(),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
Assert(!MemoryProfileValidator.CanActivate(pinnedUnverifiedCopy, out _),
    "미검증 상태로 되돌리면 하드 게이트에 막혀 활성화되지 않음(fail-closed 유지)");
Assert(pinnedProfile.CountOffset == 0xB98 && pinnedProfile.EntriesPointerOffset == 0xBA0,
    "실측 유닛 풀 오프셋 worldFrame+0xB98/+0xBA0 유지");
Assert(pinnedProfile.RawcodeOffset == 0x178 && pinnedProfile.OwnerOffset == 0x1C0,
    "실측 CUnit rawcode+0x178 / owner+0x1C0 오프셋 유지");
Assert(pinnedProfile.EntriesContainPointers && !pinnedProfile.EntriesAreInline && pinnedProfile.EntryStride == 8,
    "실측 8바이트 포인터 배열 순회 형태 유지");
// maximumUnits는 실측 풀 상한(0x1FFF)까지 허용한다 — 그 위는 검증 범위 밖으로 보고 차단.
Assert(pinnedProfile.MaximumUnits == 0x1FFF && pinnedProfile.RequireNonEmptyInventory &&
       Math.Abs(pinnedProfile.MinimumCatalogMatchRatio - 0.2) < 1e-9,
    "핀된 프로필이 fail-closed 가드값을 그대로 유지");
Assert(pinnedProfile.LocatorKind == MemoryLocatorKind.StructuralScan &&
       pinnedProfile.UnitClassName == ".?AVCUnit@@" && pinnedProfile.PointerOffsets.Length == 0,
    "핀된 프로필이 RTTI 기반 구조 스캔으로 루트를 찾음(추정 AOB 시그니처 없음)");
Assert(pinnedProfile.HasLocalPlayerAnchor && pinnedProfile.LocalPlayerIdOffset == 0x2664,
    "핀된 프로필이 로컬 플레이어 슬롯을 실측 앵커로 읽음");
var installedWarcraftPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    "Warcraft III", "_retail_", "x86_64", "Warcraft III.exe");
Assert(!File.Exists(installedWarcraftPath) || string.Equals(
        Convert.ToHexString(SHA256.HashData(File.OpenRead(installedWarcraftPath))),
        pinnedProfile.ExecutableSha256, StringComparison.OrdinalIgnoreCase),
    "설치된 Warcraft III.exe가 있으면 핀된 sha256과 실제 해시가 일치");
Assert(new RecognitionResult { State = RecognitionState.Ready }.ShouldReplaceInventory,
    "검증된 0장 Ready는 마지막 패를 지움");
Assert(!new RecognitionResult { State = RecognitionState.TransientReadError }.ShouldReplaceInventory,
    "일시 읽기 실패 시 기존 패 유지");
Assert(!new RecognitionResult { Entries = [], State = RecognitionState.Waiting }.ShouldReplaceInventory,
    "게임 밖 Waiting은 빈 결과이며 기존 패를 덮지 않음");
Assert(new RecognitionResult { State = RecognitionState.Waiting }.ShouldClearAutomaticInventory &&
       !new RecognitionResult { State = RecognitionState.TransientReadError }.ShouldClearAutomaticInventory,
    "Waiting은 현재 자동 패를 비우고 일시 race는 보존");
Assert(new AppSettings().AutoScanEnabled, "첫 실행은 메모리 자동 인식을 바로 시작");
Assert(!new AppSettings().ClickThroughOverlay, "첫 실행 오버레이는 즉시 드래그 가능");
string[] screenCaptureSettingMarkers = ["Recognition" + "Source", "Inventory" + "Region", "Capture" + "Interval"];
Assert(typeof(AppSettings).GetProperties().All(p =>
           !screenCaptureSettingMarkers.Any(m => p.Name.Contains(m, StringComparison.Ordinal))),
    "화면 인식용 설정(인식 소스 선택·캡처 영역·캡처 주기)이 AppSettings에 남아 있지 않음");
Assert(RecognitionPolicy.MayUseLastGoodForRecommendations(RecognitionState.TransientReadError) &&
       !RecognitionPolicy.MayUseLastGoodForRecommendations(RecognitionState.Waiting),
    "일시 race만 이전 추천을 유지하고 게임 종료 상태는 추천에서 제외");
Assert(RecognitionPolicy.IsConfirmedOutOfGame(new RecognitionResult
       {
           State = RecognitionState.Waiting,
           ConfirmsSessionBoundary = true
       }) &&
       !RecognitionPolicy.IsConfirmedOutOfGame(new RecognitionResult { State = RecognitionState.Waiting }),
    "확인된 게임 경계만 수동 보정을 지우고 일시 읽기 오류는 보존");
// 실패 클래스별 상태 전이: fail-closed(추천 중단) vs fail-soft(마지막 정상 유지).
// 게임 미실행(Waiting)은 위 줄(ShouldReplaceInventory·ShouldClearAutomaticInventory·MayUseLastGoodForRecommendations)에서 이미 커버.
Assert(!RecognitionPolicy.MayUseLastGoodForRecommendations(RecognitionState.UnverifiedProfile) &&
       !RecognitionPolicy.MayUseLastGoodForRecommendations(RecognitionState.Unsupported),
    "미검증 프로필·미지원 빌드는 fail-closed: 마지막 정상 추천을 유지하지 않음");
Assert(!new RecognitionResult { State = RecognitionState.UnverifiedProfile }.ShouldReplaceInventory &&
       !new RecognitionResult { State = RecognitionState.UnverifiedProfile }.ShouldClearAutomaticInventory,
    "미검증 프로필은 기존 패를 덮지 않고 자동 패 초기화도 하지 않음(배너만 표시)");
// 스테일 루트(WC3 재시작·로비 재진입 뒤 루트 주소 무효화)는 TransientReadError로 분류된다.
// LocatorCache가 무효화된 뒤 ReadConsistentSnapshot 재시도가 실패하면 이 상태로 전이한다.
Assert(RecognitionPolicy.MayUseLastGoodForRecommendations(RecognitionState.TransientReadError) &&
       !new RecognitionResult { State = RecognitionState.TransientReadError }.ShouldClearAutomaticInventory,
    "스테일 루트·일시 읽기 오류는 TransientReadError로 분류되어 마지막 정상 패와 추천을 유지");
var correctedInventory = InventoryMerge.ApplyCorrections(
    [new InventoryEntry { UnitId = "luffy_common", Count = 2, Confidence = 1 }],
    [new InventoryEntry { UnitId = "luffy_common", Count = -1, IsManual = true }]);
Assert(correctedInventory.Single().Count == 1, "음수 수동 보정이 다음 자동 스캔 뒤에도 유지");
Assert(InventoryMerge.CanDecrement(correctedInventory, "luffy_common") &&
       !InventoryMerge.CanDecrement([], "luffy_common"),
    "현재 수량 0 아래로 숨은 음수 수동 보정이 쌓이지 않음");
var negativeMonitorPosition = OverlayPositionPolicy.Clamp(-1200, 80, 390, 510,
    -1920, 0, 3840, 1080);
var removedMonitorPosition = OverlayPositionPolicy.Clamp(5000, 3000, 390, 510,
    0, 0, 1920, 1080);
var nonRectangularPosition = OverlayPositionPolicy.ClampToNearestWorkArea(2000, 800, 390, 510,
    [new OverlayBounds(0, 0, 1920, 1080), new OverlayBounds(1920, 1080, 1920, 1080)]);
Assert(negativeMonitorPosition == new OverlayPosition(-1200, 80) &&
       removedMonitorPosition == new OverlayPosition(1530, 570) &&
       nonRectangularPosition.Top >= 1080,
    "좌측 모니터 음수 좌표를 유지하고 제거된 모니터 위치를 화면 안으로 보정");
var fhd = new OverlayBounds(0, 0, 1920, 1080);
var leftDocked = new OverlayBounds(0, 48, 300, 700);
var rightDocked = new OverlayBounds(1620, 48, 300, 700);
var inner = new OverlayBounds(800, 200, 300, 400);
Assert(OverlayPositionPolicy.CursorNeedsEdgePanPassThrough(2, 200, leftDocked, fhd, 8),
    "왼쪽 끝에 붙인 오버레이 위에서 가장자리 카메라는 클릭을 게임에 넘겨야 한다");
Assert(OverlayPositionPolicy.CursorNeedsEdgePanPassThrough(1917, 200, rightDocked, fhd, 8),
    "오른쪽 끝에 붙인 오버레이 위에서 가장자리 카메라는 클릭을 게임에 넘겨야 한다");
Assert(!OverlayPositionPolicy.CursorNeedsEdgePanPassThrough(150, 200, leftDocked, fhd, 8),
    "오버레이 안쪽(보드·수치)은 그대로 클릭 가능해야 한다");
Assert(!OverlayPositionPolicy.CursorNeedsEdgePanPassThrough(2, 200, inner, fhd, 8),
    "화면 한가운데 오버레이는 모니터 끝 커서에 반응하지 않는다");
Assert(!OverlayPositionPolicy.CursorNeedsEdgePanPassThrough(2, 10, leftDocked, fhd, 8),
    "오버레이 밖 가장자리 커서는 이 창이 가로채지 않는다");
Assert(!OverlayPositionPolicy.CursorNeedsEdgePanPassThrough(0, 200, leftDocked, fhd, 0),
    "여백 0이면 가장자리 통과를 켜지 않는다");
Assert(OverlayPositionPolicy.ClampToNearestWorkArea(0, 80, 300, 700, [fhd]) ==
       new OverlayPosition(0, 80),
    "가장자리 카메라 통과는 창을 모니터에서 밀어내지 않는다");
var singletonInventory = InventoryMerge.ApplyCorrections(
    [new InventoryEntry { UnitId = "item_greenblood", Count = 1, Confidence = 1 }],
    [new InventoryEntry { UnitId = "item_greenblood", Count = 1, IsManual = true }],
    id => id == "item_greenblood");
Assert(singletonInventory.Single().Count == 1, "그린블러드 자동/수동 중복을 1개 상태로 병합");
Assert(WarcraftInventoryHandle.IsEmpty(0) && WarcraftInventoryHandle.IsEmpty(ulong.MaxValue) &&
       !WarcraftInventoryHandle.IsEmpty(0x0000000100000001),
    "Reforged 인벤토리 빈 슬롯 0/-1 센티널 구분");
var fixture64 = new Dictionary<ulong, ulong>
{
    [0x100018] = 0x200000,
    [0x100050] = 0x210000,
    [0x200018] = 0x300000,
    [0x210018] = 0x310000
};
var fixture32 = new Dictionary<ulong, uint>
{
    [0x100030] = 2,
    [0x100068] = 2,
    [0x200010] = 0xFFFF_FFFE,
    [0x210010] = 0xFFFF_FFFE,
    [0x300024] = 1,
    [0x310024] = 2
};
Assert(WarcraftHandleResolver.TryResolve(address => fixture64[address], address => fixture32[address],
           0x100000, 0x0000000100000001, out var lowResolved) && lowResolved == 0x300000 &&
       WarcraftHandleResolver.TryResolve(address => fixture64[address], address => fixture32[address],
           0x100000, 0x0000000280000001, out var highResolved) && highResolved == 0x310000,
    "Reforged low/high handle table과 generation fixture 해석");
Assert(WarcraftGreenBloodProbe.Evaluate(new Dictionary<uint, uint>()) == GreenBloodProbeState.Unknown &&
       WarcraftGreenBloodProbe.Evaluate(new Dictionary<uint, uint>
       {
           [WarcraftGreenBloodProbe.ControllerBaselineAbility] = 3
       }) == GreenBloodProbeState.Absent,
    "컨트롤러 기본 능력으로 그린블러드 상태 신뢰성 gate");
Assert(WarcraftGreenBloodProbe.Evaluate(new Dictionary<uint, uint>
       {
           [WarcraftGreenBloodProbe.ControllerBaselineAbility] = 3,
           [WarcraftGreenBloodProbe.HeldAbility] = 1
       }) == GreenBloodProbeState.Held,
    "A13A 능력을 미사용 그린블러드 보유로 판정");
Assert(WarcraftGreenBloodProbe.Combine(GreenBloodProbeState.Held, GreenBloodProbeState.Held) ==
           GreenBloodProbeState.Held &&
       WarcraftGreenBloodProbe.Combine(GreenBloodProbeState.Absent, GreenBloodProbeState.Absent) ==
           GreenBloodProbeState.Absent &&
       WarcraftGreenBloodProbe.Combine(GreenBloodProbeState.Held, GreenBloodProbeState.Unknown) ==
           GreenBloodProbeState.Unknown &&
       WarcraftGreenBloodProbe.Combine(GreenBloodProbeState.Held, GreenBloodProbeState.Absent) ==
           GreenBloodProbeState.Unknown,
    "그린블러드는 두 독립 탐색이 같을 때만 확정");

var generatorBytes = Convert.FromHexString(
    "48897C2418488BFA488BD10F1F4400009080E7FF8AD248B900006AC0E1010000" +
    "0F1F80000000009080E2FF86ED488B413049B82C1B3D5D97A910554833024933C0488902");
Assert(WarcraftDecoder.TryParseGeneratedDecoder(generatorBytes, out var generator),
    "워크 생성 디코더 고정 서명 검증");
Assert(generator.StateAddress == 0x1E1C06A0000 && generator.XorMask == 0x5510A9975D3D1B2C,
    "워크 생성 디코더 state/xor 추출");
var gameUiKeys = WarcraftDecoder.DeriveKeys(WarcraftDecoder.GameUiSeed1,
    WarcraftDecoder.GameUiSeed2, 0x1B9BB090000, generator.XorMask);
Assert(gameUiKeys == new DecoderKeys(0x4C071947016892B1, 0x7384073D3D981D6F),
    "워크 GameUI 런타임 키 파생");
Assert(WarcraftDecoder.DecodeGameUi(_ => 0, 0, gameUiKeys) == 0xE95C522F3D981D6F,
    "GameUI 복호 산술 고정 fixture");

// --- 신+ 클리어 데이터 최적화 ---
var clearT0 = DateTimeOffset.Parse("2026-08-16T00:00:00+00:00");

var qualitySamples = new List<ClearSample>();
for (var i = 0; i < 6; i++)
{
    qualitySamples.Add(GodClear($"q-small-{i}", 8, clearT0,
        ("DB0H", "초월 [물딜]"), ("AAAA", "")));
    qualitySamples.Add(GodClear($"q-big-{i}", 24, clearT0,
        ("DB0H", "초월 [물딜]"), ("BBBB", "")));
}
var qualityStats = ClearBuildStats.FromSamples(qualitySamples);
var qualityProfile = qualityStats.GoalProfile(["DB0H"])!;
Assert(qualityProfile.SampleCount == 12, "클리어 목표 표본 집계");
Assert(qualityProfile.SupportShare["AAAA"] > qualityProfile.SupportShare["BBBB"],
    "유닛 수가 적은 클리어의 지원 유닛을 더 잘 짠 빌드로 가중");
Assert(qualityStats.PriorityScore(["DB0H"], ["AAAA"])!.Value >
       qualityStats.PriorityScore(["DB0H"], ["BBBB"])!.Value,
    "클리어 우선순위 점수도 유닛 수 품질을 반영");

var recencySamples = new List<ClearSample>();
for (var i = 0; i < 6; i++)
{
    recencySamples.Add(GodClear($"r-new-{i}", 12, clearT0,
        ("DB0H", "초월 [물딜]"), ("AAAA", "")));
    recencySamples.Add(GodClear($"r-old-{i}", 12, clearT0.AddDays(-28),
        ("DB0H", "초월 [물딜]"), ("BBBB", "")));
}
var recencyProfile = ClearBuildStats.FromSamples(recencySamples).GoalProfile(["DB0H"])!;
Assert(recencyProfile.SupportShare["AAAA"] > recencyProfile.SupportShare["BBBB"] * 3,
    "최신 클리어를 오래된 클리어보다 강하게 반영");

var difficultyStats = ClearBuildStats.FromSamples(
[
    GodClear("d-god", 12, clearT0, ("DB0H", "초월 [물딜]"), ("AAAA", "")),
    new ClearSample("d-hell", clearT0, "지옥", 12,
        [new ClearSampleUnit("DB0H", 1, "초월 [물딜]"), new ClearSampleUnit("CCCC", 1, "")]),
    new ClearSample("d-night", clearT0, "악몽", 12,
        [new ClearSampleUnit("DB0H", 1, "초월 [물딜]"), new ClearSampleUnit("AAAA", 1, "")])
]);
Assert(difficultyStats.TotalGodPlusSamples == 2, "지옥 클리어는 신+ 집계에서 제외");
Assert(!difficultyStats.GoalProfile(["DB0H"])!.SupportShare.ContainsKey("CCCC"),
    "지옥 표본 유닛은 채용률에 나타나지 않음");

var fewSamples = Enumerable.Range(0, 11).Select(i =>
    GodClear($"f-{i}", 12, clearT0, ("DB0H", "초월 [물딜]"), ("AAAA", ""))).ToList();
Assert(ClearBuildStats.FromSamples(fewSamples).PriorityScore(["DB0H"], ["AAAA"]) is null,
    "표본 12판 미만 목표는 수작업 커뮤니티 테이블로 후퇴");
fewSamples.Add(GodClear("f-11", 12, clearT0, ("DB0H", "초월 [물딜]"), ("AAAA", "")));
Assert(ClearBuildStats.FromSamples(fewSamples).PriorityScore(["DB0H"], ["AAAA"]) == 100,
    "표본 12판부터 실측 채용률 점수 사용");

var baselineCrafts = engine.RecommendNearestCrafts("yamato_transcendent", Inventory("dragon_legend"));
var baselineSupport = baselineCrafts.First(item =>
    !item.Route.GoalUnitId.Equals("yamato_transcendent", StringComparison.OrdinalIgnoreCase) &&
    catalog.Unit(item.Route.GoalUnitId).Rawcodes.Count > 0);
Assert(baselineSupport.ClearEvidence is null, "클리어 데이터가 없으면 근거 표기도 없음");
var evidenceRawcode = catalog.Unit(baselineSupport.Route.GoalUnitId).Rawcodes[0];
var evidenceSamples = Enumerable.Range(0, 12).Select(i =>
    GodClear($"e-{i}", 12, clearT0, ("DB0H", "초월 [물딜]"), (evidenceRawcode, ""))).ToList();
var evidenceEngine = new RecommendationEngine(catalog, ClearBuildStats.FromSamples(evidenceSamples));
var evidenceCrafts = evidenceEngine.RecommendNearestCrafts("yamato_transcendent", Inventory("dragon_legend"));
var evidenceSupport = evidenceCrafts.FirstOrDefault(item =>
    item.Route.GoalUnitId.Equals(baselineSupport.Route.GoalUnitId, StringComparison.OrdinalIgnoreCase));
Assert(evidenceSupport?.ClearEvidence is { SampleCount: 12, SharePercent: 100 },
    "추천 카드에 신+ 클리어 표본과 채용률 근거 표기");
Assert(evidenceCrafts.Count > 1 &&
       evidenceCrafts[1].Route.GoalUnitId.Equals(baselineSupport.Route.GoalUnitId,
           StringComparison.OrdinalIgnoreCase),
    "채용률이 높은 지원을 목표 바로 다음 순위로 표시");

var greenBloodAdvisor = new GreenBloodAdvisor(catalog);
var yamatoGoal = catalog.Unit("yamato_transcendent");
var planAdvice = greenBloodAdvisor.Evaluate(yamatoGoal, Inventory("mihawk_hidden"), [], null);
Assert(planAdvice.Count == 1 && planAdvice[0].UnitId == "mihawk_hidden" &&
       !GreenBloodAdvisor.HasUnusedGreenBlood(catalog, Inventory("mihawk_hidden")) &&
       GreenBloodAdvisor.HasUnusedGreenBlood(catalog, Inventory("item_greenblood")),
    "신 기준 획득 확정: 그린블러드 미보유여도 사용 계획을 항상 표시");
var greenBloodAdvice = greenBloodAdvisor.Evaluate(yamatoGoal,
    Inventory("item_greenblood", "mihawk_hidden", "rawcode:030h"), [], null);
Assert(greenBloodAdvice.Count == 2 && greenBloodAdvice[0].UnitId == "mihawk_hidden",
    "그린블러드 우선 태그 유닛을 먼저 추천");
Assert(greenBloodAdvice[^1].UnitId == "rawcode:030h" && greenBloodAdvice[^1].Warning is not null,
    "쿠마에는 스턴 소실 경고를 붙이고 후순위");
var distortionRecommendations = new List<Recommendation>
{
    new() { Route = new RouteDefinition { Id = "warp-test", GoalUnitId = "rawcode:V30h", Name = "코알라 왜곡" } },
    new() { Route = new RouteDefinition { Id = "warp-test-2", GoalUnitId = "rawcode:V50h", Name = "에이스 왜곡" } }
};
var legendHiddenOnly = greenBloodAdvisor.Evaluate(yamatoGoal,
    Inventory("item_greenblood", "mihawk_hidden"), distortionRecommendations, null);
Assert(legendHiddenOnly.Count == 1 && legendHiddenOnly[0].UnitId == "mihawk_hidden",
    "그린블러드는 전설·히든 부여 대상만 추천(왜곡 유닛 조합과 무관)");
var plannedFallback = greenBloodAdvisor.Evaluate(yamatoGoal, [],
    [new Recommendation { Route = new RouteDefinition { Id = "gb-plan", GoalUnitId = "mihawk_hidden", Name = "미호크" } }],
    null);
Assert(plannedFallback.Count == 1 && plannedFallback[0].UnitId == "mihawk_hidden",
    "보유 전설·히든이 없으면 추천 조합의 전설·히든으로 사용처 계획 유지");
Assert(legendHiddenOnly.All(item =>
        item.UnitId != "rawcode:V50h" && item.UnitId != "rawcode:V30h"),
    "왜곡 유닛에는 그린블러드를 추천하지 않음");

var cachePath = Path.Combine(Path.GetTempPath(), $"orand-clear-cache-test-{Guid.NewGuid():N}.json");
try
{
    var firstMerge = ClearSnapshotRefreshService.MergeIntoCache(cachePath,
        [GodClear("m-1", 10, clearT0, ("DB0H", "초월 [물딜]")),
         GodClear("m-2", 11, clearT0, ("DB0H", "초월 [물딜]"))], "t");
    Assert(firstMerge.Count == 2 && File.Exists(cachePath), "클리어 캐시 최초 병합 저장");
    var secondMerge = ClearSnapshotRefreshService.MergeIntoCache(cachePath,
        [GodClear("m-2", 11, clearT0, ("DB0H", "초월 [물딜]")),
         GodClear("m-3", 12, clearT0.AddMinutes(1), ("DB0H", "초월 [물딜]"))], "t");
    Assert(secondMerge.Count == 3, "캐시 병합은 id 중복을 제거");
    File.WriteAllText(cachePath, "{corrupted");
    var repairedMerge = ClearSnapshotRefreshService.MergeIntoCache(cachePath,
        [GodClear("m-4", 9, clearT0, ("DB0H", "초월 [물딜]"))], "t");
    Assert(repairedMerge.Count == 1, "손상 캐시는 무시하고 재작성");
}
finally
{
    File.Delete(cachePath);
}

// 순위 캐스케이드: 위 순위 빌드가 소비한 카드는 아래 순위 완료율에 이중 집계되지 않는다.
var cascadeBase = engine.RecommendNearestCrafts("yamato_transcendent", [], 8);
var cascadeSharedLeaf = cascadeBase[0].RecipeProgress.Leaves
    .FirstOrDefault(leaf => cascadeBase.Skip(1).Any(other =>
        other.RecipeProgress.Leaves.Any(x =>
            x.UnitId.Equals(leaf.UnitId, StringComparison.OrdinalIgnoreCase))));
Assert(cascadeSharedLeaf is not null, "1번과 하위 순위가 공유하는 재료 존재(캐스케이드 전제)");
var cascadeRecommendations = engine.RecommendNearestCrafts("yamato_transcendent",
    Inventory(cascadeSharedLeaf!.UnitId), 8);
var cascadeAllocated = cascadeRecommendations.Sum(rec => rec.RecipeProgress.Leaves
    .Where(leaf => leaf.UnitId.Equals(cascadeSharedLeaf.UnitId, StringComparison.OrdinalIgnoreCase))
    .Sum(leaf => leaf.OwnedCount));
Assert(cascadeAllocated == 1,
    "카드 1장은 전체 순위를 통틀어 한 번만 완료율에 집계(위 순위가 소비하면 아래 순위 제외)");

// 클라이언트 갱신은 저장소(GitHub) 스냅샷만 사용한다 — 티모지지 서버 미접속.
Assert(ClearSnapshotRefreshService.ParseManifest(
        "{\"schemaVersion\":1,\"newestSampleAt\":\"2026-08-16T00:00:00Z\",\"sampleCount\":3}")
    == DateTimeOffset.Parse("2026-08-16T00:00:00Z"), "스냅샷 매니페스트 해석");
Assert(ClearSnapshotRefreshService.ParseManifest("not-json") is null,
    "손상 매니페스트는 무해하게 무시");
var snapshotCalls = new List<string>();
var snapshotJson = ClearSampleDocument.Serialize(
    [GodClear("g-new", 10, clearT0.AddDays(2), ("DB0H", "초월 [물딜]")),
     GodClear("g-old", 10, clearT0.AddDays(-30), ("DB0H", "초월 [물딜]"))], "test", "now");
var manifestJson = "{\"schemaVersion\":1,\"newestSampleAt\":\"" +
                   clearT0.AddDays(2).ToString("yyyy-MM-ddTHH:mm:ssZ") +
                   "\",\"sampleCount\":2}";
var snapshotService = new ClearSnapshotRefreshService(url =>
{
    snapshotCalls.Add(url);
    return Task.FromResult(url == ClearSnapshotRefreshService.ManifestUrl
        ? manifestJson
        : snapshotJson);
});
var freshSamples = snapshotService.FetchNewSamplesAsync(clearT0).GetAwaiter().GetResult();
Assert(freshSamples.Count == 1 && freshSamples[0].Id == "g-new",
    "저장소 스냅샷에서 로컬 최신 이후 표본만 수신");
var idleService = new ClearSnapshotRefreshService(url =>
{
    snapshotCalls.Add(url);
    return Task.FromResult(manifestJson);
});
Assert(idleService.FetchNewSamplesAsync(clearT0.AddDays(3)).GetAwaiter().GetResult().Count == 0,
    "스냅샷이 더 새롭지 않으면 매니페스트만 보고 전체 내려받기 생략");
Assert(snapshotCalls.All(url =>
        url.StartsWith("https://raw.githubusercontent.com/", StringComparison.Ordinal)),
    "클라이언트 갱신 요청은 전부 저장소로만 향함");

var roundTrip = ClearSampleDocument.Parse(ClearSampleDocument.Serialize(
    [GodClear("s-1", 10, clearT0, ("DB0H", "초월 [물딜]"))], "test", "now"));
Assert(roundTrip.Count == 1 && roundTrip[0].Units[0].Grade == "초월 [물딜]",
    "표본 직렬화 왕복 보존");

var bountyEmpty = engine.RecommendNearestCrafts("yamato_transcendent", []);
var bountyUnits = bountyEmpty.Select(item => catalog.Unit(item.Route.GoalUnitId)).ToList();
Assert(!bountyUnits.Any(unit => unit.Rawcodes.Contains("140h", StringComparer.Ordinal)),
    "야마토 스턴 축은 최소 기수로 채워 아오키지 히든을 끼워넣지 않음");
Assert(bountyUnits.Any(unit => unit.Rawcodes.Any(code => code is "W20h" or "Z20h")),
    "야마토 스턴 축은 주 홀더(드래곤·바르톨로메오)로 시작");

var rerollAdvisor = new RareRerollAdvisor(catalog);
var bonClayPlan = new Recommendation
{
    Route = new RouteDefinition { Id = "sell-test", GoalUnitId = "rawcode:O30h", Name = "봉쿠레" }
};
var rerollWhileMissing = rerollAdvisor.Evaluate(
    Inventory("rawcode:Z10h", "rawcode:Z10h", "rawcode:Z10h", "rawcode:Z10h", "rawcode:Z10h",
        "rawcode:Z10h", "rawcode:Z10h", "rawcode:Z10h", "rawcode:Z10h"), [bonClayPlan]);
Assert(rerollWhileMissing.Count > 0 && rerollWhileMissing.All(item => !item.Sell),
    "부족한 희귀패가 남아 있으면 초과분은 리롤 후보");
var sellWhenComplete = rerollAdvisor.Evaluate(
    Inventory("rawcode:O30h", "rawcode:Z10h", "rawcode:Z10h"), [bonClayPlan]);
Assert(sellWhenComplete.Count > 0 && sellWhenComplete.All(item => item.Sell) &&
       sellWhenComplete[0].Reason.Contains("부족한 희귀패 없음"),
    "부족한 희귀패가 없으면 남는 희귀패는 판매 추천");

// 1상위 프로필: 상위 유닛이 하나뿐인 클리어만 집계하고, 표본이 부족하면 전체로 후퇴.
var mixedNavigationSamples = new List<ClearSample>();
for (var i = 0; i < 12; i++)
{
    mixedNavigationSamples.Add(GodClear($"solo-{i}", 12, clearT0,
        ("DB0H", "초월 [물딜]"), ("AAAA", "")));
    mixedNavigationSamples.Add(GodClear($"multi-{i}", 12, clearT0,
        ("DB0H", "초월 [물딜]"), ("I90H", "초월 [마딜]"), ("BBBB", "")));
}
var navigationStats = ClearBuildStats.FromSamples(mixedNavigationSamples);
Assert(navigationStats.PriorityScore(["DB0H"], ["BBBB"], TopScope.SoloTop) == 0 &&
       navigationStats.PriorityScore(["DB0H"], ["AAAA"], TopScope.SoloTop) == 100,
    "1상위 항법 점수는 상위 1기 클리어만 집계");
Assert(navigationStats.PriorityScore(["DB0H"], ["BBBB"], TopScope.MultiTop) == 100 &&
       navigationStats.PriorityScore(["DB0H"], ["AAAA"], TopScope.MultiTop) == 0,
    "다상위 항법 점수는 상위 2기 이상 클리어만 집계");
Assert(navigationStats.Evidence(["DB0H"], ["AAAA"], TopScope.SoloTop)
           is { Scope: TopScope.SoloTop, SampleCount: 12 },
    "1상위 근거는 1상위 표본 수로 표기");
var thinSoloStats = ClearBuildStats.FromSamples(
    Enumerable.Range(0, 12).Select(i => GodClear($"thin-{i}", 12, clearT0,
        ("DB0H", "초월 [물딜]"), ("I90H", "초월 [마딜]"), ("CCCC", ""))).ToList());
Assert(thinSoloStats.PriorityScore(["DB0H"], ["CCCC"], TopScope.SoloTop) > 0,
    "1상위 표본이 부족하면 전체 프로필로 후퇴");
var thinMultiStats = ClearBuildStats.FromSamples(
    Enumerable.Range(0, 12).Select(i => GodClear($"thin-multi-{i}", 12, clearT0,
        ("DB0H", "초월 [물딜]"), ("DDDD", ""))).ToList());
Assert(thinMultiStats.PriorityScore(["DB0H"], ["DDDD"], TopScope.MultiTop) > 0,
    "다상위 표본이 부족하면 전체 프로필로 후퇴");

// 마딜 상위(상디초월)는 바제스 가능이라도 물딜 방깎 211 파이프라인을 타지 않는다.
// 신+ 실측(92판): 방깎 중앙값 0, 마방깎 소스 1, 스턴 1.4, 보잡·광잡 각 1.
var sanjiGoal = catalog.Unit("rawcode:H90H");
Assert(sanjiGoal.Tier.Contains("[마딜]", StringComparison.Ordinal) &&
       sanjiGoal.OfficialAbilities.Any(ability => ability.Name == "바제스" &&
                                                  ability.DisplayValue == "가능"),
    "상디초월은 바제스 가능 + 마딜 티어(물딜 오판 회귀 방지 전제)");
var sanjiPicks = engine.RecommendNearestCrafts("rawcode:H90H", [], take: 10,
    navigationMode: "AlliedForces.EmergencyCall");
Assert(sanjiPicks.Count > 1 && sanjiPicks[0].Route.GoalUnitId == "rawcode:H90H",
    "긴급소집에서 상디초월 목표 카드를 먼저 표시");
var sanjiSupports = sanjiPicks.Skip(1)
    .Select(item => catalog.Unit(item.Route.GoalUnitId))
    .ToList();
Assert(!sanjiSupports.Any(unit =>
        SupportAbility(unit, "방어력 감소", "발동방어력 감소", "중첩방어력 감소") > 0 &&
        SupportAbility(unit, "마법방어력 감소", "스턴", "이동속도 감소", "발동이동속도 감소",
            "보스 잡기", "광폭화 잡기", "공중이동") <= 0),
    "마딜 상위에 방깎 전용 유닛을 추천하지 않음");
Assert(sanjiSupports.Sum(unit => SupportAbility(unit, "마법방어력 감소")) >= 1,
    "마딜 상위는 마방깎 소스를 최소 한 점 확보");
var sanjiStun = sanjiSupports.Sum(unit => SupportAbility(unit, "스턴"));
Assert(sanjiStun >= 0.9 && sanjiStun <= 1.5001,
    "마딜 상위도 스턴 축은 1.4 목표·1.5 상한 규칙을 따름");
Assert(sanjiSupports.Sum(unit => SupportAbility(unit, "보스 잡기")) >= 1 &&
       sanjiSupports.Sum(unit => SupportAbility(unit, "광폭화 잡기")) >= 1,
    "라인딜 상디는 보잡·광잡을 지원에서 확보");
Assert(sanjiSupports.Any(unit => SupportAbility(unit, "끝딜") > 0),
    "단일 상위(상디)는 끝딜 한 기를 보완(딜 밸런스 규칙)");

var damageMixStats = new InventoryStatsCalculator(catalog).Calculate(
    Inventory("rawcode:J30h", "rawcode:S50h", "rawcode:130h"));
Assert(damageMixStats.FinisherDamageProviders == 1 && damageMixStats.SingleDamageProviders == 1 &&
       Math.Abs(damageMixStats.MagicArmorReduction - 1) < 0.001,
    "단일·끝딜 기수와 마방깎 수치를 패 요약에 집계");

// 취향 조건부 학습: 앵커(이미 짠 유닛)와 같이 쓰인 클리어만 골라 채용률 재집계.
// 앵커(ANCH)는 30판 중 12판(40퍼센트)이라 범용 컷라인(50퍼센트) 아래다.
var archetypeSamples = new List<ClearSample>();
for (var i = 0; i < 12; i++)
    archetypeSamples.Add(GodClear($"arc-a-{i}", 12, clearT0,
        ("GOAL", "초월 [마딜]"), ("ANCH", ""), ("AAAA", "")));
for (var i = 0; i < 18; i++)
    archetypeSamples.Add(GodClear($"arc-b-{i}", 12, clearT0,
        ("GOAL", "초월 [마딜]"), ("BBBB", "")));
var archetypeStats = ClearBuildStats.FromSamples(archetypeSamples);
var anchored = archetypeStats.ResolveProfile(["GOAL"], TopScope.Any, ["ANCH"]);
Assert(anchored is { SampleCount: 12, AnchorRawcodes: { Count: 1 } } &&
       anchored.SupportShare["AAAA"] > 0.99 && !anchored.SupportShare.ContainsKey("BBBB"),
    "앵커 동반 클리어만 골라 채용률을 재집계");
var universalAnchor = archetypeStats.ResolveProfile(["GOAL"], TopScope.Any, ["BBBB"]);
Assert(universalAnchor is { SampleCount: 30 } && universalAnchor.AnchorRawcodes is null,
    "범용(50퍼센트 이상) 유닛은 취향 앵커로 쓰지 않음");
var thinAnchorSamples = archetypeSamples.Take(6)
    .Concat(archetypeSamples.Skip(12)).ToList();
var thinAnchored = ClearBuildStats.FromSamples(thinAnchorSamples)
    .ResolveProfile(["GOAL"], TopScope.Any, ["ANCH"]);
Assert(thinAnchored is not null && thinAnchored.AnchorRawcodes is null,
    "앵커 표본이 12판 미만으로 쪼개지면 무조건부 프로필 유지");

var brokenCountSample = new ClearSample("broken-n", clearT0, "신", 0,
    [new ClearSampleUnit("DB0H", 1, "초월 [물딜]"), new ClearSampleUnit("AAAA", 11, "")]);
Assert(ClearBuildStats.EffectiveUnitCount(brokenCountSample) == 12,
    "unitCount가 0인 기록은 나열된 유닛 수량 합으로 빌드 크기를 보정");
Assert(ClearBuildStats.FromSamples([brokenCountSample]).TotalGodPlusSamples == 1,
    "unitCount가 깨진 기록도 집계에서 버리지 않음");

var bundledStats = ClearBuildStats.Load(
    [Path.Combine(AppContext.BaseDirectory, "Data", "tmo-clear-samples.json")]);
Assert(bundledStats.HasData && bundledStats.TotalGodPlusSamples >= 10000,
    "번들 신+ 클리어 스냅샷 로드");
Assert(bundledStats.GoalProfile(["A90H"]) is { SampleCount: >= 100 },
    "번들 스냅샷에 징베 초월 표본 충분");
Assert(bundledStats.GoalProfile(["H90H"], TopScope.MultiTop)
           is { SampleCount: >= 50, Scope: TopScope.MultiTop },
    "번들 스냅샷에 상디초월 다상위 표본 충분");

// 자동 시작: 첫 희귀함이 재료로 들어가는 학습 상위 중 표본 최다를 추천한다.
var autoStartRare = catalog.AllUnits.FirstOrDefault(unit =>
    unit.Tier.Split('[', 2)[0].Trim() == "희귀함" &&
    AutoStartAdvisor.RecommendGoal(catalog, bundledStats, [unit.Id]) is not null);
Assert(autoStartRare is not null, "희귀함에서 출발하는 자동 시작 추천이 최소 1종 존재");
var autoStartAdvice = AutoStartAdvisor.RecommendGoal(catalog, bundledStats, [autoStartRare!.Id])!;
Assert(AutoStartAdvisor.RequiresUnit(catalog, autoStartAdvice.Goal, autoStartRare.Id) &&
       LearnedSelection.GoalSampleCount(bundledStats, autoStartAdvice.Goal) >=
       ClearBuildStats.MinimumGoalSamples,
    "자동 시작 추천 상위는 그 희귀함을 재료로 쓰고 학습 표본을 충족");
Assert(AutoStartAdvisor.RecommendGoal(catalog, bundledStats, ["luffy_common"]) is null,
    "희귀함이 없으면 자동 시작 추천 없음");

// 희귀패 정리: 재료 수요가 없어도 ① 지원 스탯이 있거나 ② 지원 스탯 전설·히든의
// 재료면 남긴다(마르코 → 에이스 전설 방깎33 사례).
var rareKeepAdvisor = new RareRerollAdvisor(catalog);
var utilityRare = catalog.AllUnits.FirstOrDefault(unit =>
    unit.Tier.Split('[', 2)[0].Trim() == "희귀함" && RareRerollAdvisor.HasUtilityAbility(unit));
var plainRare = catalog.AllUnits.FirstOrDefault(unit =>
    unit.Tier.Split('[', 2)[0].Trim() == "희귀함" &&
    !RareRerollAdvisor.HasUtilityAbility(unit));
Assert(utilityRare is not null && plainRare is not null, "유틸/비유틸 희귀함 표본 존재");
var noopPlan = new Recommendation
{
    Route = new RouteDefinition { Id = "noop", GoalUnitId = "luffy_common", Name = "루피" }
};
var rareKeepAdvice = rareKeepAdvisor.Evaluate(
    Inventory(utilityRare!.Id, plainRare!.Id), [noopPlan]);
Assert(rareKeepAdvice.All(item => item.UnitId != utilityRare.Id) &&
       rareKeepAdvice.Any(item => item.UnitId == plainRare.Id),
    "지원 스탯 있는 희귀패는 정리 제외, 없는 것만 정리 권고");
var aceAdoptGoal = catalog.AllUnits.First(unit =>
    unit.Tier.Split('[', 2)[0].Trim() is "신비함" or "초월" or "불멸" or "영원" or "제한됨" &&
    bundledStats.GoalProfile(unit.Rawcodes)?.SupportShare.GetValueOrDefault("O20h")
        >= RareRerollAdvisor.LegendAdoptionThreshold);
var aceAdoptShare = bundledStats.GoalProfile(aceAdoptGoal.Rawcodes)!.SupportShare;
Assert(rareKeepAdvisor.FeedsAdoptedUtilityLegend("rawcode:220h", aceAdoptShare) &&
       rareKeepAdvisor.Evaluate(Inventory("rawcode:220h"), [noopPlan], aceAdoptGoal, bundledStats)
           .All(item => item.UnitId != "rawcode:220h"),
    "마르코 희귀는 채용되는 에이스 전설(방깎)의 재료라 정리에서 제외");
Assert(rareKeepAdvisor.Evaluate(Inventory("rawcode:220h"), [noopPlan])
           .Any(item => item.UnitId == "rawcode:220h"),
    "목표·클리어 데이터 없이는 기존처럼 재료 수요만 판단");

// 드릴다운: 상위 목표의 남은 조합은 전설급을 먼저 묶고, 그 안에 하위 희귀함을 담는다.
var drillGoal = engine.RecommendNearestCrafts("rawcode:B50h", [], 1)[0];
var (drillLegends, _) = BuildDrilldown.Build(drillGoal);
Assert(drillLegends.Count > 0 &&
       BuildDrilldown.IsLegendTier(drillLegends[0].Step.Tier),
    "카벤딧슈 남은 조합은 전설급 단계를 먼저 리스트업");
Assert(FlattenDrill(drillLegends).Any(node => BuildDrilldown.IsRareTier(node.Step.Tier)) &&
       drillLegends.Any(group => group.Children.Count > 0),
    "전설을 펼치면 그 트리의 하위(희귀함 포함) 단계가 나온다");
var aceEternal = engine.RecommendNearestCrafts("rawcode:950h", [], 1)[0];
var (aceGroups, _) = BuildDrilldown.Build(aceEternal);
Assert(aceGroups.Any(group => group.Step.Tier.Split('[', 2)[0].Trim() is "변화된" or "왜곡됨" &&
                              group.Children.Any(child => child.Step.Tier.Split('[', 2)[0].Trim()
                                  is "전설" or "히든")),
    "변화된·왜곡됨 단계도 드릴다운으로 묶고 그 안에 전설 단계가 나온다");
var rareDrill = engine.RecommendFastRares([], 50)
    .Select(BuildDrilldown.Build)
    .FirstOrDefault(result => result.Legends.Any(group =>
        group.Step.Tier.Split('[', 2)[0].Trim() == "특별함"));
Assert(rareDrill.Legends is { Count: > 0 } specialGroups &&
       specialGroups.Any(group => group.Step.Tier.Split('[', 2)[0].Trim() == "특별함"),
    "희귀함 카드 드릴다운에도 특별함 단계가 나온다");

// 드릴다운은 각 단계의 조합을 그 자리에서 보여준다(유저 보고: 희귀함을 펼쳤는데
// 하위 조합이 비어 있었다 — 같은 유닛이 다른 가지에 먼저 나왔다고 건너뛴 탓).
// 조합 가능한 재료를 가진 단계는 펼쳤을 때 반드시 하위 단계가 나와야 한다.
{
    var tokiDrill = engine.RecommendNearestCrafts("rawcode:780h", [], 1)[0];
    var (tokiGroups, _) = BuildDrilldown.Build(tokiDrill);
    var allNodes = FlattenDrill(tokiGroups).ToList();
    Assert(allNodes.Count > 0, "드릴다운: 토키 목표에 조합 단계가 있다");
    var rare = allNodes.FirstOrDefault(node => node.Step.Name == "키자루");
    Assert(rare is not null, "드릴다운: 희귀함 키자루 단계가 트리에 나온다");
    Assert(rare!.Children.Count > 0,
        "드릴다운: 희귀함을 펼치면 그 조합 단계(특별함 재료)가 나온다");
    Assert(rare.Children.Any(child => child.Step.Name is "헤르메포" or "트라팔가 로우"),
        "드릴다운: 하위 조합에 실제 재료 유닛이 담긴다");
    Assert(rare.Children.All(child => child.Step.Ingredients.Count > 0),
        "드릴다운: 하위 단계도 자기 재료 목록을 갖는다");
}

// 드릴다운은 펼친 항목 자신의 조합법만 보여준다(유저 요청 "선택적 조합법 보기").
// 전설을 펼치면 그 전설의 하위 조합이 안흔함부터, 그 안의 희귀함을 펼치면 다시 그
// 희귀함의 조합이 안흔함부터. 다른 가지가 같은 재료를 먼저 썼다고 빠지면 안 된다.
{
    var scoped = engine.RecommendNearestCrafts("rawcode:780h", [], 1)[0];   // 아마츠키 토키
    var (scopedGroups, _) = BuildDrilldown.Build(scoped);
    var rare = scopedGroups.FirstOrDefault(node => node.Step.Name == "키자루");
    var zoro = scopedGroups.FirstOrDefault(node => node.Step.Name == "조로");
    Assert(rare is not null && zoro is not null,
        "선택적 드릴다운: 목표 바로 아래 단계(키자루·조로 희귀함)가 최상위로 나온다");

    var rareChildren = rare!.Children.Select(child => child.Step.Name).ToList();
    Assert(rareChildren.Contains("헤르메포") && rareChildren.Contains("트라팔가 로우"),
        "선택적 드릴다운: 희귀함을 펼치면 그 희귀함의 재료가 나온다");
    Assert(!rareChildren.Contains("겟코모리아") && !rareChildren.Contains("스모커"),
        "선택적 드릴다운: 다른 가지(조로 희귀함) 재료는 섞이지 않는다");
    Assert(BuildDrilldown.TierOrder(rare.Children[0].Step.Tier) <=
           BuildDrilldown.TierOrder(rare.Children[^1].Step.Tier),
        "선택적 드릴다운: 안흔함부터 오름차순으로 나열");

    // 같은 재료가 두 갈래에 필요하면 각 갈래에서 각각 보인다(중복 제거로 사라지지 않음).
    Assert(zoro!.Children.Any(child => child.Step.Name == "겟코모리아"),
        "선택적 드릴다운: 각 갈래가 자기 재료를 온전히 갖는다");
    var special = rare.Children.First(child => child.Step.Name == "트라팔가 로우");
    Assert(special.Children.Any(child => child.Step.Name is "베포" or "타시기"),
        "선택적 드릴다운: 특별함을 펼치면 그 아래 안흔함 조합까지 이어진다");
}

// 세라핌 기물 추천: 그린블러드 1개 레시피로 편입, 목표별 채용률 최고 세라핌 포함.
// 세라핌 포함은 클리어 프로필이 있어야 작동하므로 학습 엔진으로 검증한다.
var seraphimEngine = new RecommendationEngine(catalog, bundledStats);
var jinbeWithBlood = seraphimEngine.RecommendNearestCrafts("rawcode:A90H",
    [new InventoryEntry { UnitId = "item_greenblood", Count = 1, Confidence = 1 }], 8,
    navigationMode: "AlliedForces.EmergencyCall");
Assert(jinbeWithBlood.Any(rec =>
        rec.Route.GoalUnitId.Equals("rawcode:3A0h", StringComparison.OrdinalIgnoreCase) &&
        rec.RecipeProgress.CompletionRatio < 1),
    "징베는 S-호크 세라핌을 추천하되 그린블러드만으로 100퍼센트가 되지 않음");
var hawkRecipe = catalog.Unit("rawcode:3A0h").Recipe;
Assert(hawkRecipe.ContainsKey("item_greenblood") && hawkRecipe.ContainsKey("mihawk_hidden"),
    "S-호크 재료는 미호크 히든 + 그린블러드");
Assert(seraphimEngine.RecommendNearestCrafts("rawcode:H90H",
        [new InventoryEntry { UnitId = "item_greenblood", Count = 1, Confidence = 1 }], 8,
        navigationMode: "AlliedForces.EmergencyCall")
    .Any(rec => rec.Route.GoalUnitId.Equals("rawcode:1A0h", StringComparison.OrdinalIgnoreCase)),
    "상디는 S-베어 세라핌을 추천");
// 그린블러드는 판당 1회용: 세라핌을 이미 만들었거나 그블을 썼으면 세라핌 추천 중단.
Assert(!seraphimEngine.RecommendNearestCrafts("rawcode:A90H", Inventory("rawcode:0A0h"), 8,
        navigationMode: "AlliedForces.EmergencyCall")
    .Any(rec => catalog.Unit(rec.Route.GoalUnitId).Tier.Split('[', 2)[0].Trim() == "세라핌"),
    "세라핌을 이미 보유하면 다른 세라핌을 추천하지 않음");
Assert(!seraphimEngine.RecommendNearestCrafts("rawcode:A90H", [], 8,
        navigationMode: "AlliedForces.EmergencyCall", suppressSeraphim: true)
    .Any(rec => catalog.Unit(rec.Route.GoalUnitId).Tier.Split('[', 2)[0].Trim() == "세라핌"),
    "그린블러드를 이미 썼으면 세라핌을 추천하지 않음");

// 클리어 정산: 인식 유닛을 티어별로 세고, 흔함 등 정산 외 티어는 제외한다.
var settlement = SettlementReport.Build(catalog,
    Inventory("rawcode:I70h", "rawcode:3A0h", "rawcode:340h", "luffy_common"));
Assert(settlement.Contains("제한됨 1기") && settlement.Contains("세라핌 1기") &&
       settlement.Contains("히든 1기") && settlement.Contains("합계 3기") &&
       settlement.Contains("전 짰습니다") && !settlement.Contains("루피"),
    "정산은 상위·전설급만 티어별로 집계하고 총 전설 환산 한 줄로 요약");
Assert(SettlementReport.LegendEquivalent(catalog, catalog.Unit("rawcode:3A0h")) == 1,
    "S-호크의 전설 환산은 1전(미호크 히든)");

// 자동 시작 단계의 희귀함 순위: 빈 패에서는 재료 적은 순, 재료가 모이면 완성률 순.
var fastRaresEmpty = engine.RecommendFastRares([], 5);
Assert(fastRaresEmpty.Count == 5 && fastRaresEmpty.All(rec =>
        catalog.Unit(rec.Route.GoalUnitId).Tier.Split('[', 2)[0].Trim() == "희귀함"),
    "자동 시작 단계는 희귀함만 순위로 보여줌");
var targetRare = fastRaresEmpty[0];
var targetRareMaterials = targetRare.RecipeProgress.Leaves
    .SelectMany(leaf => Enumerable.Repeat(leaf.UnitId, (int)leaf.RequiredCount))
    .ToArray();
var fastRaresReady = engine.RecommendFastRares(Inventory(targetRareMaterials), 5);
Assert(fastRaresReady.Zip(fastRaresReady.Skip(1)).All(pair =>
           pair.First.RecipeProgress.CompletionRatio >=
           pair.Second.RecipeProgress.CompletionRatio - 1e-9) &&
       fastRaresReady.Single(rec => rec.Route.GoalUnitId == targetRare.Route.GoalUnitId)
           .RecipeProgress.CompletionRatio >= 0.999,
    "완성률 내림차순 정렬 + 재료 모인 희귀함은 100퍼센트");

// 강화 폼 rawcode 별칭: 발라티에 강화 상디(G90H)는 H90H와 같은 유닛으로 통합된다.
Assert(catalog.Unit("rawcode:G90H").Id == "rawcode:H90H" &&
       catalog.Unit("rawcode:G90H").Name.Contains("상디", StringComparison.Ordinal),
    "강화 상디(G90H)를 상디초월(H90H)로 해석");
Assert(RawcodeCodec.TryParse("G90H", out var enhancedSanjiCode) &&
       RawcodeCodec.DynamicUnitId(enhancedSanjiCode) == "rawcode:H90H",
    "메모리 인식도 강화 상디를 대표 코드 id로 매핑");
Assert(catalog.AllUnits.Count(unit =>
        unit.Rawcodes.Contains("H90H", StringComparer.Ordinal)) == 1,
    "별칭 등록으로 유닛 목록에 중복이 생기지 않음");
Assert(bundledStats.GoalProfile(["H90H"], TopScope.MultiTop) is { SampleCount: >= 500 },
    "별칭 통합으로 상디초월 다상위 학습 표본이 대폭 증가");
var sanjiClearEngine = new RecommendationEngine(catalog, bundledStats);
var sanjiClearPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:H90H", [], take: 10,
    navigationMode: "AlliedForces.EmergencyCall");
Assert(sanjiClearPicks.Any(item => item.ClearEvidence is { Scope: TopScope.MultiTop }),
    "긴급소집 상디초월 추천 근거는 다상위 클리어 실측 채용률");

// 실데이터: 에넬 제한을 이미 짠 상디초월은 에넬 동반 클리어 기준으로 재정렬된다.
var sanjiAnchoredPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:H90H",
    Inventory("rawcode:E50h"), take: 10, navigationMode: "AlliedForces.EmergencyCall");
var sanjiAnchoredEvidence = sanjiAnchoredPicks
    .Select(item => item.ClearEvidence)
    .FirstOrDefault(evidence => evidence is not null);
Assert(sanjiAnchoredEvidence is { AnchorLabel: not null } anchoredEvidence &&
       anchoredEvidence.AnchorLabel.Contains("에넬", StringComparison.Ordinal) &&
       anchoredEvidence.SampleCount >= 12 &&
       anchoredEvidence.SampleCount <
       bundledStats.GoalProfile(["H90H"], TopScope.MultiTop)!.SampleCount,
    "에넬 보유 시 상디초월 근거가 에넬 동반 클리어로 좁혀짐");

// 배 전제(유저 보고): 토키=고대의 배, 갓 에넬=해적선 — 배가 패에 없으면 후보 제외.
var sanjiNoShipUnits = sanjiClearPicks.Skip(1)
    .Select(item => catalog.Unit(item.Route.GoalUnitId))
    .ToList();
Assert(!sanjiNoShipUnits.Any(unit => unit.Rawcodes.Any(code => code is "780h" or "E50h")),
    "배가 없으면 토키·갓 에넬을 지원 후보로 노출하지 않음");
var sanjiWithShipPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:H90H",
    Inventory("rawcode:Y50h"), take: 10, navigationMode: "AlliedForces.EmergencyCall");
Assert(sanjiWithShipPicks.Skip(1).Any(item =>
        catalog.Unit(item.Route.GoalUnitId).Rawcodes.Contains("780h", StringComparer.Ordinal)),
    "고대의 배를 보유하면 토키가 후보로 복귀");

// 조합 완료 수동 표시: 카드존을 안 거친 완성 상위를 사용자가 직접 완료 처리한다.
var manualCompleteTracker = new CompletedTopUnitTracker(catalog);
Assert(manualCompleteTracker.ToggleCompleted("rawcode:H90H") &&
       manualCompleteTracker.Contains("rawcode:H90H") &&
       manualCompleteTracker.Apply([]).Any(entry =>
           entry.UnitId == "rawcode:H90H" && entry.Count == 1),
    "완료 토글이 상위를 추천 인벤토리에 주입");
Assert(manualCompleteTracker.ToggleCompleted("rawcode:H90H") &&
       !manualCompleteTracker.Contains("rawcode:H90H"),
    "완료 토글 재클릭으로 해제");
Assert(!manualCompleteTracker.ToggleCompleted("rawcode:130h"),
    "상위 등급이 아닌 유닛은 완료 토글 거부");

// 그린블러드 사용 추적: 보이던 그린블러드가 연속 3회 미검출일 때만 사용됨 처리
// (보유 확정이 스캔마다 흔들릴 수 있어 1회 미검출은 오탐으로 본다).
var greenBloodUsage = new GreenBloodAdvisor.UsageTracker(catalog);
greenBloodUsage.Observe(Inventory("item_greenblood"));
Assert(!greenBloodUsage.Used, "그린블러드 보유 중에는 사용처 안내 유지");
greenBloodUsage.Observe(Inventory("mihawk_hidden"));
greenBloodUsage.Observe(Inventory("mihawk_hidden"));
Assert(!greenBloodUsage.Used, "일시적 미검출(2회까지)로는 사용됨 처리하지 않음");
greenBloodUsage.Observe(Inventory("item_greenblood"));
greenBloodUsage.Observe(Inventory("mihawk_hidden"));
Assert(!greenBloodUsage.Used, "재검출되면 미검출 카운트가 초기화됨");
greenBloodUsage.Observe(Inventory("mihawk_hidden"));
greenBloodUsage.Observe(Inventory("mihawk_hidden"));
Assert(greenBloodUsage.Used, "연속 3회 미검출이면 사용됨으로 전환");
greenBloodUsage.Observe(Inventory("item_greenblood"));
Assert(!greenBloodUsage.Used, "그린블러드를 다시 얻으면 안내 재개");
greenBloodUsage.Toggle();
Assert(greenBloodUsage.Used, "수동 토글로도 사용됨 표시 가능");
Assert(greenBloodUsage.UsedOnUnit, "수동 사용 표시는 유닛 부여로 간주");

// 부여 vs 세라핌 제작 구분: 세라핌이 새로 생겼으면 부여 효과를 합산하지 않는다.
var grantTracker = new GreenBloodAdvisor.UsageTracker(catalog);
grantTracker.Observe(Inventory("item_greenblood"));
grantTracker.Observe(Inventory("mihawk_hidden"));
grantTracker.Observe(Inventory("mihawk_hidden"));
grantTracker.Observe(Inventory("mihawk_hidden"));
Assert(grantTracker is { Used: true, UsedOnUnit: true },
    "세라핌 없이 그린블러드가 사라지면 유닛 부여로 판정");
var seraphimTracker = new GreenBloodAdvisor.UsageTracker(catalog);
seraphimTracker.Observe(Inventory("item_greenblood"));
seraphimTracker.Observe(Inventory("rawcode:Y90h"));
seraphimTracker.Observe(Inventory("rawcode:Y90h"));
seraphimTracker.Observe(Inventory("rawcode:Y90h"));
Assert(seraphimTracker is { Used: true, UsedOnUnit: false },
    "세라핌이 새로 생겼으면 부여 효과를 합산하지 않음");
var greenBloodBuffStats = new InventoryStatsCalculator(catalog)
    .Calculate(Inventory("greenblood_buff"));
Assert(Math.Abs(greenBloodBuffStats.Stun - 0.3) < 0.001 &&
       Math.Abs(greenBloodBuffStats.AttackSpeed - 30) < 0.001,
    "그린블러드 부여 효과(스턴 0.3 환산·공속 30)를 패 수치에 합산");

// 스네이크맨 초월·빅맘 불멸: 마딜 파이프라인 + 다상위 실측 학습 확장.
Assert(catalog.Unit("rawcode:MB0h").Id == "rawcode:E40h" &&
       catalog.Unit("rawcode:E40h").Name.Contains("센고쿠", StringComparison.Ordinal),
    "센고쿠 불멸 강화 폼(MB0h)을 E40h로 통합");
Assert(bundledStats.GoalProfile(["Q40h"], TopScope.MultiTop)
           is { SampleCount: >= 300, Scope: TopScope.MultiTop },
    "번들 스냅샷에 빅맘 불멸 다상위 표본 충분");
Assert(bundledStats.GoalProfile(["2B0H"], TopScope.MultiTop)
           is { SampleCount: >= 50, Scope: TopScope.MultiTop },
    "번들 스냅샷에 스네이크맨 초월 다상위 표본 충분");
var bigMomPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:Q40h", [], take: 10,
    navigationMode: "AlliedForces.EmergencyCall");
var bigMomSupports = bigMomPicks.Skip(1)
    .Select(item => catalog.Unit(item.Route.GoalUnitId)).ToList();
Assert(bigMomPicks[0].Route.GoalUnitId == "rawcode:Q40h" &&
       bigMomSupports.Sum(unit => SupportAbility(unit, "보스 잡기")) >= 1 &&
       bigMomSupports.Sum(unit => SupportAbility(unit, "광폭화 잡기")) >= 1,
    "빅맘(보잡·광잡 없음)은 지원에서 보잡·광잡을 확보");
Assert(!bigMomSupports.Any(unit =>
        SupportAbility(unit, "방어력 감소", "발동방어력 감소", "중첩방어력 감소") > 0 &&
        SupportAbility(unit, "마법방어력 감소", "스턴", "이동속도 감소", "발동이동속도 감소",
            "보스 잡기", "광폭화 잡기", "공중이동") <= 0),
    "빅맘도 마딜 파이프라인이라 방깎 전용 유닛을 추천하지 않음");
var snakemanPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:2B0H", [], take: 10,
    navigationMode: "AlliedForces.EmergencyCall");
Assert(snakemanPicks.Any(item => item.ClearEvidence is { Scope: TopScope.MultiTop }),
    "스네이크맨 추천 근거도 다상위 실측 채용률");

// 학습된 것만 표기: 상위는 표본 12판 이상, 항법은 스코프 전용 표본이 있어야 노출.
Assert(LearnedSelection.GoalSampleCount(bundledStats, catalog.Unit("rawcode:H90H")) >=
       ClearBuildStats.MinimumGoalSamples &&
       LearnedSelection.GoalSampleCount(bundledStats, catalog.Unit("rawcode:380h")) <
       ClearBuildStats.MinimumGoalSamples,
    "상디초월은 학습된 상위, 샌즈(표본 희박)는 미학습으로 분류");
Assert(LearnedSelection.NavigationLearned(bundledStats, catalog.Unit("rawcode:H90H"),
        NavigationProfiles.Find("AlliedForces.EmergencyCall")) &&
       LearnedSelection.NavigationLearned(bundledStats, catalog.Unit("rawcode:H90H"),
           NavigationProfiles.Find("PathOfKings.BountyHunter")),
    "상디초월은 다상위·1상위 항법 모두 학습됨");
Assert(LearnedSelection.NavigationLearned(thinMultiStats, catalog.Unit("yamato_transcendent"),
           NavigationProfiles.Find("PathOfKings.BountyHunter")) &&
       !LearnedSelection.NavigationLearned(thinMultiStats, catalog.Unit("yamato_transcendent"),
           NavigationProfiles.Find("AlliedForces.EmergencyCall")),
    "1상위 표본만 있으면 다상위 항법은 미학습으로 숨김");

// 니카 뱀초: 인게임 rawcode가 루초와 같아(KB0H) 클리어 학습을 공유한다.
Assert(LearnedSelection.GoalSampleCount(bundledStats, catalog.Unit("rawcode:KB0H_")) >=
       ClearBuildStats.MinimumGoalSamples,
    "뱀초 목표가 KB0H 클리어 기록(216판)에 연결됨");
var nikaSlowPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:KB0H_", [], take: 10,
    navigationMode: "AlliedForces.EmergencyCall");
Assert(nikaSlowPicks.Skip(1).Any(unitPick =>
        SupportAbility(catalog.Unit(unitPick.Route.GoalUnitId),
            "이동속도 감소", "발동이동속도 감소") > 0),
    "빈 패 니카는 다수파인 이감 버전으로 시작(이감 지원 포함)");
// 라분(0.9)을 이미 짜면 자체 1.1과 합쳐 2.0 — 노이감(스턴 2.1·상한 2.6) 빌드로
// 전환되어, 기존 상한(1.5)을 넘긴 상태에서도 스턴을 더 채운다.
var nikaStunPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:KB0H_",
    Inventory("rawcode:Q20h"), take: 10, navigationMode: "AlliedForces.EmergencyCall");
Assert(nikaStunPicks.Skip(1).Any(unitPick =>
        SupportAbility(catalog.Unit(unitPick.Route.GoalUnitId), "스턴") > 0),
    "스턴을 쌓은 니카는 노이감 빌드로 전환(상한 1.5 초과 허용)");
// 빌드 방향 강제 선택: 노이감은 빈 패에서도 큰 스턴 페어(0.8+0.9/0.9+0.9)를 짠다.
var nikaNoSlowPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:KB0H_", [], take: 10,
    navigationMode: "AlliedForces.EmergencyCall", buildVariant: "noslow");
var nikaNoSlowStuns = nikaNoSlowPicks.Skip(1)
    .Select(unitPick => SupportAbility(catalog.Unit(unitPick.Route.GoalUnitId), "스턴"))
    .Where(value => value > 0)
    .ToList();
Assert(nikaNoSlowStuns.Sum() >= 1.5 && nikaNoSlowStuns.All(value => value >= 0.7),
    "노이감 강제 선택 시 빈 패에서도 큰 스턴 페어 구성");
var nikaForcedSlowPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:KB0H_",
    Inventory("rawcode:Q20h"), take: 10, navigationMode: "AlliedForces.EmergencyCall",
    buildVariant: "slow");
Assert(nikaForcedSlowPicks.Skip(1).Any(unitPick =>
        SupportAbility(catalog.Unit(unitPick.Route.GoalUnitId),
            "이동속도 감소", "발동이동속도 감소") > 0),
    "이감 버전 강제 선택 시 스턴이 쌓여 있어도 풀이감을 유지");

// 키자루 초월: 다상위 21판 학습 + 역발상(레일리 확정·특포 부족)은 딜 보강으로 반영.
Assert(bundledStats.GoalProfile(["5B0H"], TopScope.MultiTop)
           is { SampleCount: >= 12, Scope: TopScope.MultiTop },
    "번들 스냅샷에 키자루 초월 다상위 표본 충분(실측 학습 발동)");
var kizaruReversePicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:5B0H", [], take: 10,
    navigationMode: "BestHelp.ReverseThinking");
var kizaruReverseSupports = kizaruReversePicks.Skip(1)
    .Select(item => catalog.Unit(item.Route.GoalUnitId)).ToList();
Assert(kizaruReverseSupports.Any(unit => SupportAbility(unit, "단일") > 0) &&
       kizaruReverseSupports.Any(unit => SupportAbility(unit, "끝딜") > 0),
    "역발상 키자루는 특포 부족을 단일·끝딜 보강으로 메움");
// 특공 키자루: 특포 반복 소모(스킬강화) 때문에 특강(필수) 상위와 경합 —
// 핸콕·오뎅·알비다 같은 특포 의존 상위를 추가 상위 후보에서 제외한다.
var kizaruTraitPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:5B0H", [], take: 10,
    navigationMode: "AlliedForces.TraitEngineering");
Assert(!kizaruTraitPicks.Skip(1).Any(item =>
        catalog.Unit(item.Route.GoalUnitId).Rawcodes
            .Any(code => code is "C50h" or "R80h" or "Q80h" or "O80h")),
    "특공 키자루는 특강(필수) 상위를 특포 경합으로 제외");

var kizaruBare = sanjiClearEngine.RecommendNearestCrafts("rawcode:5B0H", [], take: 3,
    navigationMode: "AlliedForces.TraitEngineering");
var kizaruWithRayleigh = sanjiClearEngine.RecommendNearestCrafts("rawcode:5B0H",
    Inventory("rawcode:X50h"), take: 3, navigationMode: "AlliedForces.TraitEngineering");
Assert(kizaruWithRayleigh[0].RecipeProgress.OwnedLeafCount >
       kizaruBare[0].RecipeProgress.OwnedLeafCount,
    "특공 도박으로 레일리(히든)가 뜨면 키자루 완성도에 즉시 반영");

// 배 예약 상호배제(유저 보고): 해적선 1척이면 배 소비 후보는 한 기만 추천된다.
var shipConsumerCodes = new[] { "Q30h", "E50h", "X30h", "L30h", "U30h", "R30h", "P30h" };
var oneShipPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:5B0H",
    Inventory("rawcode:060h"), take: 10, navigationMode: "AlliedForces.TraitEngineering");
var oneShipConsumers = oneShipPicks.Skip(1)
    .Count(item => catalog.Unit(item.Route.GoalUnitId).Rawcodes
        .Any(code => shipConsumerCodes.Contains(code, StringComparer.Ordinal)));
Assert(oneShipConsumers <= 1,
    "해적선 1척이면 배 소비 후보(모비딕·에넬·방주맥심 등)는 최대 1기만 추천");
Assert(oneShipConsumers == 1,
    "보유한 배 1척의 사용처는 추천에 포함됨");
var twoShipPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:5B0H",
    Inventory("rawcode:060h", "rawcode:060h"), take: 10,
    navigationMode: "AlliedForces.TraitEngineering");
Assert(twoShipPicks.Skip(1).Count(item => catalog.Unit(item.Route.GoalUnitId).Rawcodes
           .Any(code => shipConsumerCodes.Contains(code, StringComparer.Ordinal))) >=
       oneShipConsumers,
    "배가 늘면 배 소비 후보도 늘 수 있음");

// 특수함 정리: 추천 빌드에 갱벳지(C10h)가 부족하면 분해 추천, 아니면 유지 안내.
var specialAdvisor = new SpecialDismantleAdvisor(catalog);
var badgeGoal = catalog.Unit("rawcode:I70h"); // 카타쿠리 제한 — 트리에 갱벳지 포함
var badgePicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:I70h",
    Inventory("rawcode:H50h"), take: 3, navigationMode: "AlliedForces.EmergencyCall");
var badgeAdvice = specialAdvisor.Evaluate(Inventory("rawcode:H50h"), badgePicks, badgeGoal,
    bundledStats);
Assert(badgeAdvice.Count == 1 && badgeAdvice[0].Dismantle &&
       badgeAdvice[0].Reason.Contains("갱벳지", StringComparison.Ordinal),
    "갱벳지가 부족한 빌드에서는 특수함 분해 추천");
var keepAdvice = specialAdvisor.Evaluate(Inventory("rawcode:H50h"), [], badgeGoal, bundledStats);
Assert(keepAdvice.Count == 1 && !keepAdvice[0].Dismantle,
    "갱벳지 수요가 없으면 특수함 유지 안내");
Assert(specialAdvisor.Evaluate(Inventory("rawcode:Z90h"), badgePicks, badgeGoal, bundledStats)
           .Count == 0,
    "특수함을 보유하지 않으면 정리 안내 없음");

// 아이템 스펙 합산(맵 w3t 추출): 유닛 인벤토리의 아이템도 패 수치에 반영된다.
var itemStats = new InventoryStatsCalculator(catalog).Calculate(
    Inventory("rawcode:300I", "rawcode:100I", "rawcode:000I", "rawcode:200I"));
Assert(Math.Abs(itemStats.ArmorReduction - 6) < 0.001 &&
       Math.Abs(itemStats.AttackSpeed - 10) < 0.001 &&
       Math.Abs(itemStats.TotalAttackBoost - 14) < 0.001 &&
       Math.Abs(itemStats.HealthRegen - 0.5) < 0.001,
    "아이템 스펙(슈스이 방깎6·헤드셋 공속10·태양신 공증14·깃털 체젠0.5) 합산");

// 스턴 공략 목표 노출: 패 수치 카드가 고정 1.4 대신 공략별 목표·상한을 쓴다.
_ = sanjiClearEngine.RecommendNearestCrafts("yamato_transcendent", [], take: 3,
    navigationMode: "PathOfKings.BountyHunter");
Assert(Math.Abs(sanjiClearEngine.ActiveStunTarget - 1.4) < 0.001 &&
       Math.Abs(sanjiClearEngine.ActiveStunCap - 1.5) < 0.001,
    "기본 공략의 스턴 목표는 1.4·상한 1.5");
_ = sanjiClearEngine.RecommendNearestCrafts("rawcode:KB0H_", [], take: 3,
    navigationMode: "AlliedForces.EmergencyCall", buildVariant: "noslow");
Assert(Math.Abs(sanjiClearEngine.ActiveStunTarget - 2.9) < 0.001 &&
       Math.Abs(sanjiClearEngine.ActiveStunCap - 3.0) < 0.001,
    "노이감 니카의 스턴 목표는 2.9·상한 3.0으로 노출");

// 오로성(신+ 판별 효과) 보정: 나스쥬로=이감 상향, 워큐리=깎기 상향, 새턴=딜 밸런스.
Assert(GoroseiEffects.AdjustSlowTarget(102, GoroseiMode.Nasjuro) == 112 &&
       GoroseiEffects.AdjustSlowTarget(102, GoroseiMode.Warcury) == 102,
    "나스쥬로는 이감 목표를 112로 올림");
Assert(GoroseiEffects.AdjustArmorTarget(211, GoroseiMode.Warcury) == 221 &&
       GoroseiEffects.AdjustArmorTarget(0, GoroseiMode.Warcury) == 0 &&
       GoroseiEffects.AdjustMagicArmorTarget(1, GoroseiMode.Warcury) == 10,
    "워큐리는 방깎 221·마방깎 10을 목표로 함(0 목표는 유지)");
var saturnPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:H90H", [], take: 10,
    navigationMode: "AlliedForces.EmergencyCall", gorosei: GoroseiMode.Saturn);
var saturnSupports = saturnPicks.Skip(1)
    .Select(item => catalog.Unit(item.Route.GoalUnitId)).ToList();
Assert(saturnSupports.Any(unit => SupportAbility(unit, "끝딜") > 0),
    "새턴에서도 단일 상위(상디)는 끝딜을 보완(자체 단일과 합쳐 양쪽 확보)");
var warcuryPicks = sanjiClearEngine.RecommendNearestCrafts("rawcode:H90H", [], take: 10,
    navigationMode: "AlliedForces.EmergencyCall", gorosei: GoroseiMode.Warcury);
var warcuryMagicArmor = warcuryPicks.Skip(1)
    .Select(item => catalog.Unit(item.Route.GoalUnitId))
    .Sum(unit => SupportAbility(unit, "마법방어력 감소"));
// 마방깎 소스 다수(우타·핸콕·후지토라)가 스턴을 겸해 스턴 1.5 상한과 충돌한다.
// 엔진은 상한을 지키며 가능한 만큼 확보하고, 부족분은 스탯 카드(목표 10)로 보여
// 에넬 제한(스턴 없는 마방깍 18) 확보를 유도한다.
Assert(warcuryMagicArmor >= 3,
    "워큐리에서는 스턴 상한 안에서 마방깎 소스를 추가 확보");

// 조합 추정: 목표 직접 재료가 전부 모였다가 연속 2회 스캔에서 전부 사라지면
// 목표가 조합된 것으로 본다(완성 상위는 카드존을 떠나 인식 불가 — 키자루 사례).
var craftTracker = new CompletedTopUnitTracker(catalog);
var kizaruIngredients = catalog.Unit("rawcode:5B0H").Recipe
    .Where(pair => !catalog.Unit(pair.Key).Tier.Equals("자원", StringComparison.OrdinalIgnoreCase) &&
                   !catalog.Unit(pair.Key).Rawcodes
                       .Any(code => code is "GOLD" or "LUMBER" or "POINT" or "RANDOM"))
    .SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value))
    .ToArray();
craftTracker.ObserveGoalCraft("rawcode:5B0H", Inventory(kizaruIngredients));
craftTracker.ObserveGoalCraft("rawcode:5B0H", Inventory());
Assert(!craftTracker.Contains("rawcode:5B0H"), "재료 소실 1회로는 조합 추정 안 함");
craftTracker.ObserveGoalCraft("rawcode:5B0H", Inventory());
Assert(craftTracker.Contains("rawcode:5B0H"),
    "재료가 전부 모였다가 전부 사라지면 목표 조합으로 추정(카드 자동 숨김)");
var partialTracker = new CompletedTopUnitTracker(catalog);
partialTracker.ObserveGoalCraft("rawcode:5B0H", Inventory(kizaruIngredients));
partialTracker.ObserveGoalCraft("rawcode:5B0H", Inventory(kizaruIngredients[0]));
partialTracker.ObserveGoalCraft("rawcode:5B0H", Inventory());
partialTracker.ObserveGoalCraft("rawcode:5B0H", Inventory());
Assert(!partialTracker.Contains("rawcode:5B0H"), "재료가 일부만 사라지면 조합 추정 해제");

// 긴급소집 와일드카드: 추천 빌드에 부족한 특별함 재료부터 3장을 배정한다.
var wildcardAdvice = sanjiClearEngine.RecommendEmergencySummons(sanjiClearPicks, []);
Assert(wildcardAdvice.Count > 0 && wildcardAdvice.Sum(item => item.Count) == 3,
    "긴급소집 와일드카드 3장 배정");
Assert(wildcardAdvice.All(item =>
        catalog.Unit(item.UnitId).Tier.Split('[', 2)[0].Trim() == "특별함"),
    "와일드카드 추천은 전부 특별함 등급");
Assert(wildcardAdvice[0].Reason.Contains("재료", StringComparison.Ordinal),
    "첫 와일드카드는 목표 카드의 부족 재료");

// 목박 보정: 비비 변화는 클리어 동시출현이 높아도(야마토 1상위 실측 51%) 빌드
// 완성 후 남는 패 필러라 실측 점수 대신 수작업 순위로 후퇴한다(유저 검증).
var yamatoOrderPicks = sanjiClearEngine.RecommendNearestCrafts("yamato_transcendent", [],
    take: 8, navigationMode: "PathOfKings.BountyHunter");
var yamatoSupportUnits = yamatoOrderPicks
    .Where(item => !item.Route.GoalUnitId.Equals("yamato_transcendent",
        StringComparison.OrdinalIgnoreCase))
    .Select(item => catalog.Unit(item.Route.GoalUnitId))
    .ToList();
Assert(yamatoSupportUnits.Count > 0 &&
       !yamatoSupportUnits[0].Rawcodes.Contains("W50h", StringComparer.Ordinal),
    "목박 필러(비비 변화)를 야마토 1순위 지원으로 표시하지 않음");
var yamatoSaboIndex = yamatoSupportUnits.FindIndex(unit =>
    unit.Rawcodes.Contains("M30h", StringComparer.Ordinal));
var yamatoViviIndex = yamatoSupportUnits.FindIndex(unit =>
    unit.Rawcodes.Contains("W50h", StringComparer.Ordinal));
Assert(yamatoSaboIndex >= 0 && (yamatoViviIndex < 0 || yamatoSaboIndex < yamatoViviIndex),
    "실측 채용률을 유지한 사보 히든이 목박 보정된 비비 변화보다 앞순위");

// --- 자동조합 계획: 맵에서 추출한 조합 키 + 재료 충족 단계 ---
var combineHotkeys = CombineHotkeyCatalog.Load(
    Path.Combine(AppContext.BaseDirectory, "Data", "tmo-combine-hotkeys.json"));
Assert(combineHotkeys.HasData && combineHotkeys.Entries.Count >= 150,
    "맵 조합 단축키 카탈로그 로드");
Assert(combineHotkeys.FindByResult(["HA0h"]) is { Key: "Z" } &&
       combineHotkeys.FindByResultName("킹 - 전설") is { Key: "Z" },
    "킹 전설 조합 키는 결과 rawcode와 이름 양쪽으로 해석");
Assert(CombineHotkeyCatalog.Load("no-such-file.json").HasData is false,
    "조합 카탈로그가 없으면 조용히 비활성");

var combinePlanner = new AutoCombinePlanner(catalog, combineHotkeys);
var kingIngredients = catalog.Unit("rawcode:HA0h").Recipe.Keys.ToArray();
var kingReadyInventory = kingIngredients
    .SelectMany(id => Enumerable.Repeat(id, catalog.Unit("rawcode:HA0h").Recipe[id]))
    .Where(id => !id.StartsWith("rawcode:LUMBER", StringComparison.OrdinalIgnoreCase) &&
                 id is not ("GOLD" or "LUMBER" or "POINT"))
    .ToArray();
var kingCrafts = new RecommendationEngine(catalog)
    .RecommendNearestCrafts("yamato_transcendent", Inventory(kingReadyInventory), 8);
var kingPlan = combinePlanner.Plan(kingCrafts, Inventory(kingReadyInventory));
Assert(kingPlan.Any(step => step.TargetUnitId.Equals("rawcode:HA0h", StringComparison.OrdinalIgnoreCase) &&
                            step.Key == "Z"),
    "킹 재료가 모이면 자동조합 계획에 킹(Z)이 잡힘");
Assert(combinePlanner.Plan(kingCrafts, []).Count == 0,
    "재료가 없으면 자동조합 계획도 비어 있음");
Assert(combinePlanner.Plan(kingCrafts, Inventory(kingReadyInventory), ["rawcode:HA0h"])
        .All(step => !step.TargetUnitId.Equals("rawcode:HA0h",
            StringComparison.OrdinalIgnoreCase)),
    "이미 만든 유닛은 자동조합 목록에서 빠진다");
var enelFill = enelGoal.RemainingCraftSteps
    .SelectMany(step => step.Ingredients)
    .Select(ingredient => ingredient.UnitId)
    .Where(id => catalog.Unit(id).Rawcodes.All(code => code is not ("060h" or "Y50h")) &&
                 catalog.Unit(id).Tier.Split('[', 2)[0].Trim() is not ("아이템" or "자원"))
    .ToArray();
Assert(combinePlanner.Plan([enelGoal], Inventory(enelFill)).Count == 0,
    "해적선 없는 에넬 목표는 중간 조합을 지금 조합 가능으로 안내하지 않음");
var hotkeyEngine = new RecommendationEngine(catalog, null, combineHotkeys);
var hotkeyPicks = hotkeyEngine.RecommendNearestCrafts("yamato_transcendent", [], take: 3);
var keyedStep = hotkeyPicks.SelectMany(item => item.RemainingCraftSteps)
    .FirstOrDefault(step => step.CombineKey is { Length: > 0 });
Assert(keyedStep is not null &&
       RecommendationPresentation.CraftIngredientLine(keyedStep)
           .Contains("유닛 조합 키: ", StringComparison.Ordinal),
    "조합 키를 아는 단계는 선택할 유닛과 조합 키로 안내");
var jinbeUnit = catalog.Unit("rawcode:A90H");
Assert(jinbeUnit.CombineCommands.SequenceEqual(["바다의협객", "jinbe tr"]),
    "징베 초월 조합 명령어는 바다의협객 / jinbe tr");
Assert(catalog.Unit("rawcode:HA0h").CombineCommands.Count == 0 &&
       catalog.Unit("rawcode:Q80h").CombineCommands.Count == 0,
    "전설·제한됨은 채팅 명령어가 없다");
Assert(catalog.Unit("rawcode:O30h").CombineCommands.SequenceEqual(["봉쿠레조합", "bonkurei"]),
    "봉쿠레 히든 조합 명령어는 봉쿠레조합 / bonkurei");
Assert(catalog.Unit("rawcode:Q30h").CombineCommands.SequenceEqual(["모비딕호조합", "mobydick"]),
    "모비딕호 조합 명령어는 모비딕호조합 / mobydick");
Assert(catalog.RawcodeCatalog.Values.Count(entry => entry.Commands.Count > 0) >= 70,
    "조합 명령어가 있는 유닛은 초월·히든·해적선까지 전부 실림");
var jinbeGoalCard = new RecommendationEngine(catalog, null, combineHotkeys)
    .RecommendNearestCrafts("rawcode:A90H", [], 1)[0];
var jinbeCraftStep = jinbeGoalCard.RemainingCraftSteps.First(step =>
    catalog.Unit(step.UnitId).Rawcodes.Contains("A90H", StringComparer.Ordinal));
Assert(jinbeCraftStep.CombineCommands.SequenceEqual(["바다의협객", "jinbe tr"]),
    "징베 초월 남은 조합에 채팅 명령어를 붙인다");
Assert(jinbeGoalCard.CombineCommands.SequenceEqual(["바다의협객", "jinbe tr"]),
    "추천 첫 화면 카드에 징베 조합 명령어가 있다");
Assert(RecommendationPresentation.OverlayCommandLine(jinbeGoalCard) ==
       "조합 명령어: 바다의협객 / jinbe tr",
    "추천 오버레이 접힌 카드에 조합 명령어를 표시한다");
var jinbeLine = RecommendationPresentation.CraftIngredientLine(jinbeCraftStep);
Assert(jinbeLine.Contains("조합 명령어", StringComparison.Ordinal) &&
       jinbeLine.Contains("바다의협객", StringComparison.Ordinal) &&
       jinbeLine.Contains("jinbe tr", StringComparison.Ordinal),
    "초월 조합 안내에 한글·영문 명령어를 그대로 보여준다");
var jinbeReady = jinbeUnit.Recipe.Keys
    .SelectMany(id => Enumerable.Repeat(id, jinbeUnit.Recipe[id]))
    .Where(id => catalog.Unit(id).Tier.Split('[', 2)[0].Trim() is not "자원")
    .ToArray();
var jinbeReadyCrafts = new RecommendationEngine(catalog, null, combineHotkeys)
    .RecommendNearestCrafts("rawcode:A90H", Inventory(jinbeReady), 1);
Assert(combinePlanner.Plan(jinbeReadyCrafts, Inventory(jinbeReady))
        .Any(step => step.TargetUnitId.Equals("rawcode:A90H", StringComparison.OrdinalIgnoreCase) &&
                     step.Commands.Contains("바다의협객") && step.Commands.Contains("jinbe tr")),
    "징베 재료가 모이면 지금 조합 가능에 채팅 명령어가 잡힌다");

// 자동 업데이트: 최신 릴리스 태그가 현재 버전보다 높을 때만 exe 자산을 고른다.
const string releaseJson = """
    {"tag_name":"v9.9.9","assets":[
      {"name":"readme.txt","browser_download_url":"https://x/readme.txt"},
      {"name":"OrandOverlay.exe","browser_download_url":"https://x/OrandOverlay.exe"}]}
    """;
Assert(UpdateService.ParseLatest(releaseJson, new Version(0, 2, 0))
           is { Tag: "v9.9.9", DownloadUrl: "https://x/OrandOverlay.exe" },
    "새 릴리스가 있으면 exe 자산과 태그를 해석");
Assert(UpdateService.ParseLatest(releaseJson.Replace("v9.9.9", "v0.2.0"),
        new Version(0, 2, 0)) is null,
    "같은 버전이면 업데이트로 판정하지 않음");
Assert(UpdateService.ParseLatest("""{"tag_name":"v9.9.9","assets":[]}""",
        new Version(0, 2, 0)) is null,
    "exe 자산이 없으면 업데이트를 시도하지 않음");

// 릴리스 페이지 302 리다이렉트 기반 확인: API 호출 제한 없이 짧은 주기로 확인한다.
Assert(UpdateService.ParseRedirectLocation(
        "https://github.com/AsmondKR/onepiece-random-defense-overlay/releases/tag/v9.9.9",
        new Version(0, 2, 0)) is { Tag: "v9.9.9" } redirectInfo &&
    redirectInfo.DownloadUrl.EndsWith("/releases/download/v9.9.9/OrandOverlay.exe",
        StringComparison.Ordinal),
    "리다이렉트 태그에서 새 버전과 내려받기 URL을 해석");
Assert(UpdateService.ParseRedirectLocation(
        "https://github.com/AsmondKR/onepiece-random-defense-overlay/releases/tag/v0.2.0",
        new Version(0, 2, 0)) is null,
    "리다이렉트 태그가 같은 버전이면 업데이트 아님");
Assert(UpdateService.ParseRedirectLocation(
        "https://github.com/AsmondKR/onepiece-random-defense-overlay/releases",
        new Version(0, 2, 0)) is null,
    "태그 리다이렉트가 아니면 무시");
Assert(UpdateService.ParseRedirectLocation(null, new Version(0, 2, 0)) is null,
    "리다이렉트 응답이 없으면 무시");

// 해상도별 UI 자동 배율: 2K(논리 1440) 기준 1.0, 더 큰 화면만 비례 확대.
Assert(Math.Abs(UiScale.FromScreen(1440, 1.0) - 1.0) < 0.001,
    "2K 100%는 배율 1.0 (기준 크기 유지)");
Assert(Math.Abs(UiScale.FromScreen(1080, 1.0) - 1.0) < 0.001,
    "FHD는 줄이지 않고 1.0 유지");
Assert(Math.Abs(UiScale.FromScreen(2160, 1.0) - 1.5) < 0.001,
    "4K 100%는 배율 1.5");
Assert(Math.Abs(UiScale.FromScreen(2160, 1.5) - 1.0) < 0.001,
    "4K 150%는 이미 논리 1440이라 그대로 1.0");
Assert(Math.Abs(UiScale.FromScreen(4320, 1.0) - 3.0) < 0.001,
    "8K 100%는 배율 3.0");

// 레거시 설정 마이그레이션(settingsSchemaVersion 기반 1회 실행)
var noVersionResult = LegacySettingsMigration.Run("{\"AutoScanEnabled\":true}");
Assert(noVersionResult.Changed && noVersionResult.Json.Contains("SettingsSchemaVersion"),
    "버전 없는 설정은 마이그레이션 후 SettingsSchemaVersion 추가됨");
Assert(!LegacySettingsMigration.Run("{\"SettingsSchemaVersion\":2}").Changed,
    "현재 스키마 버전 설정은 마이그레이션 건너뜀");
Assert(LegacySettingsMigration.Run("{\"SettingsSchemaVersion\":1}").Changed,
    "v1 설정은 마이그레이션 실행");
var screenSourceMigration = LegacySettingsMigration.Run(
    "{\"SettingsSchemaVersion\":1,\"RecognitionSource\":\"Screen\",\"AutoScanEnabled\":true}");
Assert(screenSourceMigration.Changed &&
       !screenSourceMigration.Json.Contains("RecognitionSource") &&
       screenSourceMigration.Json.Contains("AutoScanEnabled"),
    "RecognitionSource=Screen 레거시 설정이 마이그레이션으로 제거되고 AutoScanEnabled는 보존");

// 라이브 검증(work/verification/live-verification.jsonl)은 사용자의 실전 1판에서만 생성된다.
// 스모크가 자기 픽스처를 되읽는 순환 검증은 금지 — 존재 여부·내용 검증은 시드 AC의 verify_command가 담당한다.
// 여기서는 레코더의 분류·기록 로직만 임시 디렉터리에서 검증한다(사용자 판정 없이는 아무것도 기록하지 않는 계약 포함).
{
    var verifyDir = Path.Combine(Path.GetTempPath(), "orand-live-verify-" + Guid.NewGuid().ToString("N"));
    var recorder = new LiveVerificationRecorder(verifyDir,
        () => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) { Enabled = true };
    static RecognitionResult ReadyResult(params (string Id, int Count)[] units) => new()
    {
        State = RecognitionState.Ready,
        Entries = units.Select(u => new InventoryEntry { UnitId = u.Id, Count = u.Count }).ToList(),
        Diagnostics = new RecognitionDiagnostics { ProfileId = "war3-test", ProcessVersion = "2.0.4.23745" }
    };

    recorder.Observe(ReadyResult(("a", 1)));
    Assert(recorder.Pending is null, "라이브 검증: 첫 관찰은 기준선 — 이벤트 없음");
    recorder.Observe(ReadyResult(("a", 2)));
    Assert(recorder.Pending?.EventTag == LiveVerificationRecorder.EventUnitAdded, "라이브 검증: 총량 증가는 unit_added");
    Assert(recorder.Confirm(true) is not null, "라이브 검증: 일치 판정이 JSONL 경로 반환");
    recorder.Observe(ReadyResult(("a", 1)));
    Assert(recorder.Pending?.EventTag == LiveVerificationRecorder.EventUnitSold, "라이브 검증: 새 유닛 없는 감소는 unit_sold");
    recorder.Confirm(true);
    recorder.Observe(ReadyResult(("b", 1)));
    Assert(recorder.Pending?.EventTag == LiveVerificationRecorder.EventCombineCompleted,
        "라이브 검증: 새 유닛 등장+재료 소실은 combine_completed");
    recorder.Confirm(false);
    recorder.Observe(new RecognitionResult { State = RecognitionState.Waiting, ConfirmsSessionBoundary = true });
    recorder.Observe(ReadyResult(("b", 1)));
    Assert(recorder.Pending?.EventTag == LiveVerificationRecorder.EventSessionReentry,
        "라이브 검증: 세션 경계 후 첫 Ready는 session_reentry");
    recorder.Confirm(true);
    Assert(recorder.Confirm(true) is not null, "라이브 검증: 대기 이벤트 없으면 수시대조로 기록");
    Assert(recorder.ConfirmedRows == 5 && recorder.MismatchCount == 1, "라이브 검증: 확정 5건·불일치 1건 집계");

    var verifyRows = File.ReadAllLines(recorder.LogFilePath)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonDocument.Parse(line).RootElement).ToList();
    Assert(verifyRows.Count == 5, "라이브 검증: JSONL 5행 기록");
    Assert(verifyRows.Select(r => r.GetProperty("event").GetString()).SequenceEqual(
        [LiveVerificationRecorder.EventUnitAdded, LiveVerificationRecorder.EventUnitSold,
         LiveVerificationRecorder.EventCombineCompleted, LiveVerificationRecorder.EventSessionReentry,
         LiveVerificationRecorder.EventSpotCheck]), "라이브 검증: 이벤트 태그 순서 기록");
    Assert(verifyRows.Count(r => !r.GetProperty("match").GetBoolean()) == 1, "라이브 검증: 불일치 1건 기록");
    Assert(Directory.GetFiles(verifyDir, "mismatch-*.json").Length == 1, "라이브 검증: 불일치 스냅샷 1개 저장");

    var idleRecorder = new LiveVerificationRecorder(verifyDir) { Enabled = true };
    Assert(idleRecorder.Confirm(true) is null, "라이브 검증: 관찰 전에는 아무것도 기록하지 않음");
    var disabledRecorder = new LiveVerificationRecorder(verifyDir);
    disabledRecorder.Observe(ReadyResult(("a", 1)));
    disabledRecorder.Observe(ReadyResult(("a", 2)));
    Assert(disabledRecorder.Pending is null, "라이브 검증: 모드 꺼짐이면 이벤트 감지 안 함");
    Directory.Delete(verifyDir, true);
}

// 텔레메트리 레코드: 판 종료 시 서버로 보내는 익명 플레이 기록.
{
    var record = MatchTelemetryRecorder.Build(
        anonId: "11111111-1111-1111-1111-111111111111",
        appVersion: "0.6.0", mapVersion: "2.314", warcraftVersion: "2.0.4.23745",
        goalUnitId: "yamato_transcendent", navigationMode: "PathOfKings.BountyHunter",
        goroseiMode: "Nasjuro", buildVariant: "auto", difficulty: "신",
        finalHand: new List<InventoryEntry>
        {
            new() { UnitId = "luffy_common", Count = 2 },
            new() { UnitId = "rawcode:200h", Count = 1 },
        },
        completedTops: new List<string> { "yamato_transcendent" },
        topRecommendations: new List<string> { "yamato_transcendent", "rawcode:E90H" },
        sessionStartedAt: new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero),
        sessionEndedAt: new DateTimeOffset(2026, 8, 19, 10, 40, 0, TimeSpan.Zero),
        lastObservedUnitCount: 3);
    Assert(record.SchemaVersion == 1 && record.Outcome == "unknown" && record.OutcomeSource == "none",
        "텔레메트리: v1 레코드는 라벨 unknown/none으로 생성");
    Assert(record.Difficulty == "신", "텔레메트리: 난이도를 레코드에 보존");
    Assert(record.RecordId != Guid.Empty.ToString() && record.AnonId.StartsWith("1111"),
        "텔레메트리: recordId 생성·anonId 보존");
    Assert(record.FinalHand.Count == 2 && record.FinalHand[0].Count == 2,
        "텔레메트리: 최종 패 rawcode+수량 보존");
    var telemetryJson = System.Text.Json.JsonSerializer.Serialize(record);
    Assert(telemetryJson.Contains("\"schemaVersion\":1") && telemetryJson.Contains("\"outcome\":\"unknown\""),
        "텔레메트리: camelCase JSON 직렬화");
    Assert(System.Text.Encoding.UTF8.GetByteCount(telemetryJson) < 4096,
        "텔레메트리: 일반 레코드가 4KB 상한 안");
    Assert(!telemetryJson.Contains("nickname") && !telemetryJson.Contains("battletag"),
        "텔레메트리: 개인정보 필드 자체가 없음");
}

// 텔레메트리 업로더: fail-silent 큐. 서버 없이도 게임에 지장이 없어야 한다.
{
    var queueDir = Path.Combine(Path.GetTempPath(), "orand-telemetry-" + Guid.NewGuid().ToString("N"));
    // 닫힌 로컬 포트 → 즉시 연결 실패 → 큐에 남아야 한다.
    var uploader = new TelemetryUploader("http://127.0.0.1:9/v1/records", queueDir);
    var record = MatchTelemetryRecorder.Build(
        "22222222-2222-2222-2222-222222222222", "0.6.0", "2.314", "2.0.4.23745",
        "yamato_transcendent", "PathOfKings.BountyHunter", "None", "auto", "악몽",
        new List<InventoryEntry> { new() { UnitId = "luffy_common", Count = 1 } },
        new List<string>(), new List<string> { "yamato_transcendent" },
        DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow, 1);
    uploader.EnqueueAndFlushAsync(record).GetAwaiter().GetResult();
    Assert(uploader.PendingCount == 1, "텔레메트리 업로더: 전송 실패 시 큐에 보관");
    uploader.FlushPendingAsync().GetAwaiter().GetResult();
    Assert(uploader.PendingCount == 1, "텔레메트리 업로더: 재시도 실패해도 레코드 유지(네트워크 오류)");

    for (var i = 0; i < 55; i++)
        File.WriteAllText(Path.Combine(queueDir, $"{Guid.NewGuid()}.json"), "{}");
    uploader.TrimQueue();
    Assert(Directory.GetFiles(queueDir, "*.json").Length <= 50, "텔레메트리 업로더: 큐 50판 상한");
    Directory.Delete(queueDir, true);
}

// 텔레메트리: 옵트아웃 없이 항상 전송. 익명 ID는 최초 1회 생성.
{
    Assert(typeof(AppSettings).GetProperty("TelemetryEnabled") is null,
        "텔레메트리: 옵트아웃 설정 필드 없음 — 항상 전송");
    var overlayRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var settingsXaml = File.ReadAllText(Path.Combine(overlayRoot, "MainWindow.xaml"));
    Assert(!settingsXaml.Contains("TelemetryCheck") && !settingsXaml.Contains("익명 플레이 통계"),
        "텔레메트리: 설정 창에 보내기 체크박스가 없음");
    Assert(!settingsXaml.Contains("LiveVerify") && !settingsXaml.Contains("라이브 검증"),
        "설정 창에 개발자용 라이브 검증 패널이 없음");
    var freshSettings = new AppSettings();
    Assert(string.IsNullOrEmpty(freshSettings.TelemetryAnonId), "텔레메트리 설정: ID는 보장 시점에 생성");
    var ensured = SettingsStore.EnsureTelemetryAnonId(freshSettings);
    Assert(Guid.TryParse(ensured.TelemetryAnonId, out _), "텔레메트리 설정: 익명 GUID 생성");
    var again = SettingsStore.EnsureTelemetryAnonId(ensured);
    Assert(again.TelemetryAnonId == ensured.TelemetryAnonId, "텔레메트리 설정: 이미 있으면 유지");
}

Assert(TelemetryUploader.DefaultEndpoint.StartsWith("https://") &&
       TelemetryUploader.DefaultEndpoint.EndsWith("/v1/records") &&
       !TelemetryUploader.DefaultEndpoint.Contains("tmo.gg"),
    "텔레메트리: 기본 엔드포인트는 자체 Worker(HTTPS)이며 티모지지가 아님");

// 라이브 통계 스냅샷: 있으면 표기, 없거나 깨졌으면 조용히 무시(fail-silent).
{
    var statsPath = Path.Combine(Path.GetTempPath(), "orand-live-stats-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(statsPath,
        "{\"schemaVersion\":1,\"generatedAt\":\"2026-08-19T13:00:00Z\",\"totalRecords\":41,\"labeledRecords\":12," +
        "\"goals\":{\"yamato_transcendent\":{\"plays\":30,\"labeled\":10,\"clears\":7,\"adherenceMean\":0.8,\"failHeavyUnits\":[]}}," +
        "\"weights\":{}}");
    var live = LiveStats.Load(statsPath);
    Assert(live.TotalRecords == 41, "라이브 통계: 총 판수 파싱");
    Assert(live.TryGetGoal("yamato_transcendent", out var liveGoalStats) && liveGoalStats.Plays == 30,
        "라이브 통계: 목표별 판수");
    Assert(liveGoalStats.ClearRateText == "클리어율 70%", "라이브 통계: 클리어율 표기(확정 라벨 기준)");
    Assert(!live.TryGetGoal("없는목표", out _), "라이브 통계: 미수집 목표는 표기 없음");
    Assert(LiveStats.Load(statsPath + ".missing").TotalRecords == 0, "라이브 통계: 파일 없으면 빈 통계");
    File.Delete(statsPath);
}

// 게이트 통과 가중치: 스냅샷 weights를 읽어 채용률 점수에 ±10% 상한으로 반영.
{
    var statsPath = Path.Combine(Path.GetTempPath(), "orand-live-weights-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(statsPath,
        "{\"schemaVersion\":1,\"generatedAt\":\"2026-08-19T13:00:00Z\",\"totalRecords\":50,\"labeledRecords\":40," +
        "\"goals\":{},\"weights\":{\"bad_unit\":-0.1,\"good_unit\":0.07,\"overflow\":-0.5}}");
    var live = LiveStats.Load(statsPath);
    Assert(Math.Abs(live.WeightFor("bad_unit") + 0.1) < 1e-9, "가중치: 하향 값 파싱");
    Assert(Math.Abs(live.WeightFor("good_unit") - 0.07) < 1e-9, "가중치: 상향 값 파싱");
    Assert(Math.Abs(live.WeightFor("overflow") + 0.1) < 1e-9, "가중치: 스냅샷이 상한을 넘겨도 ±10%로 캡");
    Assert(live.WeightFor("없는유닛") == 0, "가중치: 미등재 유닛은 0");
    Assert(LiveStats.ApplyWeight(40, -0.1) == 36 && LiveStats.ApplyWeight(40, 0.1) == 44,
        "가중치: 점수 반영은 ±10% 곱");
    Assert(LiveStats.ApplyWeight(40, 0) == 40, "가중치: 0이면 점수 불변");
    File.Delete(statsPath);
}

// 패배 판정: 맵이 패배 시 그 플레이어의 모든 유닛을 RemoveUnit 한다(JASS 필터 mTN).
// 따라서 "내 유닛이 있었는데 전멸했고 판은 계속 돌아간다"가 패배 신호다.
{
    var detector = new MatchOutcomeDetector();
    Assert(detector.Outcome == "unknown", "패배 판정: 초기 상태는 unknown");

    detector.Observe(localUnits: 0, foreignUnits: 300);
    Assert(detector.Outcome == "unknown", "패배 판정: 뽑기 전 0개는 패배가 아님");

    detector.Observe(localUnits: 6, foreignUnits: 300);
    detector.Observe(localUnits: 9, foreignUnits: 300);
    Assert(detector.Outcome == "unknown", "패배 판정: 플레이 중에는 unknown");

    // 기본값은 연속 2회 관측을 요구한다(스캔 경합으로 한 틱 비는 경우 대비).
    detector.Observe(localUnits: 0, foreignUnits: 300);
    Assert(detector.Outcome == "unknown", "패배 판정: 전멸 1회 관측만으로는 확정하지 않음");
    detector.Observe(localUnits: 0, foreignUnits: 300);
    Assert(detector.Outcome == "fail", "패배 판정: 유닛 전멸 + 판 진행 중이면 패배");

    detector.Observe(localUnits: 5, foreignUnits: 300);
    Assert(detector.Outcome == "fail", "패배 판정: 한 번 확정되면 세션 내내 유지");

    // 판 자체가 끝난 경우(풀 전체가 사라짐)는 패배로 단정하지 않는다.
    var ended = new MatchOutcomeDetector();
    ended.Observe(localUnits: 8, foreignUnits: 300);
    ended.Observe(localUnits: 0, foreignUnits: 0);
    Assert(ended.Outcome == "unknown", "패배 판정: 풀 전체 소멸(게임 종료·나가기)은 판정 보류");

    // 순간적인 0은 무시한다(스캔 경합·리롤 사이 등).
    // 0이 연속이 아니면(중간에 다시 잡히면) 카운터가 초기화된다.
    var blip = new MatchOutcomeDetector();
    blip.Observe(localUnits: 7, foreignUnits: 300);
    blip.Observe(localUnits: 0, foreignUnits: 300);
    blip.Observe(localUnits: 7, foreignUnits: 300);
    blip.Observe(localUnits: 0, foreignUnits: 300);
    Assert(blip.Outcome == "unknown", "패배 판정: 끊긴 0 관측은 누적되지 않음");
    blip.Observe(localUnits: 0, foreignUnits: 300);
    Assert(blip.Outcome == "fail" && blip.OutcomeSource == "unitWipe",
        "패배 판정: 연속 관측으로 확정하고 근거를 unitWipe로 표기");

    ended.Reset();
    Assert(ended.Outcome == "unknown", "패배 판정: 세션 경계에서 초기화");
}

// 맵 상태 파싱: 런타임에 조합된 문자열만 신호다. war3map.j 원문(마커 뒤 따옴표)과
// 고정 문구는 숫자가 붙지 않아 걸러진다.
{
    static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);
    Assert(MapStateReader.ScanBuffer(Utf8("현재 라운드|r : 7")).MaxRound == 7, "맵 상태: 라운드 표기 A");
    Assert(MapStateReader.ScanBuffer(Utf8("현재 라운드 : |r65|r")).MaxRound == 65, "맵 상태: 라운드 표기 B");
    Assert(MapStateReader.ScanBuffer(Utf8("현재 라운드|r : 8 현재 라운드 : |r41")).MaxRound == 41,
        "맵 상태: 여러 사본 중 최댓값");
    Assert(MapStateReader.ScanBuffer(Utf8("현재 라운드|r : \"+I2S(OG)")).MaxRound == 0,
        "맵 상태: 스크립트 원문은 숫자가 없어 무시");
    Assert(MapStateReader.ScanBuffer(Utf8("현재 라운드|r : 999")).MaxRound == 0, "맵 상태: 범위 밖 라운드는 버린다");
    Assert(MapStateReader.ScanBuffer([]).MaxRound == 0, "맵 상태: 빈 버퍼");

    var settled = MapStateReader.ScanBuffer(Utf8("마지막 라운드 유닛 점수 : |cffffd7004250점|r"));
    Assert(settled.SettlementCopies == 1, "맵 상태: 정산 문자열 1건 인식");
    var two = MapStateReader.ScanBuffer(Utf8(
        "마지막 라운드 유닛 점수 : |cffffd700100점 마지막 라운드 유닛 점수 : |cffffd7000점|r"));
    Assert(two.SettlementCopies == 2, "맵 상태: 정산 사본 수를 센다");
    Assert(MapStateReader.ScanBuffer(Utf8("마지막 라운드 유닛 점수 : |cffffd700\"+I2S(aQN)+\"점"))
        .SettlementCopies == 0, "맵 상태: 정산도 스크립트 원문은 무시");
    Assert(MapStateReader.ScanBuffer(Utf8("마지막 라운드 유닛 점수 : |cffffd700450골드"))
        .SettlementCopies == 0, "맵 상태: 숫자 뒤 '점'이 없으면 정산이 아니다");
    Assert(MapStateReader.ScanBuffer(Utf8("v|cff00bfff2.314[R]|r |cffffd700신|r ")).Difficulty == "신",
        "맵 상태: 멀티보드 조합 난이도 신");
    Assert(MapStateReader.ScanBuffer(Utf8("◎ 난이도 : |c00cc3337악몽|r / ◎ 모드 : 일반")).Difficulty == "악몽",
        "맵 상태: 정산 줄 조합 난이도 악몽");
    Assert(MapStateReader.ScanBuffer(Utf8("|cffffd700신|r")).Difficulty == "unknown",
        "맵 상태: 스크립트 원문 색코드 이름은 난이도가 아니다");
    Assert(MapStateReader.ScanBuffer(Utf8("난이도 : \"+vN+\"")).Difficulty == "unknown",
        "맵 상태: 스크립트 원문 난이도 줄은 무시");
}

// 클리어 판정: 맵의 65라운드 정산("마지막 라운드 유닛 점수")이 곧 클리어 판정이다.
// 정산 문자열이 세션 기준선보다 늘고, 그 뒤에도 내 유닛이 살아 있어야 클리어 —
// 65라운드 도달만으로는 아니다(그 시점 보스·유닛 카운트 실패가 남아 있다).
{
    var t0 = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    var run = new MatchOutcomeDetector();
    run.ObserveRound(1);
    run.ObserveSettlement(0, t0);
    run.Observe(8, 300, t0);
    run.ObserveRound(65);
    run.Observe(8, 300, t0.AddMinutes(30));
    Assert(run.Outcome == "unknown", "클리어 판정: 65라운드 도달만으로는 클리어가 아니다");
    run.ObserveSettlement(4, t0.AddMinutes(31));
    Assert(run.Outcome == "unknown", "클리어 판정: 정산 직후에는 아직 보류(탈락 처리 대비)");
    run.Observe(8, 300, t0.AddMinutes(31).AddSeconds(10));
    run.Observe(8, 300, t0.AddMinutes(31).AddSeconds(40));
    Assert(run.Outcome == "clear", "클리어 판정: 정산 후 생존 확인되면 클리어");
    Assert(run.OutcomeSource == "mapSettlement", "클리어 판정: 근거는 mapSettlement");

    // 65라운드 정산 순간 보스 실패/카운트 초과로 탈락하는 경우 — 정산 직후 전멸이 온다.
    var bossFail = new MatchOutcomeDetector();
    bossFail.ObserveRound(2);
    bossFail.ObserveSettlement(0, t0);
    bossFail.Observe(8, 300, t0);
    bossFail.ObserveRound(65);
    bossFail.ObserveSettlement(3, t0.AddMinutes(30));
    bossFail.Observe(0, 300, t0.AddMinutes(30).AddSeconds(4));
    bossFail.Observe(0, 300, t0.AddMinutes(30).AddSeconds(8));
    Assert(bossFail.Outcome == "fail", "클리어 판정: 정산이 떠도 내가 전멸하면 패배");

    // 같은 프로세스의 이전 판 정산 사본이 남아 있으면 기준선으로 흡수한다.
    var stale = new MatchOutcomeDetector();
    stale.ObserveRound(65);
    stale.ObserveSettlement(8, t0);
    stale.Observe(8, 300, t0);
    stale.ObserveSettlement(8, t0.AddMinutes(30));
    stale.Observe(8, 300, t0.AddMinutes(31));
    Assert(stale.Outcome == "unknown", "클리어 판정: 잔여 정산 사본은 새 정산이 아니다");
    Assert(stale.OutcomeSource == "none", "클리어 판정: 보류 상태의 근거는 none");

    // 잔여 사본이 해제되어 수가 줄면 기준선도 내려가, 이후 진짜 정산을 잡는다.
    var ratchet = new MatchOutcomeDetector();
    ratchet.ObserveRound(3);
    ratchet.ObserveSettlement(8, t0);
    ratchet.Observe(8, 300, t0);
    ratchet.ObserveSettlement(0, t0.AddMinutes(5));
    ratchet.ObserveRound(65);
    ratchet.ObserveSettlement(4, t0.AddMinutes(40));
    ratchet.Observe(8, 300, t0.AddMinutes(40).AddSeconds(10));
    ratchet.Observe(8, 300, t0.AddMinutes(40).AddSeconds(40));
    Assert(ratchet.Outcome == "clear", "클리어 판정: 기준선이 내려간 뒤의 새 정산을 인식");

    // 정산 없이 라운드만 진행되면(중도 종료 등) 판정하지 않는다.
    var noSettle = new MatchOutcomeDetector();
    noSettle.ObserveRound(2);
    noSettle.ObserveSettlement(0, t0);
    noSettle.Observe(8, 300, t0);
    noSettle.ObserveRound(65);
    noSettle.Observe(8, 300, t0.AddMinutes(40));
    Assert(noSettle.Outcome == "unknown", "클리어 판정: 정산 문자열 없이는 클리어가 아니다");

    // 패배가 먼저 확정되면 이후 정산이 떠도 뒤집히지 않는다(남의 정산일 뿐이다).
    var lostFirst = new MatchOutcomeDetector();
    lostFirst.ObserveRound(10);
    lostFirst.ObserveSettlement(0, t0);
    lostFirst.Observe(8, 300, t0);
    lostFirst.Observe(0, 300, t0.AddMinutes(10));
    lostFirst.Observe(0, 300, t0.AddMinutes(10).AddSeconds(5));
    lostFirst.ObserveRound(65);
    lostFirst.ObserveSettlement(4, t0.AddMinutes(30));
    lostFirst.Observe(5, 300, t0.AddMinutes(31));
    Assert(lostFirst.Outcome == "fail", "클리어 판정: 내 패배 후 남의 정산은 클리어가 아니다");

    var reset = new MatchOutcomeDetector();
    reset.ObserveRound(2);
    reset.ObserveSettlement(0, t0);
    reset.ObserveRound(65);
    reset.ObserveSettlement(4, t0.AddMinutes(30));
    reset.Observe(8, 300, t0.AddMinutes(30).AddSeconds(10));
    reset.Observe(8, 300, t0.AddMinutes(31));
    reset.Reset();
    reset.Observe(8, 300, t0.AddMinutes(32));
    Assert(reset.Outcome == "unknown", "클리어 판정: 세션 경계에서 클리어 상태도 초기화");
}

// 텔레메트리 버퍼: 전멸·세션 종료로 패가 비어도 마지막 패를 남겨 클리어/패배를 보낸다.
// 예전 경로는 인벤토리를 먼저 지운 뒤 보내서, 완성 상위가 없으면 0건이 되었다.
{
    var t0 = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    static TelemetryRecord? Emit(MatchTelemetryBuffer buffer, DateTimeOffset ended,
        string outcome, string source) =>
        buffer.TryEmit(
            "33333333-3333-3333-3333-333333333333", "0.6.7", "2.314", "2.0.4.23745",
            "yamato_transcendent", "PathOfKings.BountyHunter", "None", "auto", "신",
            ended, outcome, source);

    Assert(Emit(new MatchTelemetryBuffer(), t0, "fail", "unitWipe") is null,
        "텔레메트리 버퍼: 패를 한 번도 못 본 빈 판은 보내지 않음");

    var failDetector = new MatchOutcomeDetector();
    var failBuffer = new MatchTelemetryBuffer();
    var livingHand = new List<InventoryEntry>
    {
        new() { UnitId = "luffy_common", Count = 4 },
        new() { UnitId = "rawcode:200h", Count = 2 },
    };
    failBuffer.Capture(livingHand, ["yamato_transcendent"], ["yamato_transcendent"], t0, 6);
    failDetector.Observe(6, 300, t0);
    // MainWindow는 Waiting에서 _automatic.Clear() 한다. 빈 관측이 스냅샷을 덮으면 안 된다.
    failBuffer.Capture([], [], [], t0.AddSeconds(1), 0);
    failDetector.Observe(0, 300, t0.AddSeconds(1));
    failDetector.Observe(0, 300, t0.AddSeconds(2));
    Assert(failDetector.Outcome == "fail" && failDetector.OutcomeSource == "unitWipe",
        "텔레메트리 버퍼: 전멸 연속 관측은 패배");
    var failRecord = Emit(failBuffer, t0.AddSeconds(2), failDetector.Outcome, failDetector.OutcomeSource);
    Assert(failRecord is not null, "텔레메트리 버퍼: 패배 확정 시 레코드를 만든다");
    Assert(failRecord!.Outcome == "fail" && failRecord.OutcomeSource == "unitWipe",
        "텔레메트리 버퍼: 패배 라벨·근거(unitWipe)를 붙인다");
    Assert(failRecord.FinalHand.Count == 2 && failRecord.FinalHand.Sum(x => x.Count) == 6,
        "텔레메트리 버퍼: 전멸 뒤에도 마지막 패를 보낸다");
    Assert(failRecord.CompletedTops.SequenceEqual(["yamato_transcendent"]) &&
           failRecord.LastObservedUnitCount == 6,
        "텔레메트리 버퍼: 완성 상위·마지막 유닛 수는 전멸 전 값을 유지");
    Assert(Emit(failBuffer, t0.AddSeconds(10), "unknown", "none") is null,
        "텔레메트리 버퍼: 패배를 보낸 뒤 세션 종료로 한 판을 두 번 보내지 않음");

    var clearDetector = new MatchOutcomeDetector();
    var clearBuffer = new MatchTelemetryBuffer();
    var clearHand = new List<InventoryEntry> { new() { UnitId = "rawcode:E90H", Count = 8 } };
    clearBuffer.Capture(clearHand, ["yamato_transcendent"], ["yamato_transcendent"], t0, 8);
    clearDetector.ObserveRound(1);
    clearDetector.ObserveSettlement(0, t0);
    clearDetector.Observe(8, 300, t0);
    clearDetector.ObserveRound(65);
    clearDetector.ObserveSettlement(4, t0.AddMinutes(31));
    clearDetector.Observe(8, 300, t0.AddMinutes(31).AddSeconds(10));
    clearDetector.Observe(8, 300, t0.AddMinutes(31).AddSeconds(40));
    Assert(clearDetector.Outcome == "clear" && clearDetector.OutcomeSource == "mapSettlement",
        "텔레메트리 버퍼: 정산 후 생존 확인은 클리어");
    var clearRecord = Emit(clearBuffer, t0.AddMinutes(31).AddSeconds(40),
        clearDetector.Outcome, clearDetector.OutcomeSource);
    Assert(clearRecord is not null && clearRecord.Outcome == "clear" &&
           clearRecord.OutcomeSource == "mapSettlement",
        "텔레메트리 버퍼: 클리어 확정 시 레코드를 만든다");
    Assert(clearRecord!.FinalHand.Count == 1 && clearRecord.FinalHand[0].Count == 8,
        "텔레메트리 버퍼: 클리어는 마지막 생존 패를 보낸다");

    var midBuffer = new MatchTelemetryBuffer();
    midBuffer.Capture(livingHand, [], ["yamato_transcendent"], t0, 6);
    var unknownRecord = Emit(midBuffer, t0.AddMinutes(5), "unknown", "none");
    Assert(unknownRecord is not null && unknownRecord.Outcome == "unknown" &&
           unknownRecord.FinalHand.Sum(x => x.Count) == 6,
        "텔레메트리 버퍼: 판정 전에 세션이 끝나도 마지막 패는 보낸다");

    failBuffer.Reset();
    failBuffer.Capture(clearHand, ["yamato_transcendent"], ["yamato_transcendent"], t0.AddHours(1), 8);
    var nextRecord = Emit(failBuffer, t0.AddHours(1).AddMinutes(40), "clear", "mapSettlement");
    Assert(nextRecord is not null && nextRecord.Outcome == "clear" &&
           nextRecord.FinalHand[0].UnitId == "rawcode:E90H",
        "텔레메트리 버퍼: Reset 후 다음 판은 다시 보낼 수 있다");
}

// 업데이트 시점 정책: 유저 클라이언트는 판이 끝난 뒤에 교체한다(재시작이 판을 끊으므로).
// 개발 PC는 즉시 교체해야 검증이 빠르므로 예외.
{
    Assert(UpdatePolicy.ShouldInstallNow(liveSessionActive: false, developerMachine: false),
        "업데이트 정책: 게임 중이 아니면 바로 교체");
    Assert(!UpdatePolicy.ShouldInstallNow(liveSessionActive: true, developerMachine: false),
        "업데이트 정책: 유저 클라이언트는 게임 중 교체하지 않음");
    Assert(UpdatePolicy.ShouldInstallNow(liveSessionActive: true, developerMachine: true),
        "업데이트 정책: 개발 PC는 게임 중에도 즉시 교체");
}

// 드릴다운 재현 진단: 특정 패·목표로 조합 트리와 남은 단계를 그대로 출력한다.
if (Environment.GetEnvironmentVariable("ORAND_DIAG") == "drill")
{
    // 패는 ORAND_DIAG_HAND에 "rawcode:Y00h*1,rawcode:I10h*2" 형식으로 넘긴다.
    // 목표는 ORAND_DIAG_GOAL(기본 아마츠키 토키). 유저가 본 화면을 그대로 재현하기 위한 도구.
    var handSpec = Environment.GetEnvironmentVariable("ORAND_DIAG_HAND") ?? "rawcode:Y00h*1";
    var diagInv = handSpec.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('*', 2))
        .Select(parts => new InventoryEntry
        {
            UnitId = parts[0].Trim(),
            Count = parts.Length > 1 && int.TryParse(parts[1], out var count) ? count : 1
        })
        .ToList();
    var diagGoal = Environment.GetEnvironmentVariable("ORAND_DIAG_GOAL") ?? "rawcode:780h";
    var diagRec = engine.RecommendNearestCrafts(diagGoal, diagInv, 1)[0];
    Console.WriteLine($"[진단] 목표 {diagRec.Route.Name} 완성률 {diagRec.RecipeProgress.CompletionRatio:P0}");
    foreach (var step in diagRec.RemainingCraftSteps)
        Console.WriteLine($"[진단] 단계 {step.Name}[{step.Tier}] 필요{step.RequiredCount} 보유{step.OwnedCount} " +
                          $"부족{step.MissingCount} 재료=[{string.Join(",", step.Ingredients.Select(x => x.Name))}]");
    void Dump(RecipeTreeNode node, int depth)
    {
        Console.WriteLine($"[트리] {new string(' ', depth * 2)}{node.Name}[{node.Tier}] 필요{node.RequiredCount} 보유{node.OwnedCount}");
        foreach (var child in node.Children) Dump(child, depth + 1);
    }
    if (diagRec.RecipeTree is not null) Dump(diagRec.RecipeTree, 0);
    return;
}

Console.WriteLine("PASS: 추천/메모리 연동 스모크 테스트 419/419");
return;

static ClearSample GodClear(string id, int unitCount, DateTimeOffset at,
    params (string Code, string Grade)[] units) =>
    new(id, at, "신", unitCount,
        units.Select(unit => new ClearSampleUnit(unit.Code, 1, unit.Grade)).ToList());

static List<InventoryEntry> Inventory(params string[] ids) => ids
    .Select(id => new InventoryEntry { UnitId = id, Count = 1, IsManual = true })
    .ToList();

static IEnumerable<BuildDrilldown.DrillNode> FlattenDrill(
    IEnumerable<BuildDrilldown.DrillNode> nodes) =>
    nodes.SelectMany(node => new[] { node }.Concat(FlattenDrill(node.Children)));

static double SupportAbility(UnitDefinition unit, params string[] abilityNames) =>
    unit.OfficialAbilities
        .Where(ability => abilityNames.Contains(ability.Name, StringComparer.Ordinal))
        .Sum(ability => ability.DisplayValue == "가능"
            ? 1
            : double.TryParse(ability.DisplayValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? Math.Abs(value)
                : 0);

static double AbilityValue(CompositionUnitDetail unit, string abilityName)
{
    var display = unit.Abilities.FirstOrDefault(ability => ability.Name == abilityName)?.DisplayValue;
    return double.TryParse(display, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var value) ? Math.Abs(value) : 0;
}

static UnitDefinition TestUnit(string id, string name, Dictionary<string, int>? recipe = null,
    string tier = "일반", List<string>? rawcodes = null) => new()
{
    Id = id,
    Name = name,
    Tier = tier,
    Recipe = recipe ?? [],
    Rawcodes = rawcodes ?? []
};

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("OK: " + name);
}

static MemoryProfile ValidMemoryProfile(bool enabled, bool verified, double matchRatio = 0.6,
    string? sha256 = null) => new()
{
    ProfileSchemaVersion = 1,
    ProfileId = "test-profile",
    ProfileRevision = 1,
    FileVersion = "2.0.4.23745",
    ModuleName = "Warcraft III.exe",
    Enabled = enabled,
    Verified = verified,
    Sha256 = sha256 ?? new string('A', 64),
    LocatorKind = MemoryLocatorKind.ModuleOffset,
    ModuleOffset = 0x1234,
    PointerOffsets = [0],
    CountOffset = 0x10,
    EntriesPointerOffset = 0x18,
    EntryStride = 8,
    EntryPointerOffset = 0,
    EntriesContainPointers = true,
    OwnerOffset = 0x20,
    RawcodeOffset = 0x24,
    LocalPlayerSlot = 0,
    MaximumUnits = 5000,
    MinimumCatalogMatchRatio = matchRatio
};
