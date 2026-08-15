namespace TrackingService;

/// <summary>Plain rectangle in upscaled-crop pixel space (no WinRT types so parsers stay testable).</summary>
public readonly record struct RectF(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>One recognized word with its bounding box in upscaled-crop space.</summary>
public sealed record OcrWordInfo(string Text, RectF CropRect);

/// <summary>One recognized line. WinRT gives boxes per word only; a line box is the union of its words.</summary>
public sealed record OcrLineInfo(string Text, IReadOnlyList<OcrWordInfo> Words);

/// <summary>
/// Geometry-preserving OCR result for one ROI. All word rects are in the upscaled-crop
/// coordinate space; <see cref="ToFramePoint"/> maps back to full-frame pixels using the
/// scale that was actually applied (the pipeline clamps the requested scale).
/// </summary>
public sealed record OcrRegionResult(
    string Text,
    IReadOnlyList<OcrLineInfo> Lines,
    double EffectiveScale,
    uint RoiX, uint RoiY, uint RoiWidth, uint RoiHeight)
{
    public double CropWidth => RoiWidth * EffectiveScale;
    public double CropHeight => RoiHeight * EffectiveScale;

    /// <summary>
    /// Projects a crop-space point back to full-frame pixels. A non-positive scale would divide
    /// to infinity and the unchecked cast would yield int.MinValue — a coordinate that looks
    /// like data, not like a bug. Kept in lockstep with CaptureContracts.OcrRegionResult until
    /// ENGINE-SPLIT TASK-8 deletes this copy.
    /// </summary>
    public (int X, int Y) ToFramePoint(double cropX, double cropY)
    {
        if (!(EffectiveScale > 0))
            throw new InvalidOperationException(
                $"EffectiveScale must be > 0 to project crop coordinates (was {EffectiveScale}).");

        return ((int)(RoiX + cropX / EffectiveScale), (int)(RoiY + cropY / EffectiveScale));
    }

    public IEnumerable<OcrWordInfo> AllWords()
    {
        foreach (var line in Lines)
            foreach (var word in line.Words)
                yield return word;
    }
}
