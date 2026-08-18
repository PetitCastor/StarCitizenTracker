namespace GameCapture.Contracts;

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

    /// <summary>
    /// Validates the buffer against the declared geometry up front, so an indexing bug can
    /// never reach <see cref="AveragePatch"/>. An empty patch (width or height 0) is legal —
    /// the engine can clamp a ROI away — and samples as black.
    /// </summary>
    public PixelPatchSampler(byte[] bgra, int stride, int width, int height, int frameX, int frameY)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfNegative(stride);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        if (stride < width * 4L)
            throw new ArgumentException(
                $"stride {stride} is shorter than one row of {width} BGRA pixels.", nameof(stride));

        if (bgra.LongLength < (long)stride * height)
            throw new ArgumentException(
                $"buffer has {bgra.LongLength} bytes, needs {(long)stride * height} for " +
                $"{width}x{height} at stride {stride}.", nameof(bgra));

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
        // An empty patch has no nearest edge to clamp to (Math.Clamp throws when min > max),
        // and a ROI the engine clamped away arrives as 0x0. Black is the same answer the
        // sample loop below gives for "nothing sampled".
        if (Width <= 0 || Height <= 0)
            return (0, 0, 0);

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
