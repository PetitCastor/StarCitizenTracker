using CaptureContracts;
using CaptureContracts.Proto;
using Google.Protobuf;
using TrackerSdk;

// The proto namespace declares its own RectF (the wire mirror of the local one), so both names are
// in scope here. Alias the local one rather than fully qualifying at every word box below.
using RectF = CaptureContracts.RectF;

namespace RefineryPlugin.Tests;

/// <summary>One panel row to fabricate: a material name plus its numeric columns, at a crop-space
/// vertical center. The center is what the toggle sampler projects back to a frame row, so it is
/// stated per row rather than derived from an index.</summary>
internal readonly record struct RowSpec(string Name, int[] Numbers, double CropCenterY);

/// <summary>
/// Builds ticks the way the engine would have sent them — a <see cref="TickResult"/> through
/// <see cref="TickData.From"/> — rather than faking the SDK type. The mapping (kind checks,
/// frame_rect, effective_scale, the pixel-buffer geometry checks) is part of what the logic
/// depends on, so a hand-made shortcut around it would let a tick that could never arrive on the
/// wire pass a test.
/// </summary>
/// <remarks>
/// Every subscribed ROI is filled on every tick, because that is what the engine does: it answers
/// the whole subscribed set per frame. Fixtures are 2560x1440, so reference space == frame space
/// and the engine would have echoed each subscribed rect straight back.
/// </remarks>
internal static class TickFactory
{
    /// <summary>Orange pill fill — the REFINE toggle switched on.</summary>
    public static readonly (byte B, byte G, byte R) ToggleOn = (20, 40, 200);

    /// <summary>White knob — the REFINE toggle switched off.</summary>
    public static readonly (byte B, byte G, byte R) ToggleOff = (244, 244, 251);

    // Interlocked because xUnit runs test classes in parallel and this factory is shared.
    private static long _seq;

    public static TickData Tick(
        string panel,
        string modal = "",
        string station = "",
        string process = "",
        string footer = "",
        string yieldTotal = "",
        RowSpec[]? setupRows = null,
        RowSpec[]? yieldRows = null,
        (byte B, byte G, byte R)? toggle = null,
        bool manual = false,
        params RoiId[] erroredRois)
    {
        var proto = new TickResult
        {
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FrameSeq = (ulong)Interlocked.Increment(ref _seq),
            FrameWidth = 2560,
            FrameHeight = 1440,
            Manual = manual,
        };

        proto.Results.Add(TextResult(Rois.Panel, panel));
        proto.Results.Add(TextResult(Rois.Modal, modal));
        proto.Results.Add(TextResult(Rois.Station, station));
        proto.Results.Add(TextResult(Rois.Process, process));
        proto.Results.Add(TextResult(Rois.Footer, footer));
        proto.Results.Add(TextResult(Rois.YieldTotal, yieldTotal));
        proto.Results.Add(DetailedResult(Rois.SetupList, setupRows ?? []));
        proto.Results.Add(DetailedResult(Rois.YieldList, yieldRows ?? []));
        proto.Results.Add(PixelResult(Rois.Toggles, toggle ?? ToggleOff));

        // An engine-side ROI failure keeps its slot on the tick — the plugin has to tell it apart
        // from a successful read of an empty region — but it carries NO payload: ScanLoop's catch
        // builds a bare RoiResult so a half-filled one can never escape. Replacing the result
        // rather than flagging the filled one keeps the fixture to ticks the engine can actually
        // send; a flagged-but-populated result would let a test pass on data the plugin will never
        // see, which is the whole reason these are built through the wire type.
        for (var i = 0; i < proto.Results.Count; i++)
        {
            if (!erroredRois.Contains((RoiId)proto.Results[i].RoiId))
                continue;

            proto.Results[i] = new RoiResult
            {
                RoiId = proto.Results[i].RoiId,
                Error = true,
                ErrorMessage = "fabricated ROI failure",
            };
        }

        return TickData.From(proto);
    }

    private static RoiResult TextResult(RoiSubscription roi, string text) => new()
    {
        RoiId = roi.Id.Value,
        Kind = RoiResultKind.Text,
        FrameRect = roi.Rect.ToProto(),
        EffectiveScale = roi.Scale,
        Text = text,
    };

    /// <summary>
    /// A DETAILED result carrying one OCR line per row, each word boxed in upscaled-crop space.
    /// Name tokens sit left of the numeric columns, which is the only geometry
    /// <see cref="RefineryParser.ExtractColumnarRows"/> actually reads: it orders words by X and
    /// splits at the first numeric-looking token.
    /// </summary>
    private static RoiResult DetailedResult(RoiSubscription roi, RowSpec[] rows)
    {
        var result = new RoiResult
        {
            RoiId = roi.Id.Value,
            Kind = RoiResultKind.Detailed,
            FrameRect = roi.Rect.ToProto(),
        };
        result.FillFrom(ListResult(roi, rows));
        return result;
    }

    /// <summary>The rows as an <see cref="OcrRegionResult"/> — the shape the engine's OCR pipeline
    /// produces before it is serialised.</summary>
    private static OcrRegionResult ListResult(RoiSubscription roi, RowSpec[] rows)
    {
        const double wordHeight = 40;

        var lines = new List<OcrLineInfo>(rows.Length);
        foreach (var row in rows)
        {
            var words = new List<OcrWordInfo>();
            var y = row.CropCenterY - wordHeight / 2;

            var nameTokens = row.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < nameTokens.Length; i++)
                words.Add(new OcrWordInfo(nameTokens[i], new RectF(i * 220, y, 200, wordHeight)));

            // Numeric columns start well right of the name column so the left-to-right ordering is
            // unambiguous however many name tokens a material has.
            for (var i = 0; i < row.Numbers.Length; i++)
                words.Add(new OcrWordInfo(row.Numbers[i].ToString(),
                    new RectF(700 + i * 200, y, 150, wordHeight)));

            lines.Add(new OcrLineInfo(string.Join(' ', words.Select(w => w.Text)), words));
        }

        return new OcrRegionResult(
            string.Join(Environment.NewLine, lines.Select(l => l.Text)),
            lines,
            roi.Scale,
            roi.Rect.X, roi.Rect.Y, roi.Rect.Width, roi.Rect.Height);
    }

    /// <summary>A PIXELS result whose whole strip is one colour, at 1:1 with the frame.</summary>
    private static RoiResult PixelResult(RoiSubscription roi, (byte B, byte G, byte R) color)
    {
        var width = (int)roi.Rect.Width;
        var height = (int)roi.Rect.Height;
        var stride = width * 4;
        var bgra = new byte[stride * height];

        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = color.B;
            bgra[i + 1] = color.G;
            bgra[i + 2] = color.R;
            bgra[i + 3] = 255;
        }

        return new RoiResult
        {
            RoiId = roi.Id.Value,
            Kind = RoiResultKind.Pixels,
            FrameRect = roi.Rect.ToProto(),
            PixelsBgra = ByteString.CopyFrom(bgra),
            PixelsStride = (uint)stride,
            PixelsWidth = (uint)width,
            PixelsHeight = (uint)height,
        };
    }
}
