using GameCapture.Contracts;
using Xunit;

namespace GameCapture.Contracts.Tests;

/// <summary>
/// The two numbers proto3 cannot carry: what an unset scale means, and how big a PIXELS ROI
/// may get before it would blow the gRPC message cap and take the whole tick down with it.
/// </summary>
public class WireLimitsTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    public void NormalizeOcrScale_NonPositiveMeansEngineDefault(double requested)
    {
        Assert.Equal(WireLimits.DefaultOcrScale, WireLimits.NormalizeOcrScale(requested));
    }

    [Fact]
    public void NormalizeOcrScale_PositiveIsPassedThrough()
    {
        Assert.Equal(2.75, WireLimits.NormalizeOcrScale(2.75));
    }

    [Fact]
    public void FitsPixelBudget_AcceptsAToggleStripAndRejectsAScreenshot()
    {
        Assert.True(WireLimits.FitsPixelBudget(256, 256));   // exactly the cap
        Assert.False(WireLimits.FitsPixelBudget(257, 256));
        Assert.False(WireLimits.FitsPixelBudget(2560, 1440));
    }

    [Fact]
    public void FitsPixelBudget_DoesNotOverflowOnHugeDimensions()
    {
        Assert.False(WireLimits.FitsPixelBudget(uint.MaxValue, uint.MaxValue));
    }
}
