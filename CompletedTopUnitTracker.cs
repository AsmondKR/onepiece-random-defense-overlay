namespace OrandOverlay;

/// <summary>
/// Warcraft can temporarily replace a completed top unit with skill/helper objects. Once a
/// top unit has been authoritatively observed in the current match, keep it completed for
/// recommendation purposes until the recognizer confirms a real session boundary.
/// </summary>
public sealed class CompletedTopUnitTracker(DataCatalog catalog)
{
    private readonly HashSet<string> _completed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>텔레메트리용: 이번 세션에서 완료 처리된 상위 유닛 ID 목록.</summary>
    public IReadOnlyCollection<string> CompletedUnitIds => _completed.ToList();

    public void Observe(IEnumerable<InventoryEntry> inventory)
    {
        foreach (var entry in inventory.Where(entry => entry.Count > 0))
        {
            if (IsTopTier(catalog.Unit(entry.UnitId).Tier)) _completed.Add(entry.UnitId);
        }
    }

    public IReadOnlyList<InventoryEntry> Apply(IEnumerable<InventoryEntry> inventory)
    {
        var result = inventory.ToList();
        var present = result.Where(entry => entry.Count > 0)
            .Select(entry => entry.UnitId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unitId in _completed.Where(unitId => !present.Contains(unitId)))
            result.Add(new InventoryEntry { UnitId = unitId, Count = 1, Confidence = 1 });
        return result;
    }

    private string? _armedGoalId;
    private int _armedMissingStreak;
    private const int MissingStreakForCrafted = 2;

    /// <summary>
    /// 완성 상위는 카드존을 떠나 인식되지 않으므로, 목표의 직접 재료가 전부
    /// 모였다가(조합 가능 상태) 이후 연속 2회 스캔에서 전부 사라지면 목표가
    /// 조합된 것으로 추정한다. 재료 전체가 동시에 다른 곳에 쓰일 일은 없다.
    /// 일부만 사라지면(개별 판매 등) 추정을 해제한다.
    /// </summary>
    public void ObserveGoalCraft(string goalId, IEnumerable<InventoryEntry> inventory)
    {
        var goal = catalog.Unit(goalId);
        if (!IsTopTier(goal.Tier) || goal.Recipe.Count == 0) return;
        var counts = inventory
            .Where(entry => entry.Count > 0)
            .GroupBy(entry => entry.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count),
                StringComparer.OrdinalIgnoreCase);
        if (counts.GetValueOrDefault(goalId) > 0 || _completed.Contains(goalId))
        {
            if (_armedGoalId == goalId) _armedGoalId = null;
            return;
        }
        var ingredients = goal.Recipe
            .Where(pair => !IsResourceLike(catalog.Unit(pair.Key)))
            .ToList();
        if (ingredients.Count == 0) return;
        if (ingredients.All(pair => counts.GetValueOrDefault(pair.Key) >= pair.Value))
        {
            _armedGoalId = goalId;
            _armedMissingStreak = 0;
            return;
        }
        if (_armedGoalId != goalId) return;
        if (!ingredients.All(pair => counts.GetValueOrDefault(pair.Key) == 0))
        {
            _armedGoalId = null;
            return;
        }
        if (++_armedMissingStreak < MissingStreakForCrafted) return;
        _completed.Add(goalId);
        _armedGoalId = null;
    }

    private static bool IsResourceLike(UnitDefinition unit) =>
        unit.Tier.Equals("자원", StringComparison.OrdinalIgnoreCase) ||
        unit.Rawcodes.Any(code => code is "GOLD" or "LUMBER" or "POINT" or "RANDOM");

    public void Reset()
    {
        _completed.Clear();
        _armedGoalId = null;
        _armedMissingStreak = 0;
    }

    public bool Contains(string unitId) => _completed.Contains(unitId);

    /// <summary>
    /// 조합 직후 상위가 카드존을 거치지 않고 필드로 나가면 스캔이 못 본다.
    /// 사용자가 "조합 완료"를 직접 표시/해제하는 경로. 상위 등급만 받는다.
    /// </summary>
    public bool ToggleCompleted(string unitId)
    {
        if (!IsTopTier(catalog.Unit(unitId).Tier)) return false;
        if (!_completed.Add(unitId)) _completed.Remove(unitId);
        return true;
    }

    private static bool IsTopTier(string tier)
    {
        var baseTier = tier.Split('[', 2)[0].Trim();
        return baseTier is "신비함" or "초월" or "불멸" or "영원" or "제한됨";
    }
}
