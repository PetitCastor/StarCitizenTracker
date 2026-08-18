using GameCapture.Contracts;
using Xunit;

namespace GameCapture.Contracts.Tests;

/// <summary>Parity with the monolith's PixelStripTests, on the buffer-only port.</summary>
public class PixelPatchSamplerTests
{
    private static byte[] BuildBgra(int width, int height, Func<int, int, (byte B, byte G, byte R)> colorAt)
    {
        var stride = width * 4;
        var buffer = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (b, g, r) = colorAt(x, y);
                var i = y * stride + x * 4;
                buffer[i] = b;
                buffer[i + 1] = g;
                buffer[i + 2] = r;
                buffer[i + 3] = 255;
            }
        }
        return buffer;
    }

    [Fact]
    public void AveragePatch_InsideColoredSquare_ReturnsExactColor()
    {
        // 8x8 black buffer with a solid (10,20,30) square covering x/y in [2,5].
        var bgra = BuildBgra(8, 8, (x, y) =>
            x is >= 2 and <= 5 && y is >= 2 and <= 5
                ? ((byte)10, (byte)20, (byte)30)
                : ((byte)0, (byte)0, (byte)0));
        var sampler = new PixelPatchSampler(bgra, stride: 32, width: 8, height: 8, frameX: 0, frameY: 0);

        // Radius 1 around the square's center stays wholly inside it.
        var (b, g, r) = sampler.AveragePatch(frameX: 3, frameY: 3, radius: 1);

        Assert.Equal(((byte)10, (byte)20, (byte)30), (b, g, r));
    }

    [Fact]
    public void AveragePatch_AtColorBoundary_AveragesAcrossBoth()
    {
        // Left two columns R=0, right two columns R=100. Sampling at (1,1) radius 1
        // covers columns 0,1,2 (2 cols of R=0, 1 col of R=100), 3 rows each -> 9 samples.
        var bgra = BuildBgra(4, 4, (x, _) => x < 2 ? ((byte)0, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)100));
        var sampler = new PixelPatchSampler(bgra, stride: 16, width: 4, height: 4, frameX: 0, frameY: 0);

        var (_, _, r) = sampler.AveragePatch(frameX: 1, frameY: 1, radius: 1);

        Assert.Equal(33, r); // (6*0 + 3*100) / 9 = 33 (integer division)
    }

    [Fact]
    public void AveragePatch_ClampsAtCorner_DoesNotSampleOutOfBounds()
    {
        var bgra = BuildBgra(4, 4, (x, _) => x < 2 ? ((byte)0, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)100));
        var sampler = new PixelPatchSampler(bgra, stride: 16, width: 4, height: 4, frameX: 0, frameY: 0);

        // Corner (0,0) radius 1 clamps to x in [0,1], y in [0,1] -> all R=0 samples only.
        var (_, _, r) = sampler.AveragePatch(frameX: 0, frameY: 0, radius: 1);

        Assert.Equal(0, r);
    }

    [Fact]
    public void AveragePatch_PointOutsideBuffer_ClampsToNearestEdge()
    {
        // 8x8 with the right half at R=200; a request far past the right edge clamps into it.
        var bgra = BuildBgra(8, 8, (x, _) => x >= 4 ? ((byte)0, (byte)0, (byte)200) : ((byte)0, (byte)0, (byte)0));
        var sampler = new PixelPatchSampler(bgra, stride: 32, width: 8, height: 8, frameX: 0, frameY: 0);

        var (_, _, r) = sampler.AveragePatch(frameX: 999, frameY: 999, radius: 0);

        Assert.Equal(200, r);
    }

    [Fact]
    public void AveragePatch_OnEmptyPatch_ReturnsBlackInsteadOfThrowing()
    {
        // Clamping into a 0-wide patch is Math.Clamp(v, 0, -1), which throws. A ROI the engine
        // clamped away arrives as 0x0 and must take the same "nothing sampled" path as an
        // out-of-buffer request.
        var sampler = new PixelPatchSampler([], stride: 0, width: 0, height: 0, frameX: 100, frameY: 200);

        Assert.Equal(((byte)0, (byte)0, (byte)0), sampler.AveragePatch(frameX: 100, frameY: 200));
    }

    [Fact]
    public void Constructor_WithBufferSmallerThanGeometry_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new PixelPatchSampler(new byte[8], stride: 8, width: 2, height: 2, frameX: 0, frameY: 0));
    }

    [Fact]
    public void Constructor_WithStrideShorterThanARow_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new PixelPatchSampler(new byte[32], stride: 4, width: 2, height: 2, frameX: 0, frameY: 0));
    }

    [Fact]
    public void Constructor_WithNegativeGeometry_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PixelPatchSampler(new byte[16], stride: 16, width: -1, height: 1, frameX: 0, frameY: 0));
    }

    [Fact]
    public void AveragePatch_UsesFrameOffsetToLocalizeCoordinates()
    {
        var bgra = BuildBgra(4, 4, (x, y) => x == 1 && y == 1 ? ((byte)1, (byte)2, (byte)3) : ((byte)0, (byte)0, (byte)0));
        var sampler = new PixelPatchSampler(bgra, stride: 16, width: 4, height: 4, frameX: 100, frameY: 200);

        var (b, g, r) = sampler.AveragePatch(frameX: 101, frameY: 201, radius: 0);

        Assert.Equal(((byte)1, (byte)2, (byte)3), (b, g, r));
    }
}
