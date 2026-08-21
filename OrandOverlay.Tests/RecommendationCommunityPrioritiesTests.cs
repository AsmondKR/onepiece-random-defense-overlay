using OrandOverlay;
using Xunit;

namespace OrandOverlay.Tests;

public sealed class RecommendationCommunityPrioritiesTests
{
    [Theory]
    [InlineData("DB0H", "Q30h", 12)]
    [InlineData("B90H", "M30h", 21)]
    [InlineData("F90H", "F50h", 30)]
    [InlineData("A90H", "Q80h", 40)]
    public void ForGoal_ReturnsExtractedPolicy(
        string goalRawcode, string candidateRawcode, int expected)
    {
        var goal = new UnitDefinition
        {
            Id = "goal",
            Name = "목표",
            Rawcodes = [goalRawcode]
        };
        var priorities = RecommendationCommunityPriorities.ForGoal(goal);
        Assert.NotNull(priorities);
        Assert.Equal(expected, priorities![candidateRawcode]);
    }

    [Fact]
    public void ForGoal_ReturnsNullForUnmappedGoal()
    {
        var goal = new UnitDefinition
        {
            Id = "other",
            Name = "기타",
            Rawcodes = ["0000"]
        };
        Assert.Null(RecommendationCommunityPriorities.ForGoal(goal));
    }
}
