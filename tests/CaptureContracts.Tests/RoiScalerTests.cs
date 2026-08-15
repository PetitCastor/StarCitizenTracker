using CaptureContracts;
using Xunit;

namespace CaptureContracts.Tests;

/// <summary>
/// Parity with the monolith's RoiScalerTests: same inputs, same expected outputs, on the
/// RoiRect port. If these ever disagree the two scalers have drifted.
/// </summary>
public class RoiScalerTests
{
    private static readonly RoiRect SampleRoi = new(620, 640, 440, 340);

    [Fact]
    public void ToFrame_AtReferenceResolution_ReturnsRoiUnchanged()
    {
        var scaled = RoiScaler.ToFrame(SampleRoi, RoiScaler.ReferenceWidth, RoiScaler.ReferenceHeight);

        Assert.Equal(SampleRoi.X, scaled.X);
        Assert.Equal(SampleRoi.Y, scaled.Y);
        Assert.Equal(SampleRoi.Width, scaled.Width);
        Assert.Equal(SampleRoi.Height, scaled.Height);
    }

    [Fact]
    public void ToFrame_At1080p_ScalesByThreeQuarters()
    {
        var scaled = RoiScaler.ToFrame(SampleRoi, 1920, 1080);

        Assert.Equal(465u, scaled.X);      // 620 * 0.75
        Assert.Equal(480u, scaled.Y);      // 640 * 0.75
        Assert.Equal(330u, scaled.Width);  // right 1060*0.75=795, 795-465
        Assert.Equal(255u, scaled.Height); // bottom 980*0.75=735, 735-480
    }

    [Fact]
    public void ToFrame_AdjacentRois_StayAdjacentAfterScaling()
    {
        // Edge-based rounding: a ROI ending where another begins must not gap or overlap.
        var left = new RoiRect(100, 0, 233, 100);
        var right = new RoiRect(333, 0, 233, 100);

        var scaledLeft = RoiScaler.ToFrame(left, 1920, 1080);
        var scaledRight = RoiScaler.ToFrame(right, 1920, 1080);

        Assert.Equal(scaledLeft.X + scaledLeft.Width, scaledRight.X);
    }

    [Fact]
    public void DescribeFrame_Ultrawide_WarnsAboutAspect()
    {
        // The non-16:9 case: per-axis scaling is unverified, so the banner must say so.
        Assert.Contains("WARNING", RoiScaler.DescribeFrame(3440, 1440));
    }
}
