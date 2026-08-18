namespace GameCapture.Contracts;

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
    /// Projects a crop-space point back to full-frame pixels.
    /// </summary>
    /// <remarks>
    /// The scale guard is not paranoia across the wire boundary: a proto3 double defaults to 0
    /// when the engine omits effective_scale, and dividing by it yields infinity, which an
    /// unchecked cast turns into int.MinValue — a coordinate that looks like data, not like a
    /// bug. ProtoMapping rejects such results, so reaching this throw means a locally built
    /// result is malformed.
    /// </remarks>
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
