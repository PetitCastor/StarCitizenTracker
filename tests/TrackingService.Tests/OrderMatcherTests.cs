using TrackingService.Orders;
using Xunit;

namespace TrackingService.Tests;

public class OrderMatcherTests
{
    private static WorkOrder Wo(
        string station,
        IEnumerable<(string Name, int Yield)> mats,
        OrderState state = OrderState.Pending,
        DateTime firstSeen = default)
    {
        var materials = mats.Select(m => new OrderMaterial(m.Name, 0, m.Yield, false)).ToList();
        return new WorkOrder(
            Id: "id-" + Guid.NewGuid().ToString("N"),
            Key: OrderMatcher.Key(station, materials.Select(m => m.Name)),
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
    public void Key_IsOrderAndCaseInsensitiveOverNames()
    {
        var a = OrderMatcher.Key("Rayari Anvik", ["Titanium", "Gold"]);
        var b = OrderMatcher.Key("rayari anvik", ["gold", "  titanium  "]);
        Assert.Equal(a, b);
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
}
