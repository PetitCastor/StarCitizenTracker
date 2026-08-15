using CaptureContracts.Proto;
using Google.Protobuf;

// The proto namespace also declares a RectF (the wire mirror of the local one), so both names
// are in scope here. Alias them apart rather than fully qualifying at every use site.
using ProtoRectF = CaptureContracts.Proto.RectF;
using LocalRectF = CaptureContracts.RectF;

namespace CaptureContracts;

/// <summary>
/// The ONLY place proto types are converted to and from the pure shared types. Keeping the
/// conversion in one file means the engine, the SDK and the plugins all agree on the wire
/// semantics by construction — in particular that a <see cref="RoiResult"/>'s frame_rect is
/// what <see cref="OcrRegionResult"/> treats as its ROI origin, so ToFramePoint keeps
/// yielding real frame pixels on the far side of the boundary.
/// </summary>
public static class ProtoMapping
{
    /// <summary>Reference- or frame-space rectangle to its wire form.</summary>
    public static Rect ToProto(this RoiRect r)
        => new() { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };

    /// <summary>Wire rectangle back to the plain struct.</summary>
    public static RoiRect ToRoiRect(this Rect r)
        => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>
    /// Copies OCR content into an existing <see cref="RoiResult"/>. roi_id and frame_rect stay
    /// the caller's business: only the engine knows which subscription the result answers and
    /// which frame-space rect it actually read.
    /// </summary>
    public static void FillFrom(this RoiResult target, OcrRegionResult source)
    {
        target.Text = source.Text;
        target.EffectiveScale = source.EffectiveScale;

        target.Lines.Clear();
        foreach (var line in source.Lines)
        {
            var protoLine = new OcrLine { Text = line.Text };
            foreach (var word in line.Words)
            {
                protoLine.Words.Add(new OcrWord
                {
                    Text = word.Text,
                    CropRect = new ProtoRectF
                    {
                        X = word.CropRect.X,
                        Y = word.CropRect.Y,
                        Width = word.CropRect.Width,
                        Height = word.CropRect.Height,
                    },
                });
            }
            target.Lines.Add(protoLine);
        }
    }

    /// <summary>
    /// Wire result back to an <see cref="OcrRegionResult"/>. RoiX/Y/Width/Height come from
    /// frame_rect, so <see cref="OcrRegionResult.ToFramePoint"/> yields real frame pixels —
    /// identical semantics to the monolith, where ReadRegionDetailedAsync received the
    /// frame-space rect.
    /// </summary>
    public static OcrRegionResult ToOcrRegionResult(this RoiResult r)
    {
        var rect = r.FrameRect ?? new Rect();

        var lines = new List<OcrLineInfo>(r.Lines.Count);
        foreach (var line in r.Lines)
        {
            var words = new List<OcrWordInfo>(line.Words.Count);
            foreach (var word in line.Words)
            {
                var box = word.CropRect;
                words.Add(new OcrWordInfo(
                    word.Text,
                    box is null ? default : new LocalRectF(box.X, box.Y, box.Width, box.Height)));
            }
            lines.Add(new OcrLineInfo(line.Text, words));
        }

        return new OcrRegionResult(r.Text, lines, r.EffectiveScale,
            rect.X, rect.Y, rect.Width, rect.Height);
    }

    /// <summary>
    /// Wire result of a ROI_MODE_PIXELS subscription to a sampler. FrameX/Y come from the
    /// frame_rect origin so callers keep addressing pixels in frame coordinates.
    /// </summary>
    public static PixelPatchSampler ToPixelSampler(this RoiResult r)
    {
        var rect = r.FrameRect ?? new Rect();

        return new PixelPatchSampler(
            r.PixelsBgra.ToByteArray(),
            (int)r.PixelsStride,
            (int)r.PixelsWidth,
            (int)r.PixelsHeight,
            (int)rect.X,
            (int)rect.Y);
    }
}
