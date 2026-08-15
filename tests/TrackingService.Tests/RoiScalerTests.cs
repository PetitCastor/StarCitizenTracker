using TrackingService;
using Windows.Graphics.Imaging;
using Xunit;

namespace TrackingService.Tests;

public class RoiScalerTests
{
    private static readonly BitmapBounds SampleRoi = new() { X = 620, Y = 640, Width = 440, Height = 340 };

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
        var left = new BitmapBounds { X = 100, Y = 0, Width = 233, Height = 100 };
        var right = new BitmapBounds { X = 333, Y = 0, Width = 233, Height = 100 };

        var scaledLeft = RoiScaler.ToFrame(left, 1920, 1080);
        var scaledRight = RoiScaler.ToFrame(right, 1920, 1080);

        Assert.Equal(scaledLeft.X + scaledLeft.Width, scaledRight.X);
    }

    [Fact]
    public void ToFrame_RoiTouchingFrameEdge_StaysInsideFrame()
    {
        var edgeRoi = new BitmapBounds { X = 2100, Y = 1300, Width = 460, Height = 140 };

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
        // CaptureAsync would hand it straight to a bitmap crop.
        var overflowing = new BitmapBounds { X = 2500, Y = 1400, Width = 400, Height = 200 };

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
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RoiScaler.ToFrame(SampleRoi, frameWidth, frameHeight));
    }

    [Fact]
    public void ToFrameX_ScalesReferenceColumn()
    {
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
        var text = RoiScaler.DescribeFrame(1920, 1080);

        Assert.Contains("scaled", text);
        Assert.DoesNotContain("WARNING", text);
    }

    [Fact]
    public void DescribeFrame_Ultrawide_WarnsAboutAspect()
    {
        Assert.Contains("WARNING", RoiScaler.DescribeFrame(3440, 1440));
    }
}
