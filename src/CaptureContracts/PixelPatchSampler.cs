namespace CaptureContracts;

/// <summary>
/// CPU-side pixel access over a raw BGRA buffer for a small frame region (e.g. the refinery
/// REFINE toggles). The buffer arrives from the engine's ROI_MODE_PIXELS result; sampling is
/// by frame coordinates, exactly like the monolith's PixelStrip.
/// </summary>
public sealed class PixelPatchSampler
{
    private readonly byte[] _bgra;
    private readonly int _stride;

    public int Width { get; }
    public int Height { get; }
    public int FrameX { get; }
    public int FrameY { get; }

    public PixelPatchSampler(byte[] bgra, int stride, int width, int height, int frameX, int frameY)
    {
        _bgra = bgra;
        _stride = stride;
        Width = width;
        Height = height;
        FrameX = frameX;
        FrameY = frameY;
    }

    /// <summary>
    /// Average BGRA color of a square patch centered on a frame-space point, clamped to the
    /// strip. Averaging survives antialiasing and the game's film grain; a single pixel does not.
    /// </summary>
    public (byte B, byte G, byte R) AveragePatch(int frameX, int frameY, int radius = 3)
    {
        var cx = Math.Clamp(frameX - FrameX, 0, Width - 1);
        var cy = Math.Clamp(frameY - FrameY, 0, Height - 1);

        long b = 0, g = 0, r = 0, n = 0;
        for (var y = Math.Max(0, cy - radius); y <= Math.Min(Height - 1, cy + radius); y++)
        {
            for (var x = Math.Max(0, cx - radius); x <= Math.Min(Width - 1, cx + radius); x++)
            {
                var i = y * _stride + x * 4;
                b += _bgra[i];
                g += _bgra[i + 1];
                r += _bgra[i + 2];
                n++;
            }
        }

        return n == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)(b / n), (byte)(g / n), (byte)(r / n));
    }
}
