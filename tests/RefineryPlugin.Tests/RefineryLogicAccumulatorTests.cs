using RefineryPlugin.Orders;
using Xunit;

namespace RefineryPlugin.Tests;

/// <summary>
/// The monolith's RefineryTrackerAccumulatorTests, moved onto <see cref="RefineryLogic.Accumulator"/>
/// unchanged: these cases are the record of how a scrolled SETUP list stitches back together, and
/// relaxing one during the port would be a behaviour change dressed up as a move. Its IsRefineOn
/// theory is not carried over — RefineryToggleSamplingTests already holds that exact theory.
/// </summary>
public class RefineryLogicAccumulatorTests
{
    private static OrderMaterial Mat(string name, int quality, int qty, int yield, bool refine)
        => new(name, quality, qty, yield, refine);

    [Fact]
    public void Merge_NewRows_KeepInsertionOrder()
    {
        var acc = new RefineryLogic.Accumulator();
        acc.Merge(Mat("Titanium", 262, 10, 12, true));
        acc.Merge(Mat("Gold", 100, 5, 6, false));

        Assert.Equal(["Titanium", "Gold"], acc.Materials.Select(m => m.Name));
    }

    [Fact]
    public void Merge_SameNameAndQuality_ReplacesRowButKeepsOriginalOrder()
    {
        var acc = new RefineryLogic.Accumulator();
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
        var acc = new RefineryLogic.Accumulator();
        acc.Merge(Mat("Torite (Ore)", 262, 112, 50, false));
        acc.Merge(Mat("Torite (Ore)", 785, 156, 70, true));

        // Two batches of the same material at different qualities must not collapse.
        Assert.Equal(2, acc.Materials.Count);
        Assert.Equal([262, 785], acc.Materials.Select(m => m.Quality));
    }

    [Fact]
    public void IsEmpty_TrueUntilFirstMerge()
    {
        var acc = new RefineryLogic.Accumulator();
        Assert.True(acc.IsEmpty);

        acc.Merge(Mat("Gold", 100, 1, 1, true));
        Assert.False(acc.IsEmpty);
    }
}
