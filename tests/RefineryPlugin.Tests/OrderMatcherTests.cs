using RefineryPlugin.Orders;
using Xunit;

namespace RefineryPlugin.Tests;

public class OrderMatcherTests
{
    private static WorkOrder Wo(
        string station,
        IEnumerable<(string Name, int Yield)> mats,
        OrderState state = OrderState.Pending,
        DateTime firstSeen = default)
    {
        var materials = mats.Select(m => new OrderMaterial(m.Name, 0, 0, m.Yield, false)).ToList();
        return new WorkOrder(
            Id: "id-" + Guid.NewGuid().ToString("N"),
            Key: OrderMatcher.Key(station, materials),
            Station: station,
            Process: "Diffusion",
            Cost: "1000 aUEC",
            Eta: "1h",
            State: state,
            Completeness: Completeness.Unknown,
            Materials: materials,
            TotalYieldCscu: null,
            RowsSeen: materials.Count,
            FirstSeen: firstSeen,
            LastSeen: firstSeen,
            Sources: ["SETUP"]);
    }

    [Fact]
    public void Key_IsOrderAndCaseInsensitiveAndStripsOreSuffix()
    {
        var a = OrderMatcher.Key("Rayari Anvik",
            [new OrderMaterial("Titanium (Ore)", 262, 0, 0, false), new OrderMaterial("Gold", 100, 0, 0, false)]);
        var b = OrderMatcher.Key("rayari anvik",
            [new OrderMaterial("gold", 100, 0, 0, false), new OrderMaterial("  titanium  ", 262, 0, 0, false)]);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Key_SameNameDifferentQuality_AreDistinctInKey()
    {
        var a = OrderMatcher.Key("S", [new OrderMaterial("Torite (Ore)", 262, 0, 0, false)]);
        var b = OrderMatcher.Key("S", [new OrderMaterial("Torite (Ore)", 785, 0, 0, false)]);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void IsClosed_OnlyCollectedIsClosed()
    {
        Assert.True(OrderMatcher.IsClosed(Wo("S", [("A", 1)], OrderState.Collected)));
        Assert.False(OrderMatcher.IsClosed(Wo("S", [("A", 1)], OrderState.Ready)));
        Assert.False(OrderMatcher.IsClosed(Wo("S", [("A", 1)], OrderState.Processing)));
        Assert.False(OrderMatcher.IsClosed(Wo("S", [("A", 1)], OrderState.Pending)));
    }

    [Fact]
    public void TryMatch_SubsetObservation_MatchesSupersetRecord()
    {
        var existing = Wo("S", [("A", 100), ("B", 200), ("C", 300)]);
        var partial = Wo("S", [("A", 100), ("B", 200)]);

        Assert.True(OrderMatcher.TryMatch(partial, [existing], out var best, out _));
        Assert.Equal(existing.Id, best!.Id);
    }

    [Fact]
    public void TryMatch_DifferentStation_NoMatch()
    {
        var existing = Wo("Station A", [("A", 100)]);
        var candidate = Wo("Station B", [("A", 100)]);
        Assert.False(OrderMatcher.TryMatch(candidate, [existing], out _, out _));
    }

    [Fact]
    public void TryMatch_DisjointNames_NoMatch()
    {
        var existing = Wo("S", [("A", 100), ("B", 200)]);
        var candidate = Wo("S", [("X", 100), ("Y", 200)]);
        Assert.False(OrderMatcher.TryMatch(candidate, [existing], out _, out _));
    }

    [Fact]
    public void TryMatch_SameStationSameNames_YieldClosenessBreaksTie()
    {
        var near = Wo("S", [("A", 150), ("B", 250)]);
        var far = Wo("S", [("A", 100), ("B", 200)]);
        var candidate = Wo("S", [("A", 148), ("B", 252)]); // within tolerance of `near`

        Assert.True(OrderMatcher.TryMatch(candidate, [far, near], out var best, out _));
        Assert.Equal(near.Id, best!.Id);
    }

    [Fact]
    public void TryMatch_IdenticalCandidates_EarliestFirstSeenWins()
    {
        var older = Wo("S", [("A", 100)], firstSeen: new DateTime(2026, 1, 1));
        var newer = Wo("S", [("A", 100)], firstSeen: new DateTime(2026, 6, 1));
        var candidate = Wo("S", [("A", 100)]);

        Assert.True(OrderMatcher.TryMatch(candidate, [newer, older], out var best, out _));
        Assert.Equal(older.Id, best!.Id);
    }

    [Fact]
    public void TryMatch_EmptyCandidateNames_NoMatch()
    {
        var existing = Wo("S", [("A", 100)]);
        var empty = Wo("S", []);
        Assert.False(OrderMatcher.TryMatch(empty, [existing], out _, out _));
    }

    // ---- SameMaterial quality tolerance (H5: quality is OCR-derived, not exact) ----

    private static OrderMaterial Mat(string name, int quality) => new(name, quality, 0, 0, false);

    [Fact]
    public void SameMaterial_OneDigitOff_MatchesWithinTolerance()
        => Assert.True(OrderMatcher.SameMaterial(Mat("GOLD", 714), Mat("GOLD", 715)));

    [Fact]
    public void SameMaterial_DigitSwap_ExceedsTolerance_DoesNotMatch()
        // 714 -> 774: a single-digit OCR confusion, but the delta (60) is well outside a tight
        // tolerance — must NOT match, or two genuinely different batches would collapse into one.
        => Assert.False(OrderMatcher.SameMaterial(Mat("GOLD", 714), Mat("GOLD", 774)));

    [Fact]
    public void SameMaterial_ZeroQualityOnEitherSide_IsWildcard_Matches()
    {
        Assert.True(OrderMatcher.SameMaterial(Mat("GOLD", 0), Mat("GOLD", 714)));
        Assert.True(OrderMatcher.SameMaterial(Mat("GOLD", 714), Mat("GOLD", 0)));
        Assert.True(OrderMatcher.SameMaterial(Mat("GOLD", 0), Mat("GOLD", 0)));
    }

    [Fact]
    public void SameMaterial_ClearlyDifferentQuality_GOLD714VsGOLD262_DoesNotMatch()
        // Regression guard for the original bug class: a same-station same-name material at a very
        // different quality must never collapse into one batch.
        => Assert.False(OrderMatcher.SameMaterial(Mat("GOLD", 714), Mat("GOLD", 262)));

    [Fact]
    public void SameMaterial_DifferentNameSameQuality_DoesNotMatch()
        => Assert.False(OrderMatcher.SameMaterial(Mat("GOLD", 714), Mat("TITANIUM", 714)));
}
