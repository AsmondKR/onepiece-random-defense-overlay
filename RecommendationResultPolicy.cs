namespace OrandOverlay;

/// <summary>후보를 뒤늦게 삽입해도 추천 API의 최대 개수 계약을 지킨다.</summary>
public static class RecommendationResultPolicy
{
    public static List<T> Limit<T>(IEnumerable<T> items, int take) =>
        items.Take(Math.Max(1, take)).ToList();
}
