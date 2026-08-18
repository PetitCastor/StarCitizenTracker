using GameCapture.Contracts;
using Xunit;

namespace GameCapture.Contracts.Tests;

/// <summary>
/// The monolith's RoiScalerTests, now the only copy: same inputs, same expected outputs, on the
/// RoiRect port of the scaler. Every ROI in the system is placed by this arithmetic, so the cases
/// stay exactly as the monolith accumulated them rather than being re-derived from the code.
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
    public void ToFrame_At4K_ScalesUp()
    {
        var scaled = RoiScaler.ToFrame(SampleRoi, 3840, 2160);

        Assert.Equal(930u, scaled.X);
        Assert.Equal(960u, scaled.Y);
        Assert.Equal(660u, scaled.Width);
        Assert.Equal(510u, scaled.Height);
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
    public void ToFrame_RoiTouchingFrameEdge_StaysInsideFrame()
    {
        var edgeRoi = new RoiRect(2100, 1300, 460, 140);

        var scaled = RoiScaler.ToFrame(edgeRoi, 1920, 1080);

        Assert.True(scaled.X + scaled.Width <= 1920);
        Assert.True(scaled.Y + scaled.Height <= 1080);
        Assert.True(scaled.Width >= 1);
        Assert.True(scaled.Height >= 1);
    }

    [Fact]
    public void ToFrame_AtReferenceResolution_ClampsRoiThatOverflowsTheFrame()
    {
        // A mis-typed config value used to escape unclamped through the identity shortcut, and
        // the engine would hand it straight to a bitmap crop.
        var overflowing = new RoiRect(2500, 1400, 400, 200);

        var scaled = RoiScaler.ToFrame(overflowing, RoiScaler.ReferenceWidth, RoiScaler.ReferenceHeight);

        Assert.True(scaled.X + scaled.Width <= RoiScaler.ReferenceWidth);
        Assert.True(scaled.Y + scaled.Height <= RoiScaler.ReferenceHeight);
    }

    [Theory]
    [InlineData(0, 1440)]
    [InlineData(2560, 0)]
    [InlineData(-1920, 1080)]
    public void ToFrame_NonPositiveFrameSize_Throws(int frameWidth, int frameHeight)
    {
        // Clamping a rect into a zero-sized frame means Math.Clamp(v, 1, 0), which throws an
        // ArgumentException from deep inside the scaler; reject the frame size instead.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RoiScaler.ToFrame(SampleRoi, frameWidth, frameHeight));
    }

    [Fact]
    public void ToFrameX_ScalesReferenceColumn()
    {
        // The point-scaling overloads the pixel-strip ROIs use; the identity case has to survive
        // the same shortcut ToFrame takes.
        Assert.Equal(798, RoiScaler.ToFrameX(1064, 1920));
        Assert.Equal(1064, RoiScaler.ToFrameX(1064, RoiScaler.ReferenceWidth));
    }

    [Fact]
    public void ToFrameY_ScalesReferenceRow()
    {
        Assert.Equal(480, RoiScaler.ToFrameY(640, 1080));
    }

    [Fact]
    public void DescribeFrame_ReferenceResolution_SaysOneToOne()
    {
        Assert.Contains("1:1", RoiScaler.DescribeFrame(2560, 1440));
    }

    [Fact]
    public void DescribeFrame_Same16By9_NoAspectWarning()
    {
        // Scaling within 16:9 is the verified path, so the banner must not cry wolf about it.
        var text = RoiScaler.DescribeFrame(1920, 1080);

        Assert.Contains("scaled", text);
        Assert.DoesNotContain("WARNING", text);
    }

    [Fact]
    public void DescribeFrame_Ultrawide_WarnsAboutAspect()
    {
        // The non-16:9 case: per-axis scaling is unverified, so the banner must say so.
        Assert.Contains("WARNING", RoiScaler.DescribeFrame(3440, 1440));
    }
}
