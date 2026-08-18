using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// Round-trip tests for the crop path every tracker reads through. These exist because the
/// encode/decode transform split is easy to get subtly (or catastrophically) wrong: bounds mean
/// different coordinate spaces on the encoder and the decoder, and combining bounds with a scale
/// on the encoder throws at flush time. Compiling proves nothing here — only running does.
/// </summary>
public class OcrPipelineCropAndScaleTests
{
    private const int FrameWidth = 64;
    private const int FrameHeight = 64;

    // The one red 8x8 block in an otherwise black frame.
    private static readonly BitmapBounds RedBlock =
        new() { X = 40, Y = 16, Width = 8, Height = 8 };

    private static IBuffer ToBuffer(byte[] bytes)
    {
        var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    private static byte[] ToBytes(SoftwareBitmap bitmap)
    {
        var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        bitmap.CopyToBuffer(buffer);
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>Black frame with a single pure-red block at <see cref="RedBlock"/>.</summary>
    private static SoftwareBitmap MakeFrame()
    {
        var bytes = new byte[FrameWidth * FrameHeight * 4];
        for (var y = 0; y < FrameHeight; y++)
        {
            for (var x = 0; x < FrameWidth; x++)
            {
                var inBlock = x >= (int)RedBlock.X && x < (int)(RedBlock.X + RedBlock.Width)
                           && y >= (int)RedBlock.Y && y < (int)(RedBlock.Y + RedBlock.Height);
                var i = (y * FrameWidth + x) * 4;
                bytes[i + 2] = inBlock ? (byte)255 : (byte)0;   // R
                bytes[i + 3] = 255;                              // A
            }
        }

        var frame = new SoftwareBitmap(BitmapPixelFormat.Bgra8, FrameWidth, FrameHeight, BitmapAlphaMode.Ignore);
        frame.CopyFromBuffer(ToBuffer(bytes));
        return frame;
    }

    /// <summary>Share of pixels that read as red, 0-100.</summary>
    private static int RedPercent(SoftwareBitmap bitmap)
    {
        var bytes = ToBytes(bitmap);
        var stride = bytes.Length / bitmap.PixelHeight;
        var red = 0;

        for (var y = 0; y < bitmap.PixelHeight; y++)
            for (var x = 0; x < bitmap.PixelWidth; x++)
                if (bytes[y * stride + x * 4 + 2] > 128)
                    red++;

        return red * 100 / (bitmap.PixelWidth * bitmap.PixelHeight);
    }

    [Fact]
    public async Task CropAndScale_AtOneToOne_ReturnsExactlyTheRequestedRegion()
    {
        var ocr = new OcrPipeline();
        using var frame = MakeFrame();

        using var crop = await ocr.CropAndScaleAsync(frame, RedBlock, 1.0);

        Assert.Equal(8, crop.PixelWidth);
        Assert.Equal(8, crop.PixelHeight);
        Assert.Equal(100, RedPercent(crop));
    }

    [Fact]
    public async Task CropAndScale_Upscaled_KeepsTheRegionAndMultipliesTheSize()
    {
        var ocr = new OcrPipeline();
        using var frame = MakeFrame();

        using var crop = await ocr.CropAndScaleAsync(frame, RedBlock, 4.0);

        Assert.Equal(32, crop.PixelWidth);
        Assert.Equal(32, crop.PixelHeight);
        // Cubic interpolation softens the border pixels, so allow a little bleed.
        Assert.True(RedPercent(crop) >= 95, $"expected a red crop, got {RedPercent(crop)}% red");
    }

    [Fact]
    public async Task CropAndScale_RegionAwayFromTheBlock_ContainsNoneOfIt()
    {
        // Guards the coordinate space: an ROI interpreted in scaled rather than source pixels
        // lands somewhere else entirely, and this is what would catch it.
        var ocr = new OcrPipeline();
        using var frame = MakeFrame();
        var elsewhere = new BitmapBounds { X = 0, Y = 0, Width = 8, Height = 8 };

        using var crop = await ocr.CropAndScaleAsync(frame, elsewhere, 4.0);

        Assert.Equal(0, RedPercent(crop));
    }

    [Fact]
    public async Task CropAndScale_RoiOverhangingTheFrame_ClampsInsteadOfThrowing()
    {
        var ocr = new OcrPipeline();
        using var frame = MakeFrame();
        var overhang = new BitmapBounds { X = 56, Y = 56, Width = 32, Height = 32 };

        using var crop = await ocr.CropAndScaleAsync(frame, overhang, 1.0);

        Assert.Equal(8, crop.PixelWidth);
        Assert.Equal(8, crop.PixelHeight);
    }

    [Fact]
    public async Task CropAndScale_RoiFullyOutsideTheFrame_ThrowsSomethingLegible()
    {
        var ocr = new OcrPipeline();
        using var frame = MakeFrame();
        var outside = new BitmapBounds { X = 200, Y = 200, Width = 10, Height = 10 };

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ocr.CropAndScaleAsync(frame, outside, 1.0));

        Assert.Contains("64x64", ex.Message);
    }

    [Theory]
    // Already inside: untouched.
    [InlineData(10u, 10u, 20u, 20u, 10u, 10u, 20u, 20u)]
    // Overhangs right/bottom: trimmed to the edge.
    [InlineData(56u, 48u, 32u, 32u, 56u, 48u, 8u, 16u)]
    // Origin past the edge: collapses to zero size rather than wrapping.
    [InlineData(80u, 10u, 8u, 8u, 64u, 10u, 0u, 8u)]
    public void ClampToBitmap_TrimsToTheFrame(
        uint x, uint y, uint w, uint h,
        uint expectedX, uint expectedY, uint expectedW, uint expectedH)
    {
        var clamped = OcrPipeline.ClampToBitmap(
            new BitmapBounds { X = x, Y = y, Width = w, Height = h }, FrameWidth, FrameHeight);

        Assert.Equal((expectedX, expectedY, expectedW, expectedH),
            (clamped.X, clamped.Y, clamped.Width, clamped.Height));
    }
}
