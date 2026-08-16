using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CaptureContracts;

namespace RefineryPlugin;

/// <summary>One material row of a work order. Quantities in SCU (screen shows cSCU).</summary>
public sealed record MaterialRow(string Name, decimal QtyScu, decimal YieldScu, bool RefineOn);

/// <summary>One committed refinery work order.</summary>
public sealed record RefineryWorkOrder(
    string Station, string Process, string TotalCost, string ProcessingTime,
    IReadOnlyList<MaterialRow> Materials)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Station:         {Station}");
        sb.AppendLine($"Process:         {Process}");
        sb.AppendLine($"Total cost:      {TotalCost}");
        sb.AppendLine($"Processing time: {ProcessingTime}");
        sb.AppendLine($"Materials ({Materials.Count}):");
        foreach (var m in Materials)
            sb.AppendLine($"  {m.Name,-24} qty {m.QtyScu,8:0.00} SCU  yield {m.YieldScu,8:0.00} SCU  {(m.RefineOn ? "REFINE" : "skip")}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>A materials-list row as parsed from one frame, before toggle sampling.</summary>
public sealed record ParsedRow(string Name, decimal QtyScu, decimal YieldScu, double CropCenterY);

/// <summary>
/// A row parsed in raw integer cSCU — the unit the COMPLETED/PROCESSING panels and the ledger use.
/// Kept distinct from <see cref="ParsedRow"/>'s decimal SCU so the completed-panel path never does a
/// lossy /100 round-trip. <see cref="QtyCscu"/> is 0 for the two-column NAME/YIELD panels.
/// </summary>
public sealed record ParsedRowCscu(string Name, int QtyCscu, int YieldCscu, double CropCenterY);

/// <summary>
/// Result of a two-column yield-row extraction. The dropped-edge counts are the secondary
/// truncation signal: a row clipped at the top or bottom of the ROI OCRs as garbage and is dropped,
/// so a non-zero count means the visible list did not start/end cleanly and the read may be partial.
/// </summary>
public sealed record ExtractResult(IReadOnlyList<ParsedRowCscu> Rows, int DroppedTopEdge, int DroppedBottomEdge);

/// <summary>
/// One refinery-panel row parsed generically: a name plus its numeric columns left-to-right, each
/// nullable (a <c>--</c> placeholder or an unparseable token becomes null). The three panels differ
/// in their columns (SETUP: quality/qty/yield; PROCESSING: quality/yield/todo/done; COMPLETED:
/// quality/yield), so the caller indexes <see cref="Numbers"/> per panel rather than the parser
/// hard-coding a layout.
/// </summary>
public sealed record ColumnarRow(string Name, IReadOnlyList<int?> Numbers, double CropCenterY);

/// <summary>Rows plus the edge-clip truncation counts (see <see cref="ExtractResult"/>).</summary>
public sealed record ColumnarResult(IReadOnlyList<ColumnarRow> Rows, int DroppedTopEdge, int DroppedBottomEdge);

/// <summary>
/// Pure parsing for the refinery SETUP screen — no WinRT types, so it can run offline
/// against replayed OCR results. Rows are reconstructed from word geometry because
/// Windows OCR splits/merges lines unpredictably across the wide column gaps.
/// </summary>
public static partial class RefineryParser
{
    // Name column, then QTY and YIELD numeric columns. Numeric classes tolerate the usual
    // OCR digit confusions; NormalizeNumber repairs them before parsing.
    [GeneratedRegex(@"^(?<name>[A-Za-z][A-Za-z()\-'’ ]*?)\s+(?<qty>[0-9OolIiSsB,.]{1,12})\s+(?<yield>[0-9OolIiSsB,.]{1,12})$")]
    private static partial Regex RowPattern();

    [GeneratedRegex(@"(?<p>[A-Z][A-Za-z]+\s+Process)", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessPattern();

    [GeneratedRegex(@"(?<c>[\d,.OolIS]*\d[\d,.OolIS]*)\s*aUEC", RegexOptions.IgnoreCase)]
    private static partial Regex CostPattern();

    // Two in-game formats seen: "33m 45s" (optionally with hours) and "03:12:36".
    [GeneratedRegex(@"(?<t>\d{1,2}:\d{2}:\d{2}|(?:\d+\s*h\s*)?\d+\s*m\s*\d+\s*s)", RegexOptions.IgnoreCase)]
    private static partial Regex TimePattern();

    // COMPLETED / PROCESSING panels: "MATERIALS YIELDED (CSCU)" — a NAME column and a single YIELD
    // column, no QTY. Same numeric class + OCR-confusion tolerance as RowPattern.
    [GeneratedRegex(@"^(?<name>[A-Za-z][A-Za-z()\-'’ ]*?)\s+(?<yield>[0-9OolIiSsB,.]{1,12})$")]
    private static partial Regex YieldRowPattern();

    // The COMPLETED-panel checksum line, e.g. "YIELD 644".
    [GeneratedRegex(@"YIELD\s*[:\-]?\s*(?<v>[0-9OolIiSsB.,]{1,12})", RegexOptions.IgnoreCase)]
    private static partial Regex YieldTotalPattern();

    // Slot index, e.g. "WORK ORDER 1" — logging/disambiguation only, never an identity key.
    [GeneratedRegex(@"WORK\s*ORDER\s*(?<n>[0-9OolIiSsB]{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex WorkOrderIndexPattern();

    /// <summary>
    /// Clusters the region's words into visual rows by vertical center, then parses each as
    /// NAME QTY YIELD. Clusters touching the ROI's top/bottom edge are discarded — those are
    /// partially scrolled rows that OCR as garbage. Unparseable clusters are skipped; the
    /// caller re-reads at ~2 Hz, so a later clean read repairs them.
    /// </summary>
    public static IReadOnlyList<ParsedRow> ExtractRows(OcrRegionResult list, double edgeMarginFramePx = 10)
    {
        var words = list.AllWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0)
            return [];

        var heights = words.Select(w => w.CropRect.Height).OrderBy(h => h).ToList();
        var medianHeight = heights[heights.Count / 2];
        var tolerance = Math.Max(2, medianHeight * 0.6);

        var rows = new List<ParsedRow>();
        var margin = edgeMarginFramePx * list.EffectiveScale;

        foreach (var cluster in ClusterByCenterY(words, tolerance))
        {
            var top = cluster.Min(w => w.CropRect.Y);
            var bottom = cluster.Max(w => w.CropRect.Bottom);
            if (top < margin || bottom > list.CropHeight - margin)
                continue;

            var text = string.Join(' ', cluster.OrderBy(w => w.CropRect.X).Select(w => w.Text)).Trim();
            var match = RowPattern().Match(text);
            if (!match.Success)
                continue;

            if (!TryParseCscu(match.Groups["qty"].Value, out var qtyCscu) ||
                !TryParseCscu(match.Groups["yield"].Value, out var yieldCscu))
                continue;

            var name = NormalizeName(match.Groups["name"].Value);
            var centerY = cluster.Average(w => w.CropRect.CenterY);
            rows.Add(new ParsedRow(name, qtyCscu / 100m, yieldCscu / 100m, centerY));
        }

        return rows;
    }

    private static IEnumerable<List<OcrWordInfo>> ClusterByCenterY(List<OcrWordInfo> words, double tolerance)
    {
        var cluster = new List<OcrWordInfo>();
        var clusterY = 0.0;

        foreach (var word in words.OrderBy(w => w.CropRect.CenterY))
        {
            if (cluster.Count > 0 && Math.Abs(word.CropRect.CenterY - clusterY) > tolerance)
            {
                yield return cluster;
                cluster = [];
            }
            cluster.Add(word);
            clusterY = cluster.Average(w => w.CropRect.CenterY);
        }

        if (cluster.Count > 0)
            yield return cluster;
    }

    /// <summary>Uppercased, whitespace-collapsed dictionary key ("Titanium (Ore)" == "TITANIUM (ORE)").</summary>
    public static string NormalizeName(string raw)
        => string.Join(' ', raw.Trim().Trim('.', ',', ':', '-').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

    /// <summary>
    /// Material identity name with the ore-form suffix stripped, so the same material reads the same
    /// across panels: SETUP shows "TORITE (ORE)" while PROCESSING/COMPLETED show just "TORITE".
    /// Combined with the quality value it distinguishes two batches of the same material.
    /// </summary>
    public static string BaseName(string name)
    {
        var normalized = NormalizeName(name);
        var paren = normalized.IndexOf('(');
        return paren > 0 ? normalized[..paren].Trim() : normalized;
    }

    /// <summary>
    /// Generic row extraction for any refinery panel: clusters words into visual rows, splits each
    /// into a leading name and its numeric columns. Shares the clustering and edge-clip handling with
    /// <see cref="ExtractYieldRows"/>, counting (not silently dropping) edge-touching rows.
    /// </summary>
    public static ColumnarResult ExtractColumnarRows(OcrRegionResult list, double edgeMarginFramePx = 10)
    {
        var words = list.AllWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0)
            return new ColumnarResult([], 0, 0);

        var heights = words.Select(w => w.CropRect.Height).OrderBy(h => h).ToList();
        var medianHeight = heights[heights.Count / 2];
        var tolerance = Math.Max(2, medianHeight * 0.6);

        var rows = new List<ColumnarRow>();
        var margin = edgeMarginFramePx * list.EffectiveScale;
        var droppedTop = 0;
        var droppedBottom = 0;

        foreach (var cluster in ClusterByCenterY(words, tolerance))
        {
            var top = cluster.Min(w => w.CropRect.Y);
            var bottom = cluster.Max(w => w.CropRect.Bottom);
            if (top < margin)
            {
                droppedTop++;
                continue;
            }
            if (bottom > list.CropHeight - margin)
            {
                droppedBottom++;
                continue;
            }

            var tokens = cluster.OrderBy(w => w.CropRect.X).Select(w => w.Text).ToList();
            var nameParts = new List<string>();
            var numbers = new List<int?>();
            var inNumbers = false;

            foreach (var token in tokens)
            {
                if (!inNumbers && !LooksNumeric(token))
                {
                    nameParts.Add(token);
                    continue;
                }
                inNumbers = true;
                numbers.Add(TryParseCscu(token, out var v) ? ClampCscu(v) : null);
            }

            var name = NormalizeName(string.Join(' ', nameParts));
            if (name.Length == 0 || numbers.Count == 0)
                continue;

            rows.Add(new ColumnarRow(name, numbers, cluster.Average(w => w.CropRect.CenterY)));
        }

        return new ColumnarResult(rows, droppedTop, droppedBottom);
    }

    // A numeric-column token: the "--" placeholder, or a token that is mostly digits (tolerating the
    // usual OCR digit confusions). Material-name tokens like "(ORE)" or "CORUNDUM" are not.
    private static bool LooksNumeric(string token)
    {
        if (token is "--" or "—" or "-")
            return true;
        var core = token.Trim('(', ')', '%', ',', '.');
        if (core.Length == 0)
            return false;
        // Mostly digit-ish (tolerating OCR letter/digit confusions, e.g. "S,OOl" -> 5001). Names like
        // "ORE"/"GOLD"/"IRON" have at most one digit-ish letter over their length, so they fall through.
        var digitish = core.Count(c => char.IsDigit(c) || "OolIiSsB".Contains(c));
        return digitish >= core.Length - 1;
    }

    /// <summary>Repairs common OCR digit confusions and parses an integer cSCU value.</summary>
    public static bool TryParseCscu(string token, out decimal value)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var c in token)
        {
            var mapped = c switch
            {
                'O' or 'o' => '0',
                'l' or 'I' or 'i' => '1',
                'S' or 's' => '5',
                'B' => '8',
                ',' or '.' or ' ' => '\0', // thousands separators / OCR specks
                _ => c,
            };
            if (mapped != '\0')
                sb.Append(mapped);
        }

        return decimal.TryParse(sb.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Upper bound of a sane cSCU reading — generously above anything a real refinery order
    /// could ever show, so this only ever rejects OCR garbage, never a genuine value.</summary>
    private const decimal MaxSaneCscu = 10_000_000m;

    /// <summary>
    /// Narrows an OCR-parsed <see cref="TryParseCscu"/> decimal to <c>int</c>, or <c>null</c> if it's
    /// outside a sane cSCU range. The numeric-token regexes admit up to 12 digits (to tolerate stray
    /// OCR noise glued onto a real number), so an unchecked <c>(int)</c> cast on a fully-garbage
    /// 12-digit token (~1e12) throws <see cref="OverflowException"/> — silently killing the tracker in
    /// live mode (caught per-tick by the plugin's tick loop, but the tracker then stops updating) and
    /// aborting replay outright. Garbage in, unparsed out: treat it exactly like any other unparseable
    /// token.
    /// </summary>
    public static int? ClampCscu(decimal value)
        => value >= 0 && value <= MaxSaneCscu ? (int)value : null;

    /// <summary>First non-empty line of the station-header ROI, verbatim.</summary>
    public static string? ParseStation(string headerText)
        => headerText.Split('\r', '\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 3);

    public static string? ParseProcess(string text)
    {
        var m = ProcessPattern().Match(text);
        return m.Success ? m.Groups["p"].Value : null;
    }

    public static string? ParseCost(string text)
    {
        var m = CostPattern().Match(text);
        return m.Success ? $"{m.Groups["c"].Value} aUEC" : null;
    }

    public static string? ParseTime(string text)
    {
        var m = TimePattern().Match(text);
        return m.Success ? m.Groups["t"].Value : null;
    }

    /// <summary>
    /// Two-column NAME/YIELD variant of <see cref="ExtractRows"/> for the COMPLETED/PROCESSING panels
    /// (no QTY column). Shares the same row clustering and edge handling, but *counts* the edge-clipped
    /// clusters rather than silently dropping them, so the caller can flag a possibly-truncated read.
    /// Yields are returned as raw integer cSCU (no /100 conversion).
    /// </summary>
    public static ExtractResult ExtractYieldRows(OcrRegionResult list, double edgeMarginFramePx = 10)
    {
        var words = list.AllWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0)
            return new ExtractResult([], 0, 0);

        var heights = words.Select(w => w.CropRect.Height).OrderBy(h => h).ToList();
        var medianHeight = heights[heights.Count / 2];
        var tolerance = Math.Max(2, medianHeight * 0.6);

        var rows = new List<ParsedRowCscu>();
        var margin = edgeMarginFramePx * list.EffectiveScale;
        var droppedTop = 0;
        var droppedBottom = 0;

        foreach (var cluster in ClusterByCenterY(words, tolerance))
        {
            var top = cluster.Min(w => w.CropRect.Y);
            var bottom = cluster.Max(w => w.CropRect.Bottom);
            if (top < margin)
            {
                droppedTop++;
                continue;
            }
            if (bottom > list.CropHeight - margin)
            {
                droppedBottom++;
                continue;
            }

            var text = string.Join(' ', cluster.OrderBy(w => w.CropRect.X).Select(w => w.Text)).Trim();
            var match = YieldRowPattern().Match(text);
            if (!match.Success)
                continue;

            if (!TryParseCscu(match.Groups["yield"].Value, out var yieldCscu) || ClampCscu(yieldCscu) is not int yield)
                continue;

            var name = NormalizeName(match.Groups["name"].Value);
            var centerY = cluster.Average(w => w.CropRect.CenterY);
            rows.Add(new ParsedRowCscu(name, 0, yield, centerY));
        }

        return new ExtractResult(rows, droppedTop, droppedBottom);
    }

    /// <summary>Parses the COMPLETED-panel <c>YIELD</c> total, in cSCU. Null when absent/occluded/garbage.</summary>
    public static int? ParseYieldTotal(string text)
    {
        var m = YieldTotalPattern().Match(text);
        if (m.Success && TryParseCscu(m.Groups["v"].Value, out var labelled) && ClampCscu(labelled) is int total)
            return total;

        // Fallback: the ROI is the total line alone, so the first genuinely numeric token is the total.
        foreach (var token in text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
            if (TryParseCscu(token, out var v) && ClampCscu(v) is int t)
                return t;

        return null;
    }

    /// <summary>Parses the reused slot index from "WORK ORDER 1". Logging/disambiguation only.</summary>
    public static int? ParseWorkOrderIndex(string text)
    {
        var m = WorkOrderIndexPattern().Match(text);
        return m.Success && TryParseCscu(m.Groups["n"].Value, out var v) ? (int)v : null;
    }

    /// <summary>Classifies the middle-column state header text into a <see cref="PanelState"/>.</summary>
    public static PanelState Classify(string headerText)
    {
        var t = headerText.ToUpperInvariant();
        if (t.Contains("COMPLETED"))
            return PanelState.Completed;
        if (t.Contains("PROCESSING"))
            return PanelState.Processing;
        if (t.Contains("SETUP"))
            return PanelState.Setup;
        return PanelState.None;
    }
}
