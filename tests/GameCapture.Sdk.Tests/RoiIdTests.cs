using Xunit;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// The id type earns tests for one reason: it is a struct, so <c>default(RoiId)</c> exists without a
/// constructor ever running, and every lookup in <see cref="TickData"/> goes through its equality.
/// </summary>
public class RoiIdTests
{
    [Fact]
    public void Default_EqualsTheEmptyId()
    {
        Assert.Equal(new RoiId(string.Empty), default);
        Assert.Equal(new RoiId(string.Empty).GetHashCode(), default(RoiId).GetHashCode());
    }

    [Fact]
    public void NullValue_IsNormalisedToEmpty()
    {
        RoiId fromNull = new(null!);

        Assert.Equal(string.Empty, fromNull.Value);
        Assert.Equal(new RoiId(string.Empty), fromNull);
    }

    [Fact]
    public void Default_FindsTheEmptyKeyInADictionary()
    {
        var byId = new Dictionary<RoiId, string> { [new RoiId(string.Empty)] = "hit" };

        Assert.True(byId.TryGetValue(default, out var found));
        Assert.Equal("hit", found);
    }

    [Fact]
    public void Comparison_StaysOrdinal()
    {
        Assert.NotEqual(new RoiId("Panel"), new RoiId("panel"));
    }

    [Fact]
    public void ImplicitConversion_KeepsStringCallSitesWorking()
    {
        RoiId id = "panel";

        Assert.Equal(new RoiId("panel"), id);
        Assert.Equal("panel", id.ToString());
    }

    [Fact]
    public void ToString_OfDefault_IsEmptyNotNull()
    {
        Assert.Equal(string.Empty, default(RoiId).ToString());
    }
}
