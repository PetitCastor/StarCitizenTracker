using CaptureContracts;
using TrackerSdk;
using TrackerSdk.Testing;

namespace RefineryPlugin.Tests;

/// <summary>One panel row to fabricate: a material name plus its numeric columns, at a crop-space
/// vertical center. The center is what the toggle sampler projects back to a frame row, so it is
/// stated per row rather than derived from an index.</summary>
internal readonly record struct RowSpec(string Name, int[] Numbers, double CropCenterY);

/// <summary>
/// Builds refinery ticks through <see cref="TickDataBuilder"/> — the SDK's public, wire-faithful
/// factory — rather than the private wire-type shortcut the old <c>TickFactory</c> used (which
/// reached <c>TickData.From</c> through an InternalsVisibleTo grant that died with TASK-11). Every
/// subscribed ROI is filled on every tick, because that is what the engine does: it answers the whole
/// subscribed set per frame. Frames are the reference 2560x1440, so reference space == frame space.
/// </summary>
internal static class RefineryTicks
{
    /// <summary>Orange pill fill — the REFINE toggle switched on.</summary>
    public static readonly (byte B, byte G, byte R) ToggleOn = (20, 40, 200);

    /// <summary>White knob — the REFINE toggle switched off.</summary>
    public static readonly (byte B, byte G, byte R) ToggleOff = (244, 244, 251);

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
        var errored = new HashSet<RoiId>(erroredRois);
        var b = new TickDataBuilder();

        AddText(b, errored, Rois.Panel.Id, panel);
        AddText(b, errored, Rois.Modal.Id, modal);
        AddText(b, errored, Rois.Station.Id, station);
        AddText(b, errored, Rois.Process.Id, process);
        AddText(b, errored, Rois.Footer.Id, footer);
        AddText(b, errored, Rois.YieldTotal.Id, yieldTotal);
        AddDetailed(b, errored, Rois.SetupList.Id, setupRows ?? []);
        AddDetailed(b, errored, Rois.YieldList.Id, yieldRows ?? []);
        AddPixels(b, errored, Rois.Toggles.Id, toggle ?? ToggleOff);

        if (manual)
            b.Manual();

        return b.Build();
    }

    // An engine-side ROI failure keeps its slot on the tick — the plugin has to tell it apart from a
    // successful read of an empty region — but it carries NO payload: TickDataBuilder.Errored builds
    // the same bare RoiResult the engine's ScanLoop catch does.
    private static void AddText(TickDataBuilder b, HashSet<RoiId> errored, RoiId id, string text)
    {
        if (errored.Contains(id))
            b.Errored(id, "fabricated ROI failure");
        else
            b.Text(id, text);
    }

    private static void AddDetailed(TickDataBuilder b, HashSet<RoiId> errored, RoiId id, RowSpec[] rows)
    {
        if (errored.Contains(id))
        {
            b.Errored(id, "fabricated ROI failure");
            return;
        }

        b.Detailed(id, rows.Select(ToLine).ToArray());
    }

    /// <summary>
    /// One OCR line per row, each word boxed in upscaled-crop space. Name tokens sit left of the
    /// numeric columns, which is the only geometry <see cref="RefineryParser.ExtractColumnarRows"/>
    /// actually reads: it orders words by X and splits at the first numeric-looking token.
    /// </summary>
    private static OcrLineSpec ToLine(RowSpec row)
    {
        const double wordHeight = 40;
        var y = row.CropCenterY - wordHeight / 2;

        var words = new List<OcrWordSpec>();

        var nameTokens = row.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < nameTokens.Length; i++)
            words.Add(new OcrWordSpec(nameTokens[i], new RectF(i * 220, y, 200, wordHeight)));

        // Numeric columns start well right of the name column so left-to-right ordering is
        // unambiguous however many name tokens a material has.
        for (var i = 0; i < row.Numbers.Length; i++)
            words.Add(new OcrWordSpec(row.Numbers[i].ToString(), new RectF(700 + i * 200, y, 150, wordHeight)));

        return new OcrLineSpec(words.ToArray());
    }

    // A solid-colour strip at the real toggle ROI's 40x250, the shape the colour probe reads. Solid,
    // so the exact frame row the sampler clamps to is immaterial to the colour it returns.
    private static void AddPixels(TickDataBuilder b, HashSet<RoiId> errored, RoiId id, (byte B, byte G, byte R) color)
    {
        if (errored.Contains(id))
        {
            b.Errored(id, "fabricated ROI failure");
            return;
        }

        b.Pixels(id, color.B, color.G, color.R, 40, 250);
    }
}
