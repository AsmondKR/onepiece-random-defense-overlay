namespace OrandOverlay;

public static class RecommendationPresentation
{
    public static string CompletionPercent(RecipeProgress progress) =>
        $"{Math.Round(progress.CompletionRatio * 100, MidpointRounding.AwayFromZero):0}%";

    public static string AbilitySummary(CompositionUnitDetail unit)
    {
        if (unit.Abilities.Count == 0)
            return unit.UnitId.Equals("item_greenblood", StringComparison.OrdinalIgnoreCase)
                ? "특수 아이템 · 적용 수치는 대상 유닛에서 확인"
                : "표시할 능력 수치 없음";

        return string.Join(" · ", unit.Abilities.Select(ability =>
            $"{ShortAbilityName(ability.Name)} {SafeText(ability.DisplayValue)}"));
    }

    public static string RecommendationEffectLine(CompositionUnitDetail unit) =>
        AbilitySummary(unit);

    public static string Ownership(CompositionUnitDetail unit)
    {
        var target = Math.Max(1, unit.IsRequired ? unit.RequiredCount : unit.SuggestedCount);
        return $"보유 {unit.OwnedCount}/{target}";
    }

    public static string CraftUnitName(CompositionUnitDetail unit) =>
        CraftUnitName(unit.Name, unit.Tier);

    public static string CraftUnitName(RecipeTreeNode unit) =>
        CraftUnitName(unit.Name, unit.Tier);

    public static string CraftUnitName(string name, string tier)
    {
        name = SafeText(name).Trim();
        var baseTier = SafeText(tier).Split('[', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(baseTier)) return name;
        name = RemoveTierSuffix(name, baseTier);
        return $"{name} - {baseTier}";
    }

    public static string CraftIngredientLine(RecipeCraftStep step)
    {
        var select = CraftSelectUnitName(step);
        if (select is null) return "조합할 하위 유닛 없음";
        var line = $"선택할 유닛: {select}";
        // 맵에서 추출한 조합 키가 있으면 실제 조작(선택 → 키)을 그대로 안내한다.
        if (step.CombineKey is { Length: > 0 } key)
            return line + $"\n유닛 조합 키: {key}";
        var companions = CraftCompanionNames(step);
        return companions is null ? line : line + "\n함께 조합: " + companions;
    }

    /// <summary>조합 시작 시 클릭해야 하는 유닛 이름. 재료가 없으면 null.</summary>
    public static string? CraftSelectUnitName(RecipeCraftStep step)
    {
        if (step.Ingredients.Count == 0) return null;
        return IngredientName(step.Ingredients.OrderBy(i => i.SelectionOrder).First());
    }

    /// <summary>조합 키가 없을 때 함께 조합할 나머지 재료 목록("A / B"). 없으면 null.</summary>
    public static string? CraftCompanionNames(RecipeCraftStep step)
    {
        if (step.CombineKey is { Length: > 0 } || step.Ingredients.Count <= 1) return null;
        var ordered = step.Ingredients.OrderBy(i => i.SelectionOrder).Skip(1).ToList();
        return ordered.Count == 0 ? null : string.Join(" / ", ordered.Select(IngredientName));
    }

    private static string IngredientName(RecipeCraftIngredient ingredient)
    {
        var tier = SafeText(ingredient.Tier).Split('[', 2)[0].Trim();
        // 단계별 총 부족 수량은 '남은 제작'에만 표시한다. 여기서는 실제로
        // 어떤 유닛을 선택하고 함께 조합하는지만 간결하게 안내한다.
        return RemoveTierSuffix(SafeText(ingredient.Name).Trim(), tier);
    }

    public static string LeafStatus(RecipeLeafProgress leaf) =>
        $"보유 {leaf.OwnedCount}/{leaf.RequiredCount}장 · {leaf.MissingCount}장 부족";

    public static string SafeDescription(string value) => SafeText(value);

    public static string SafeText(string value)
    {
        var safe = KoreanLabels.ContainsLatin(value) ? KoreanLabels.RemoveLatin(value) : value;
        return safe.Replace("%", "퍼센트", StringComparison.Ordinal);
    }

    public static string ShortAbilityName(string value) => SafeText(value) switch
    {
        "이동속도 감소" => "이감",
        "발동이동속도 감소" => "발동이감",
        "방어력 감소" => "방깎",
        "발동방어력 감소" => "발동방깎",
        "중첩방어력 감소" => "중첩방깎",
        "단일방어력 감소" => "단일방깎",
        "공격력 증가" => "공증",
        "발동공격력 증가" => "발동공증",
        "공격속도 증가" => "공속",
        "마법 방어력 감소" => "마방깎",
        "마법 대미지 증가" => "마딜증폭",
        "단일마법 대미지 증가" => "단일마딜증폭",
        "폭발형 대미지 증폭" => "폭발증폭",
        "방어력 무시 대미지" => "방무뎀",
        "보스 잡기" => "보잡",
        "광폭화 잡기" => "광잡",
        "체력 재생" => "체젠",
        "마나 재생" => "마젠",
        "유닛삭제" => "유닛 삭제",
        "모든피해증가" => "모든 피해 증가",
        var name => name
    };

    private static string RemoveTierSuffix(string name, string tier)
    {
        if (!string.IsNullOrWhiteSpace(tier) &&
            name.EndsWith(" " + tier, StringComparison.OrdinalIgnoreCase))
            return name[..^(tier.Length + 1)].TrimEnd();
        return name;
    }

}
