using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GameCapture.Engine;

/// <summary>
/// CPU-side pixel access for a small frame region, for UI elements OCR cannot read
/// (e.g. the refinery REFINE toggles): crop the region at 1:1, copy it to a byte
/// buffer once, then sample colors by frame coordinates.
/// </summary>
public sealed class PixelStrip
{
    private readonly byte[] _bgra;
    private readonly int _stride;

    public int Width { get; }
    public int Height { get; }
    public int FrameX { get; }
    public int FrameY { get; }

    /// <summary>
    /// Raw BGRA rows, for the scan loop to copy onto the wire. Not public: outside the engine the
    /// buffer is meaningless without <see cref="Stride"/>, and callers should sample through
    /// <see cref="AveragePatch"/> (or, across the boundary, PixelPatchSampler) instead.
    /// </summary>
    internal byte[] Bgra => _bgra;

    /// <summary>Bytes per row, which may exceed Width * 4 when the decoder pads rows.</summary>
    internal int Stride => _stride;

    internal PixelStrip(byte[] bgra, int stride, int width, int height, int frameX, int frameY)
    {
        _bgra = bgra;
        _stride = stride;
        Width = width;
        Height = height;
        FrameX = frameX;
        FrameY = frameY;
    }

    public static async Task<PixelStrip> CaptureAsync(OcrPipeline ocr, SoftwareBitmap frame, BitmapBounds strip)
    {
        using var crop = await ocr.CropAndScaleAsync(frame, strip, 1.0);

        var width = crop.PixelWidth;
        var height = crop.PixelHeight;
        var buffer = new Windows.Storage.Streams.Buffer((uint)(width * height * 4));
        crop.CopyToBuffer(buffer);

        var bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(bytes);
        }

        var stride = height > 0 ? bytes.Length / height : 0;
        return new PixelStrip(bytes, stride, width, height, (int)strip.X, (int)strip.Y);
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
