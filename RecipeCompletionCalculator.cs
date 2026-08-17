namespace OrandOverlay;

/// <summary>
/// Expands a composition into its lowest non-resource cards and allocates the current inventory
/// top-down. Owning a completed upper/intermediate unit replaces its whole subtree, while the
/// mutable availability map prevents the same card from satisfying two branches.
/// </summary>
public sealed class RecipeCompletionCalculator(Func<string, UnitDefinition> resolveUnit)
{
    private static readonly HashSet<string> ResourcePseudoIds =
        new(["GOLD", "LUMBER", "POINT", "RANDOM"], StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, long>> _leafCache =
        new(StringComparer.OrdinalIgnoreCase);

    public RecipeProgress Calculate(IEnumerable<string> requiredUnitIds,
        IReadOnlyDictionary<string, int> inventory)
    {
        var requirements = requiredUnitIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.OrdinalIgnoreCase);
        var availability = inventory
            .Where(pair => pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => (long)pair.Value, StringComparer.OrdinalIgnoreCase);
        var requiredLeaves = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var ownedLeaves = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var (unitId, count) in requirements)
            foreach (var (leafId, leafCount) in ExpandLeaves(unitId, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                Add(requiredLeaves, leafId, Multiply(leafCount, count));

        foreach (var (unitId, count) in requirements)
            Allocate(unitId, count, availability, ownedLeaves,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var leaves = requiredLeaves
            .Select(pair =>
            {
                var owned = Math.Min(pair.Value, ownedLeaves.GetValueOrDefault(pair.Key));
                var unit = resolveUnit(pair.Key);
                return new RecipeLeafProgress
                {
                    UnitId = pair.Key,
                    Name = unit.Name,
                    Tier = unit.Tier,
                    Image = unit.Image,
                    RequiredCount = pair.Value,
                    OwnedCount = owned
                };
            })
            .OrderBy(x => x.Name, StringComparer.CurrentCulture)
            .ToList();
        return new RecipeProgress
        {
            RequiredLeafCount = Sum(leaves.Select(x => x.RequiredCount)),
            OwnedLeafCount = Sum(leaves.Select(x => x.OwnedCount)),
            Leaves = leaves
        };
    }

    private IReadOnlyDictionary<string, long> ExpandLeaves(string unitId, HashSet<string> visiting)
    {
        if (_leafCache.TryGetValue(unitId, out var cached)) return cached;
        var unit = resolveUnit(unitId);
        if (IsResourcePseudo(unit)) return Cache(unitId, new Dictionary<string, long>());
        if (!visiting.Add(unitId))
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [unitId] = 1 };

        var children = RecipeChildren(unit).ToList();
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (children.Count == 0)
            result[unitId] = 1;
        else
            foreach (var (childId, childCount) in children)
                foreach (var (leafId, leafCount) in ExpandLeaves(childId, visiting))
                    Add(result, leafId, Multiply(leafCount, childCount));
        visiting.Remove(unitId);
        return Cache(unitId, result);
    }

    private void Allocate(string unitId, long demand, IDictionary<string, long> availability,
        IDictionary<string, long> ownedLeaves, HashSet<string> visiting)
    {
        if (demand <= 0) return;
        var unit = resolveUnit(unitId);
        if (IsResourcePseudo(unit)) return;

        var available = availability.TryGetValue(unitId, out var availableCount) ? availableCount : 0;
        var directlyOwned = Math.Min(demand, available);
        if (directlyOwned > 0)
        {
            availability[unitId] = available - directlyOwned;
            foreach (var (leafId, leafCount) in ExpandLeaves(unitId,
                         new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                Add(ownedLeaves, leafId, Multiply(leafCount, directlyOwned));
            demand -= directlyOwned;
        }
        if (demand == 0 || !visiting.Add(unitId)) return;

        foreach (var (childId, childCount) in RecipeChildren(unit))
            Allocate(childId, Multiply(demand, childCount), availability, ownedLeaves, visiting);
        visiting.Remove(unitId);
    }

    private IEnumerable<KeyValuePair<string, int>> RecipeChildren(UnitDefinition unit) => unit.Recipe
        .Where(pair => pair.Value > 0 && !IsResourcePseudo(resolveUnit(pair.Key)));

    private static bool IsResourcePseudo(UnitDefinition unit)
    {
        if (unit.Tier.Equals("자원", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Rawcodes.Any(ResourcePseudoIds.Contains)) return true;
        const string prefix = "rawcode:";
        return unit.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               ResourcePseudoIds.Contains(unit.Id[prefix.Length..]);
    }

    private IReadOnlyDictionary<string, long> Cache(string unitId, Dictionary<string, long> value)
    {
        _leafCache[unitId] = value;
        return value;
    }

    private static void Add(IDictionary<string, long> target, string key, long amount) =>
        target[key] = Add(target.TryGetValue(key, out var current) ? current : 0, amount);

    private static long Add(long left, long right) => left >= long.MaxValue - right ? long.MaxValue : left + right;

    private static long Multiply(long left, long right) =>
        left == 0 || right == 0 ? 0 : left > long.MaxValue / right ? long.MaxValue : left * right;

    private static long Sum(IEnumerable<long> values)
    {
        var result = 0L;
        foreach (var value in values) result = Add(result, value);
        return result;
    }
}
