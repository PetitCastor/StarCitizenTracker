using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TrackingService.Trackers;

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
}
