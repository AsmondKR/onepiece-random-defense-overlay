from pathlib import Path
import re
from textwrap import dedent


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected one match, found {count}")
    return text.replace(old, new, 1)


def regex_once(text: str, pattern: str, replacement: str, label: str) -> str:
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise SystemExit(f"{label}: expected one match, found {count}")
    return updated


engine_path = Path("RecommendationEngine.cs")
engine = engine_path.read_text(encoding="utf-8")
engine = replace_once(
    engine,
    "        results = RecommendationResultPolicy.Limit(results, take);",
    "        results = LimitRecommendationsPreservingStrategy(\n"
    "            results, take, goal, counts, strategy, showGoal, recipeLegendaryIds);",
    "strategy-aware cap call",
)

method_anchor = "    private ClearEvidence? BuildClearEvidence(IReadOnlyList<string> candidateRawcodes)\n"
method = dedent(
    '''
    /// <summary>
    /// 세라핌처럼 후삽입되는 후보가 있어도 take 제한을 지키되, 현재 전략의 필수
    /// 역할(스턴·이감·깎기·단일·끝딜 등)을 담당하는 후보를 단순히 꼬리에서 자르지 않는다.
    /// 같은 역할 충족도를 유지하는 후보 중 채용률이 가장 낮은 항목부터 제거한다.
    /// </summary>
    private List<Recommendation> LimitRecommendationsPreservingStrategy(
        List<Recommendation> results,
        int take,
        UnitDefinition goal,
        IReadOnlyDictionary<string, int> inventory,
        GoalStrategyProfile? strategy,
        bool showGoal,
        IReadOnlyCollection<string> protectedUnitIds)
    {
        var limit = Math.Max(1, take);
        if (results.Count <= limit) return results;
        if (strategy is null) return RecommendationResultPolicy.Limit(results, limit);

        while (results.Count > limit)
        {
            var fullMetrics = ProjectedMetrics(results, excludedIndex: -1);
            var removable = Enumerable.Range(0, results.Count)
                .Select(index =>
                {
                    var recommendation = results[index];
                    var unit = catalog.Unit(recommendation.Route.GoalUnitId);
                    var without = ProjectedMetrics(results, index);
                    return new
                    {
                        Index = index,
                        Unit = unit,
                        CoverageLoss = StrategyCoverageLoss(fullMetrics, without, strategy.Value),
                        CommunityPriority = CommunityPriorityScore(goal, unit),
                        IsSeraphim = BaseTier(unit.Tier) == "세라핌"
                    };
                })
                .Where(item => !item.Unit.Id.Equals(goal.Id, StringComparison.OrdinalIgnoreCase))
                .Where(item => !protectedUnitIds.Contains(item.Unit.Id,
                    StringComparer.OrdinalIgnoreCase))
                .Where(item => !IsCommunityCore(goal, item.Unit))
                .OrderBy(item => item.CoverageLoss)
                .ThenBy(item => item.CommunityPriority)
                // 같은 손실·채용률이면 세라핌보다 일반 후보를 먼저 정리한다.
                .ThenBy(item => item.IsSeraphim ? 1 : 0)
                .ThenByDescending(item => item.Index)
                .ToList();

            if (removable.Count == 0)
                return RecommendationResultPolicy.Limit(results, limit);
            results.RemoveAt(removable[0].Index);
        }
        return results;

        StrategyMetrics ProjectedMetrics(IReadOnlyList<Recommendation> items, int excludedIndex)
        {
            var projected = AggregateStrategyMetrics(inventory);
            if (showGoal) projected += StrategyMetricsFor(goal);
            for (var index = 0; index < items.Count; index++)
            {
                if (index == excludedIndex) continue;
                var unitId = items[index].Route.GoalUnitId;
                if (unitId.Equals(goal.Id, StringComparison.OrdinalIgnoreCase)) continue;
                projected += StrategyMetricsFor(catalog.Unit(unitId));
            }
            return projected;
        }
    }

    private static double StrategyCoverageLoss(StrategyMetrics before,
        StrategyMetrics after, GoalStrategyProfile strategy) =>
        Loss(before.Slow, after.Slow, strategy.SlowTarget) +
        Loss(before.Stun, after.Stun, strategy.StunTarget) +
        Loss(before.ArmorReduction, after.ArmorReduction, strategy.ArmorReductionTarget) +
        Loss(before.ArmorBreak, after.ArmorBreak, strategy.ArmorBreakTarget) +
        Loss(before.AirMovement, after.AirMovement, strategy.AirMovementTarget) +
        Loss(before.BossControl, after.BossControl, strategy.BossControlTarget) +
        Loss(before.BerserkBossControl, after.BerserkBossControl,
            strategy.BerserkBossControlTarget) +
        Loss(before.MagicArmorReduction, after.MagicArmorReduction,
            strategy.MagicArmorReductionTarget) +
        Loss(before.SingleDamage, after.SingleDamage, strategy.SingleDamageTarget) +
        Loss(before.FinisherDamage, after.FinisherDamage, strategy.FinisherDamageTarget);

    private static double Loss(double before, double after, double target)
    {
        if (target <= 0) return 0;
        var beforeCovered = Math.Min(Math.Max(0, before), target);
        var afterCovered = Math.Min(Math.Max(0, after), target);
        return Math.Max(0, beforeCovered - afterCovered) / target;
    }

    ''')
