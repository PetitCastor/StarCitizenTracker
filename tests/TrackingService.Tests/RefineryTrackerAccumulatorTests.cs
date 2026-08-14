using TrackingService.Trackers;
using Xunit;

namespace TrackingService.Tests;

public class RefineryTrackerAccumulatorTests
{
    [Fact]
    public void Merge_NewRow_AssignsInsertionOrder()
    {
        var acc = new RefineryTracker.Accumulator();
        acc.Merge(new MaterialRow("Titanium", 10, 12, true));
        acc.Merge(new MaterialRow("Gold", 5, 6, false));

        var order = acc.ToOrder();

        Assert.Equal(["Titanium", "Gold"], order.Materials.Select(m => m.Name));
    }

    [Fact]
    public void Merge_SameNameAgain_ReplacesRowButKeepsOriginalOrder()
    {
        var acc = new RefineryTracker.Accumulator();
        acc.Merge(new MaterialRow("Titanium", 10, 12, true));
        acc.Merge(new MaterialRow("Gold", 5, 6, false));
        // Rescroll / toggle flip: Titanium seen again with new values. Last-seen-wins applies
        // to the whole row, including name casing — the dictionary key match is case-insensitive
        // but the stored Row is replaced wholesale.
        acc.Merge(new MaterialRow("titanium", 11, 13, false));

        var order = acc.ToOrder();

        Assert.Equal(["titanium", "Gold"], order.Materials.Select(m => m.Name));
        Assert.Equal(11, order.Materials[0].QtyScu);
        Assert.Equal(13, order.Materials[0].YieldScu);
        Assert.False(order.Materials[0].RefineOn);
    }

    [Fact]
    public void IsEmpty_TrueUntilFirstMerge()
    {
        var acc = new RefineryTracker.Accumulator();
        Assert.True(acc.IsEmpty);

        acc.Merge(new MaterialRow("Gold", 1, 1, true));
        Assert.False(acc.IsEmpty);
    }

    [Fact]
    public void ToOrder_UnsetFields_DefaultToPlaceholder()
    {
        var acc = new RefineryTracker.Accumulator();
        acc.Merge(new MaterialRow("Gold", 1, 1, true));

        var order = acc.ToOrder();

        Assert.Equal("?", order.Station);
        Assert.Equal("?", order.Process);
        Assert.Equal("?", order.TotalCost);
        Assert.Equal("?", order.ProcessingTime);
    }

    [Theory]
    [InlineData(200, 50, true)]   // clearly orange
    [InlineData(80, 80, false)]   // clearly neutral gray
    [InlineData(141, 78, true)]   // just over both thresholds
    [InlineData(141, 79, false)]  // R > 140 but R <= B*1.8
    [InlineData(140, 50, false)]  // R not strictly > 140
    public void IsRefineOn_AppliesColorThreshold(byte r, byte b, bool expected)
        => Assert.Equal(expected, RefineryTracker.IsRefineOn((b, 0, r)));
}
