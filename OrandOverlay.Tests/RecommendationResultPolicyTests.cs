using OrandOverlay;
using Xunit;

namespace OrandOverlay.Tests;

public sealed class RecommendationResultPolicyTests
{
    [Fact]
    public void Limit_CapsItemsAddedAfterInitialRanking()
    {
        var result = RecommendationResultPolicy.Limit(Enumerable.Range(1, 9), 8);
        Assert.Equal(Enumerable.Range(1, 8), result);
    }

    [Fact]
    public void Limit_AlwaysReturnsAtLeastOneSlot()
    {
        Assert.Single(RecommendationResultPolicy.Limit(new[] { "first", "second" }, 0));
    }

    [Fact]
    public void Limit_PreservesRankingOrder()
    {
        Assert.Equal(new[] { "goal", "support" },
            RecommendationResultPolicy.Limit(new[] { "goal", "support", "extra" }, 2));
    }
}
