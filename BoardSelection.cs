namespace OrandOverlay;

/// <summary>
/// 후보 보드에서 부모 클러스터와 그 하위패를 같은 선택으로 다룬다.
/// 하위패는 추천 목록에 없을 수 있어서, 고른 ID가 자식이면 부모 칸은 유지한다.
/// </summary>
public static class BoardSelection
{
    public static bool SameId(string? left, string? right) =>
        left is { Length: > 0 } && right is { Length: > 0 } &&
        left.Equals(right, StringComparison.OrdinalIgnoreCase);

    public static bool Matches(Recommendation rec, string? id) =>
        SameId(rec.Route.Id, id) || SameId(rec.Route.GoalUnitId, id);

    public static Recommendation? Find(IEnumerable<Recommendation> recs, string? id) =>
        recs.FirstOrDefault(item => Matches(item, id));

    public static bool Contains(IEnumerable<Recommendation> recs, string? id) =>
        Find(recs, id) is not null;

    public static Recommendation? Resolve(
        IReadOnlyList<Recommendation> recs,
        IReadOnlyList<Recommendation> children,
        string? selectedId) =>
        Find(recs, selectedId) ?? Find(children, selectedId) ?? recs.FirstOrDefault();

    public static string? ClusterHeadId(
        IReadOnlyList<Recommendation> recs,
        IReadOnlyList<Recommendation> children,
        string? selectedId,
        string? previousHeadId)
    {
        if (Contains(recs, previousHeadId))
            return Find(recs, previousHeadId)!.Route.Id;
        if (Contains(recs, selectedId))
            return Find(recs, selectedId)!.Route.Id;
        var parentId = children
            .Select(child => child.ClusterParentUnitId)
            .FirstOrDefault(id => id is { Length: > 0 });
        if (parentId is { Length: > 0 })
        {
            var head = recs.FirstOrDefault(item =>
                SameId(item.Route.GoalUnitId, parentId) || SameId(item.Route.Id, parentId));
            if (head is not null) return head.Route.Id;
        }
        return recs.FirstOrDefault()?.Route.Id;
    }

    public static bool IsKnown(
        IReadOnlyList<Recommendation> recs,
        IReadOnlyList<Recommendation> children,
        string? selectedId) =>
        Contains(recs, selectedId) || Contains(children, selectedId);
}
