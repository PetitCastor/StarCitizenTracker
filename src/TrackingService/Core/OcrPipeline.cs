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
    /// OCRs one region keeping per-word geometry, for table-shaped UI where column layout
    /// matters. Word rects are in upscaled-crop space; the result records the scale that
    /// was actually applied so callers can map back to frame pixels.
    /// </summary>
    public async Task<OcrRegionResult> ReadRegionDetailedAsync(SoftwareBitmap frame, BitmapBounds roi, double scale)
    {
        var effective = EffectiveScale(roi, scale);
        using var crop = await CropAndScaleAsync(frame, roi, scale);
        var result = await _engine.RecognizeAsync(crop);

        var lines = new List<OcrLineInfo>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = new List<OcrWordInfo>(line.Words.Count);
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                words.Add(new OcrWordInfo(word.Text, new RectF(r.X, r.Y, r.Width, r.Height)));
            }
            lines.Add(new OcrLineInfo(line.Text, words));
        }

        return new OcrRegionResult(result.Text, lines, effective, roi.X, roi.Y, roi.Width, roi.Height);
    }

    /// <summary>The scale actually applied after clamping to the OCR engine's max dimension.</summary>
    public static double EffectiveScale(BitmapBounds bounds, double scale)
    {
        var maxDim = OcrEngine.MaxImageDimension;
        var largestSide = Math.Max(bounds.Width, bounds.Height);
        return largestSide * scale > maxDim ? (double)maxDim / largestSide : scale;
    }

    /// <summary>
    /// Crops <paramref name="bounds"/> and upscales by <paramref name="scale"/>, clamped so the
    /// result stays within the OCR engine's max image dimension. Caller disposes the result.
    /// </summary>
    public async Task<SoftwareBitmap> CropAndScaleAsync(SoftwareBitmap source, BitmapBounds bounds, double scale)
    {
        scale = EffectiveScale(bounds, scale);

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
