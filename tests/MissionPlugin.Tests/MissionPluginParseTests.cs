using Xunit;

namespace MissionPlugin.Tests;

/// <summary>
/// The monolith's MissionTrackerLogicTests, retargeted at the ported statics. The cases are
/// unchanged on purpose: they are the record of what OCR actually produces for this tab, and a
/// port that quietly relaxed one of them would be a behaviour change dressed up as a move.
/// </summary>
public class MissionPluginParseTests
{
    [Theory]
    [InlineData("Accepted (3/10)", 3, 10)]
    [InlineData("ACCEPTED 3 / 10", 3, 10)]
    [InlineData("accepted(0/5)", 0, 5)]
    public void ParseAcceptedCounter_MatchesVariousFormats(string tabText, int expectedAccepted, int expectedTotal)
    {
        var result = MissionPlugin.ParseAcceptedCounter(tabText);

        Assert.NotNull(result);
        Assert.Equal(expectedAccepted, result.Value.Accepted);
        Assert.Equal(expectedTotal, result.Value.Total);
    }

    [Fact]
    public void ParseAcceptedCounter_NoMatch_ReturnsNull()
        => Assert.Null(MissionPlugin.ParseAcceptedCounter("nothing relevant here"));

    [Theory]
    [InlineData(-1, 0, false)]  // first sighting, never counts as new
    [InlineData(-1, 5, false)]
    [InlineData(0, 1, true)]    // increment
    [InlineData(5, 4, false)]   // decrement (completion/abandon)
    [InlineData(5, 5, false)]   // unchanged
    [InlineData(3, 5, false)]   // jump, not a simple increment
    public void IsNewMissionAccepted_OnlyTrueOnSimpleIncrement(int previous, int current, bool expected)
        => Assert.Equal(expected, MissionPlugin.IsNewMissionAccepted(previous, current));
}
