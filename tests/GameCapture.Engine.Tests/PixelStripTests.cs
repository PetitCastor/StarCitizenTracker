using Xunit;

namespace GameCapture.Engine.Tests;

public class PixelStripTests
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
    public void AveragePatch_UniformColor_ReturnsExactColor()
    {
        var bgra = BuildBgra(10, 10, (_, _) => (B: (byte)10, G: (byte)20, R: (byte)30));
        var strip = new PixelStrip(bgra, stride: 40, width: 10, height: 10, frameX: 0, frameY: 0);

        var (b, g, r) = strip.AveragePatch(frameX: 5, frameY: 5, radius: 2);

        Assert.Equal((10, 20, 30), (b, g, r));
    }

    [Fact]
    public void AveragePatch_AtColorBoundary_AveragesAcrossBoth()
    {
        // Left two columns R=0, right two columns R=100. Sampling at (1,1) radius 1
        // covers columns 0,1,2 (2 cols of R=0, 1 col of R=100), 3 rows each -> 9 samples.
        var bgra = BuildBgra(4, 4, (x, _) => x < 2 ? ((byte)0, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)100));
        var strip = new PixelStrip(bgra, stride: 16, width: 4, height: 4, frameX: 0, frameY: 0);

        var (_, _, r) = strip.AveragePatch(frameX: 1, frameY: 1, radius: 1);

        Assert.Equal(33, r); // (6*0 + 3*100) / 9 = 33 (integer division)
    }

    [Fact]
    public void AveragePatch_ClampsAtCorner_DoesNotSampleOutOfBounds()
    {
        var bgra = BuildBgra(4, 4, (x, _) => x < 2 ? ((byte)0, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)100));
        var strip = new PixelStrip(bgra, stride: 16, width: 4, height: 4, frameX: 0, frameY: 0);

        // Corner (0,0) radius 1 clamps to x in [0,1], y in [0,1] -> all R=0 samples only.
        var (_, _, r) = strip.AveragePatch(frameX: 0, frameY: 0, radius: 1);

        Assert.Equal(0, r);
    }

    [Fact]
    public void AveragePatch_UsesFrameOffsetToLocalizeCoordinates()
    {
        var bgra = BuildBgra(4, 4, (x, y) => x == 1 && y == 1 ? ((byte)1, (byte)2, (byte)3) : ((byte)0, (byte)0, (byte)0));
        var strip = new PixelStrip(bgra, stride: 16, width: 4, height: 4, frameX: 100, frameY: 200);

        var (b, g, r) = strip.AveragePatch(frameX: 101, frameY: 201, radius: 0);

        Assert.Equal((1, 2, 3), (b, g, r));
    }
}
