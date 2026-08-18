using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Xunit;

namespace GameCapture.Engine.Tests;

public class OcrPipelineEffectiveScaleTests
{
    [Fact]
    public void EffectiveScale_WellUnderMax_ReturnsRequestedScaleUnchanged()
    {
        var bounds = new BitmapBounds { X = 0, Y = 0, Width = 100, Height = 50 };

        var effective = OcrPipeline.EffectiveScale(bounds, 2.0);

        Assert.Equal(2.0, effective, precision: 9);
    }

    [Fact]
    public void EffectiveScale_ExceedsMax_ClampsToMaxOverLargestSide()
    {
        var maxDim = OcrEngine.MaxImageDimension;
        var bounds = new BitmapBounds { X = 0, Y = 0, Width = maxDim, Height = maxDim / 2 };

        var effective = OcrPipeline.EffectiveScale(bounds, 2.0);

        Assert.Equal((double)maxDim / maxDim, effective, precision: 9); // == 1.0
    }

    [Fact]
    public void EffectiveScale_ExactlyAtMax_DoesNotClamp()
    {
        var maxDim = OcrEngine.MaxImageDimension;
        var bounds = new BitmapBounds { X = 0, Y = 0, Width = maxDim, Height = 10 };

        var effective = OcrPipeline.EffectiveScale(bounds, 1.0);

        Assert.Equal(1.0, effective, precision: 9);
    }
}
