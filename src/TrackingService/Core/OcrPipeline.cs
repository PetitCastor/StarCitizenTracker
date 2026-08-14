using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace TrackingService;

/// <summary>
/// In-memory OCR service shared by all trackers: GPU frame -> SoftwareBitmap -> ROI crop +
/// upscale -> Windows OCR text. Nothing touches disk. Upscaling matters: Windows OCR misses
/// small game UI text at 1:1 (proven in the side-quest PoC).
/// </summary>
public sealed class OcrPipeline
{
    private readonly OcrEngine _engine;

    public OcrPipeline()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? throw new InvalidOperationException("No OCR language pack available.");
    }

    public string Language => _engine.RecognizerLanguage.DisplayName;

    /// <summary>Downloads a captured GPU frame into a CPU bitmap (caller disposes).</summary>
    public static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Direct3D11CaptureFrame frame)
    {
        using var premultiplied = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
            frame.Surface, BitmapAlphaMode.Premultiplied);
        return SoftwareBitmap.Convert(premultiplied, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
    }

    /// <summary>OCRs one region of an already-downloaded frame.</summary>
    public async Task<string> ReadRegionAsync(SoftwareBitmap frame, BitmapBounds roi, double scale)
    {
        using var crop = await CropAndScaleAsync(frame, roi, scale);
        var result = await _engine.RecognizeAsync(crop);
        return result.Text;
    }

    /// <summary>
    /// Crops <paramref name="bounds"/> and upscales by <paramref name="scale"/>, clamped so the
    /// result stays within the OCR engine's max image dimension. Caller disposes the result.
    /// </summary>
    public async Task<SoftwareBitmap> CropAndScaleAsync(SoftwareBitmap source, BitmapBounds bounds, double scale)
    {
        var maxDim = OcrEngine.MaxImageDimension;
        var largestSide = Math.Max(bounds.Width, bounds.Height);
        if (largestSide * scale > maxDim)
            scale = (double)maxDim / largestSide;

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
        encoder.SetSoftwareBitmap(source);
        await encoder.FlushAsync();

        var decoder = await BitmapDecoder.CreateAsync(stream);

        // BitmapTransform applies Bounds in the *scaled* coordinate space.
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)(decoder.PixelWidth * scale),
            ScaledHeight = (uint)(decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Cubic,
            Bounds = new BitmapBounds
            {
                X = (uint)(bounds.X * scale),
                Y = (uint)(bounds.Y * scale),
                Width = (uint)(bounds.Width * scale),
                Height = (uint)(bounds.Height * scale),
            },
        };

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
    }
}