method = ''.join("    " + line if line.strip() else line for line in method.splitlines(keepends=True))
engine = replace_once(engine, method_anchor, method + method_anchor, "strategy cap method")
# Clean the blank space left where the extracted policy tables used to be.
engine = engine.replace("    private const double MagicArmorSourceTarget = 1;\n\n\n\n\n\n",
                        "    private const double MagicArmorSourceTarget = 1;\n\n")
engine_path.write_text(engine, encoding="utf-8")

main_path = Path("MainWindow.xaml.cs")
main = main_path.read_text(encoding="utf-8")
main = replace_once(
    main,
    '''        var buildVariant = (BuildVariantCombo.SelectedItem as BuildVariant)?.Id
                           ?? BuildVariants.AutoId;
        var recommendations = _engine.RecommendNearestCrafts(goal.Id, recommendationInventory,
            navigationMode: navigation.Id, gorosei: gorosei, buildVariant: buildVariant,
            suppressSeraphim: _greenBloodUsage.Used);''',
    '''        // 니카 이감/노이감은 별도 토글 없이 현재 패의 스턴을 기준으로 자동 판정한다.
        var recommendations = _engine.RecommendNearestCrafts(goal.Id, recommendationInventory,
            navigationMode: navigation.Id, gorosei: gorosei, buildVariant: BuildVariants.AutoId,
            suppressSeraphim: _greenBloodUsage.Used);''',
    "automatic build variant",
)
main = regex_once(
    main,
    r"    // 같은 상위라도 빌드 방향이 갈리는 유닛\(니카 이감/노이감\)만 선택 UI를 노출한다\.\n"
    r"    private void RepopulateBuildVariants\(\)\n"
    r"    \{\n.*?\n    \}\n\n"
    r"    private void BuildVariantCombo_OnSelectionChanged",
    dedent(
        '''
        // 니카 이감/노이감은 추천 엔진이 패 기준으로 자동 판정한다. 사용자 토글은 노출하지 않는다.
        private void RepopulateBuildVariants()
        {
            _updatingSelections = true;
            try
            {
                BuildVariantLabel.Visibility = Visibility.Collapsed;
                BuildVariantCombo.Visibility = Visibility.Collapsed;
                BuildVariantSummaryText.Visibility = Visibility.Collapsed;
                BuildVariantCombo.ItemsSource = null;
                BuildVariantCombo.SelectedItem = null;
                BuildVariantSummaryText.Text = "";
            }
            finally
            {
                _updatingSelections = false;
            }
        }

        private void BuildVariantCombo_OnSelectionChanged'''),
    "hide build variant UI",
)
main_path.write_text(main, encoding="utf-8")

smoke_path = Path("SmokeTests/Program.cs")
smoke = smoke_path.read_text(encoding="utf-8")
smoke = replace_once(
    smoke,
    '''Assert(jinbeWithBlood.Any(rec =>
        rec.Route.GoalUnitId.Equals("rawcode:3A0h", StringComparison.OrdinalIgnoreCase) &&
        rec.RecipeProgress.CompletionRatio < 1),
    "징베는 S-호크 세라핌을 추천하되 그린블러드만으로 100퍼센트가 되지 않음");''',
    '''Assert(jinbeWithBlood.Count <= 8 && jinbeWithBlood.Any(rec =>
        rec.Route.GoalUnitId.Equals("rawcode:3A0h", StringComparison.OrdinalIgnoreCase) &&
        rec.RecipeProgress.CompletionRatio < 1),
    "징베는 8개 제한 안에서 S-호크를 추천하고 그린블러드만으로 100퍼센트가 되지 않음");''',
    "seraphim count regression",
)
smoke = replace_once(
    smoke,
    '''Assert(kizaruReverseSupports.Any(unit => SupportAbility(unit, "단일") > 0) &&
       kizaruReverseSupports.Any(unit => SupportAbility(unit, "끝딜") > 0),
    "역발상 키자루는 특포 부족을 단일·끝딜 보강으로 메움");''',
    '''Assert(kizaruReversePicks.Count <= 10 &&
       kizaruReverseSupports.Any(unit => SupportAbility(unit, "단일") > 0) &&
       kizaruReverseSupports.Any(unit => SupportAbility(unit, "끝딜") > 0),
    "역발상 키자루는 추천 제한 안에서 특포 부족을 단일·끝딜 보강으로 메움");''',
    "kizaru cap regression",
)
smoke_path.write_text(smoke, encoding="utf-8")
