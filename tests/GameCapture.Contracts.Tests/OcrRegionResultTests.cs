using GameCapture.Contracts;
using Xunit;

namespace GameCapture.Contracts.Tests;

public class OcrRegionResultTests
{
    [Fact]
    public void RectF_ComputesDerivedProperties()
    {
        var rect = new RectF(X: 10, Y: 20, Width: 30, Height: 40);

        Assert.Equal(40, rect.Right);
        Assert.Equal(60, rect.Bottom);
        Assert.Equal(25, rect.CenterX);
        Assert.Equal(40, rect.CenterY);
    }

    [Fact]
    public void ToFramePoint_MapsUsingEffectiveScale()
    {
        var region = new OcrRegionResult("", [], EffectiveScale: 2.0, RoiX: 100, RoiY: 50, RoiWidth: 200, RoiHeight: 100);

        var (x, y) = region.ToFramePoint(20, 10);

        Assert.Equal(110, x); // 100 + 20/2
        Assert.Equal(55, y);  // 50 + 10/2
    }

    [Fact]
    public void ToFramePoint_TruncatesTowardsZero()
    {
        var region = new OcrRegionResult("", [], EffectiveScale: 2.0, RoiX: 100, RoiY: 50, RoiWidth: 200, RoiHeight: 100);

        var (x, _) = region.ToFramePoint(21, 0); // 100 + 10.5 -> truncates to 110

        Assert.Equal(110, x);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    public void ToFramePoint_NonPositiveScale_Throws(double scale)
    {
        // Dividing by 0 yields infinity, and the unchecked cast turns that into int.MinValue —
        // a coordinate that looks like data rather than a bug.
        var region = new OcrRegionResult("", [], scale, RoiX: 100, RoiY: 50, RoiWidth: 200, RoiHeight: 100);

        Assert.Throws<InvalidOperationException>(() => region.ToFramePoint(20, 10));
    }

    [Fact]
    public void CropWidthAndHeight_ScaleTheRoi()
    {
        var region = new OcrRegionResult("", [], EffectiveScale: 1.5, RoiX: 0, RoiY: 0, RoiWidth: 200, RoiHeight: 100);

        Assert.Equal(300, region.CropWidth);
        Assert.Equal(150, region.CropHeight);
    }

    [Fact]
    public void AllWords_FlattensLinesInOrder()
    {
        var w1 = new OcrWordInfo("Foo", new RectF(0, 0, 10, 10));
        var w2 = new OcrWordInfo("Bar", new RectF(20, 0, 10, 10));
        var w3 = new OcrWordInfo("Baz", new RectF(0, 20, 10, 10));

        var region = new OcrRegionResult(
            "Foo Bar Baz",
            [new OcrLineInfo("Foo Bar", [w1, w2]), new OcrLineInfo("Baz", [w3])],
            EffectiveScale: 1, RoiX: 0, RoiY: 0, RoiWidth: 100, RoiHeight: 100);

        Assert.Equal([w1, w2, w3], region.AllWords());
    }
}
