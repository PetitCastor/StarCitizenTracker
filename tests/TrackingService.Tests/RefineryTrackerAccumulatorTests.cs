using TrackingService.Orders;
using TrackingService.Trackers;
using Xunit;

namespace TrackingService.Tests;

public class RefineryTrackerAccumulatorTests
{
    private static OrderMaterial Mat(string name, int quality, int qty, int yield, bool refine)
        => new(name, quality, qty, yield, refine);

    [Fact]
    public void Merge_NewRows_KeepInsertionOrder()
    {
        var acc = new RefineryTracker.Accumulator();
        acc.Merge(Mat("Titanium", 262, 10, 12, true));
        acc.Merge(Mat("Gold", 100, 5, 6, false));

        Assert.Equal(["Titanium", "Gold"], acc.Materials.Select(m => m.Name));
    }

    [Fact]
    public void Merge_SameNameAndQuality_ReplacesRowButKeepsOriginalOrder()
    {
        var acc = new RefineryTracker.Accumulator();
        acc.Merge(Mat("Titanium (Ore)", 262, 10, 12, true));
        acc.Merge(Mat("Gold", 100, 5, 6, false));
        // Rescroll: same material (same base name + quality) seen again with new values.
        acc.Merge(Mat("Titanium", 262, 11, 13, false));

        var materials = acc.Materials;
        Assert.Equal(2, materials.Count);
        Assert.Equal("Titanium", materials[0].Name); // replaced wholesale, original slot kept
        Assert.Equal(11, materials[0].QtyCscu);
        Assert.Equal(13, materials[0].YieldCscu);
        Assert.False(materials[0].RefineOn);
    }

    [Fact]
    public void Merge_SameNameDifferentQuality_KeptAsDistinctRows()
    {
        var acc = new RefineryTracker.Accumulator();
        acc.Merge(Mat("Torite (Ore)", 262, 112, 50, false));
        acc.Merge(Mat("Torite (Ore)", 785, 156, 70, true));

        // Two batches of the same material at different qualities must not collapse.
        Assert.Equal(2, acc.Materials.Count);
        Assert.Equal([262, 785], acc.Materials.Select(m => m.Quality));
    }

    [Fact]
    public void IsEmpty_TrueUntilFirstMerge()
    {
        var acc = new RefineryTracker.Accumulator();
        Assert.True(acc.IsEmpty);

        acc.Merge(Mat("Gold", 100, 1, 1, true));
        Assert.False(acc.IsEmpty);
    }

    [Theory]
    [InlineData(200, 50, true)]   // clearly orange/red (ON)
    [InlineData(80, 80, false)]   // clearly neutral gray
    [InlineData(141, 78, true)]   // just over both thresholds
    [InlineData(141, 79, false)]  // R > 140 but R <= B*1.8
    [InlineData(140, 50, false)]  // R not strictly > 140
    [InlineData(251, 244, false)] // white knob (OFF) — R high but R <= B*1.8
    public void IsRefineOn_AppliesColorThreshold(byte r, byte b, bool expected)
        => Assert.Equal(expected, RefineryTracker.IsRefineOn((b, 0, r)));
}
