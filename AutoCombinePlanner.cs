namespace OrandOverlay;

/// <summary>
/// 현재 패에서 지금 바로 실행할 수 있는 조합 단계를 순서대로 계산한다.
/// 각 단계는 "어느 재료 유닛(트리거)을 선택해 어떤 키를 누르는지"까지 결정한다.
/// 실제 입력 전송은 실행기(AutoCombineService)가 담당하고, 이 클래스는 순수 계산만 한다.
/// </summary>
public sealed class AutoCombinePlanner(DataCatalog catalog, CombineHotkeyCatalog hotkeys)
{
    public IReadOnlyList<AutoCombineStep> Plan(
        IReadOnlyList<Recommendation> recommendations,
        IEnumerable<InventoryEntry> inventory,
        IEnumerable<string>? completedUnitIds = null)
    {
        if (!hotkeys.HasData || recommendations.Count == 0) return [];

        var owned = inventory
            .Where(entry => entry.Count > 0)
            .GroupBy(entry => entry.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count),
                StringComparer.OrdinalIgnoreCase);
        var completed = new HashSet<string>(
            completedUnitIds ?? [], StringComparer.OrdinalIgnoreCase);

        var steps = new List<AutoCombineStep>();
        var virtualOwned = new Dictionary<string, int>(owned, StringComparer.OrdinalIgnoreCase);
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 추천 순서대로, 하위 단계부터(RemainingCraftSteps가 이미 제작 순서) 지금
        // 재료가 전부 있는 단계만 담는다. 상위 단계는 하위가 완성되면 다음 계획에서 잡힌다.
        foreach (var recommendation in recommendations)
        {
            if (completed.Contains(recommendation.Route.GoalUnitId)) continue;
            if (recommendation.MissingSpecials.Count > 0) continue;
            // RemainingCraftSteps는 중간 단계만 담으므로 추천 유닛 자체(루트) 조합
            // 단계를 합성해 함께 검사한다.
            var rootStep = SynthesizeRootStep(recommendation);
            var craftSteps = rootStep is null
                ? recommendation.RemainingCraftSteps
                : recommendation.RemainingCraftSteps.Append(rootStep).ToList();
            foreach (var step in craftSteps)
            {
                if (step.MissingCount <= 0) continue;
                if (completed.Contains(step.UnitId)) continue;
                if (virtualOwned.GetValueOrDefault(step.UnitId) > 0) continue;
                if (!planned.Add(step.UnitId)) continue;
                if (step.Ingredients.Count == 0) continue;
                // 목재 같은 자원은 패 인식 대상이 아니므로 보유 조건에서 제외한다.
                var materialIngredients = step.Ingredients
                    .Where(ingredient => !IsResource(ingredient.UnitId))
                    .ToList();
                if (materialIngredients.Count == 0) continue;
                var ready = materialIngredients.All(ingredient =>
                    virtualOwned.GetValueOrDefault(ingredient.UnitId) >= ingredient.RequiredCount);
                if (!ready) continue;

                var resolved = Resolve(step);
                if (resolved is null) continue;
                steps.Add(resolved);

                // 이 단계가 소모하는 재료를 가상 차감해 같은 재료의 중복 계획을 막는다.
                foreach (var ingredient in materialIngredients)
                {
                    var remaining = virtualOwned.GetValueOrDefault(ingredient.UnitId) -
                                    ingredient.RequiredCount;
                    virtualOwned[ingredient.UnitId] = Math.Max(0, remaining);
                }
            }
        }
        return steps;
    }

    private static RecipeCraftStep? SynthesizeRootStep(Recommendation recommendation)
    {
        var root = recommendation.RecipeTree;
        if (root is null || root.OwnedCount > 0 || root.Children.Count == 0) return null;
        return new RecipeCraftStep
        {
            UnitId = root.UnitId,
            Name = root.Name,
            Tier = root.Tier,
            RequiredCount = 1,
            OwnedCount = 0,
            Ingredients = root.Children
                .Select((child, index) => new RecipeCraftIngredient
                {
                    UnitId = child.UnitId,
                    Name = child.Name,
                    Tier = child.Tier,
                    RequiredCount = child.RequiredCount,
                    SelectionOrder = index
                })
                .ToList()
        };
    }

    private bool IsResource(string unitId)
    {
        var plain = unitId.StartsWith("rawcode:", StringComparison.OrdinalIgnoreCase)
            ? unitId["rawcode:".Length..]
            : unitId;
        if (plain is "GOLD" or "LUMBER" or "POINT" or "RANDOM") return true;
        return catalog.Unit(unitId).Tier.Split('[', 2)[0].Trim() == "자원";
    }

    private AutoCombineStep? Resolve(RecipeCraftStep step)
    {
        // 결과 유닛 rawcode로 맵 데이터의 조합 키를 찾고, 이름 표기 폴백을 둔다.
        var entry = hotkeys.FindByResult(catalog.Unit(step.UnitId).Rawcodes) ??
                    hotkeys.FindByResultName($"{step.Name} - {step.Tier}") ??
                    hotkeys.FindByResultName(step.Name);
        if (entry is null) return null;

        // 트리거(먼저 선택) 재료는 앱의 조합 트리가 이미 알고 있다. 자원은 제외한다.
        var trigger = step.Ingredients
            .Where(ingredient => !IsResource(ingredient.UnitId))
            .OrderBy(ingredient => ingredient.SelectionOrder)
            .First();
        var triggerUnit = catalog.Unit(trigger.UnitId);

        return new AutoCombineStep(
            TargetUnitId: step.UnitId,
            TargetName: RecommendationPresentation.CraftUnitName(step.Name, step.Tier),
            TriggerUnitId: triggerUnit.Id,
            TriggerName: triggerUnit.Name,
            TriggerRawcode: triggerUnit.Rawcodes.FirstOrDefault() ?? "",
            Key: entry.Key);
    }
}

/// <summary>실행 계획 한 단계: 트리거 유닛을 선택하고 키를 누르면 대상이 조합된다.</summary>
public sealed record AutoCombineStep(
    string TargetUnitId,
    string TargetName,
    string TriggerUnitId,
    string TriggerName,
    string TriggerRawcode,
    string Key);
