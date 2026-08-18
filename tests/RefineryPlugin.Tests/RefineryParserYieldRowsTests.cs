using GameCapture.Contracts;
using Xunit;

namespace RefineryPlugin.Tests;

public class RefineryParserYieldRowsTests
{
    private static OcrWordInfo Word(string text, double x, double y, double width = 60, double height = 20)
        => new(text, new RectF(x, y, width, height));

    private static OcrRegionResult Region(IReadOnlyList<OcrWordInfo> words, double effectiveScale = 1.0,
        uint roiWidth = 1000, uint roiHeight = 1000)
        => new(string.Join(' ', words.Select(w => w.Text)),
            [new OcrLineInfo(string.Join(' ', words.Select(w => w.Text)), words)],
            effectiveScale, RoiX: 0, RoiY: 0, roiWidth, roiHeight);

    [Fact]
    public void ExtractYieldRows_EmptyWords_ReturnsEmpty()
    {
        var result = RefineryParser.ExtractYieldRows(Region([]));
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.DroppedTopEdge);
        Assert.Equal(0, result.DroppedBottomEdge);
    }

    [Fact]
    public void ExtractYieldRows_SingleTwoColumnRow_ParsesCscuInteger()
    {
        var words = new[] { Word("Titanium", 0, 100), Word("644", 70, 100) };

        var row = Assert.Single(RefineryParser.ExtractYieldRows(Region(words)).Rows);

        Assert.Equal("TITANIUM", row.Name);
        Assert.Equal(644, row.YieldCscu); // raw cSCU, no /100
        Assert.Equal(0, row.QtyCscu);     // no QTY column on the completed panel
    }

    [Fact]
    public void ExtractYieldRows_RepairsOcrDigitConfusionInYield()
    {
        var words = new[] { Word("Gold", 0, 100), Word("S,OOl", 70, 100) }; // -> 5001
        var row = Assert.Single(RefineryParser.ExtractYieldRows(Region(words)).Rows);
        Assert.Equal(5001, row.YieldCscu);
    }

    [Fact]
    public void ExtractYieldRows_TwoRows_InYOrder()
    {
        var words = new[]
        {
            Word("Gold", 0, 300), Word("200", 70, 300),
            Word("Titanium", 0, 100), Word("644", 70, 100),
        };

        var rows = RefineryParser.ExtractYieldRows(Region(words)).Rows;

        Assert.Equal(2, rows.Count);
        Assert.Equal("TITANIUM", rows[0].Name); // lower Y first
        Assert.Equal("GOLD", rows[1].Name);
    }

    [Fact]
    public void ExtractYieldRows_RowTouchingTopEdge_CountedAsDroppedTop()
    {
        var words = new[] { Word("Titanium", 0, 2, height: 20), Word("644", 70, 2, height: 20) };

        var result = RefineryParser.ExtractYieldRows(Region(words));

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.DroppedTopEdge);
        Assert.Equal(0, result.DroppedBottomEdge);
    }

    [Fact]
    public void ExtractYieldRows_RowTouchingBottomEdge_CountedAsDroppedBottom()
    {
        var words = new[] { Word("Titanium", 0, 995, height: 20), Word("644", 70, 995, height: 20) };

        var result = RefineryParser.ExtractYieldRows(Region(words, roiHeight: 1000));

        Assert.Empty(result.Rows);
        Assert.Equal(0, result.DroppedTopEdge);
        Assert.Equal(1, result.DroppedBottomEdge);
    }

    [Fact]
    public void Checksum_SumOfRowsEqualsParsedTotal_WhenComplete()
    {
        var words = new[]
        {
            Word("Titanium", 0, 100), Word("313", 70, 100),
            Word("Gold", 0, 200), Word("57", 70, 200),
            Word("Iron", 0, 300), Word("274", 70, 300),
        };

        var rows = RefineryParser.ExtractYieldRows(Region(words)).Rows;
        var total = RefineryParser.ParseYieldTotal("YIELD 644");

        Assert.Equal(644, rows.Sum(r => r.YieldCscu)); // 313 + 57 + 274
        Assert.Equal(644, total);
    }

    [Fact]
    public void Checksum_SumMismatch_WhenARowIsMissing()
    {
        // Only two of three rows visible (list not scrolled) → sum < printed total.
        var words = new[]
        {
            Word("Titanium", 0, 100), Word("313", 70, 100),
            Word("Gold", 0, 200), Word("57", 70, 200),
        };

        var rows = RefineryParser.ExtractYieldRows(Region(words)).Rows;
        var total = RefineryParser.ParseYieldTotal("YIELD 644");

        Assert.NotEqual(total, rows.Sum(r => r.YieldCscu)); // 370 != 644 → Partial
    }

    [Theory]
    [InlineData("YIELD 644", 644)]
    [InlineData("YIELD: 644", 644)]
    [InlineData("YIELD 6,44", 644)]   // stray comma stripped
    [InlineData("644", 644)]          // bare total (fallback path)
    public void ParseYieldTotal_ParsesLabelledAndBare(string text, int expected)
        => Assert.Equal(expected, RefineryParser.ParseYieldTotal(text));

    [Fact]
    public void ParseYieldTotal_NoDigits_ReturnsNull()
        => Assert.Null(RefineryParser.ParseYieldTotal("YIELD"));

    [Theory]
    [InlineData("WORK ORDER 1", 1)]
    [InlineData("Work Order 12", 12)]
    public void ParseWorkOrderIndex_Parses(string text, int expected)
        => Assert.Equal(expected, RefineryParser.ParseWorkOrderIndex(text));

    [Fact]
    public void ParseWorkOrderIndex_NoMatch_ReturnsNull()
        => Assert.Null(RefineryParser.ParseWorkOrderIndex("no slot here"));

    [Theory]
    [InlineData("SETUP", PanelState.Setup)]
    [InlineData("processing", PanelState.Processing)]
    [InlineData("Completed", PanelState.Completed)]
    [InlineData("MATERIALS YIELDED", PanelState.None)]
    [InlineData("", PanelState.None)]
    public void Classify_MapsHeaderText(string header, PanelState expected)
        => Assert.Equal(expected, RefineryParser.Classify(header));

    [Theory]
    [InlineData("Torite (Ore)", "TORITE")]     // SETUP shows the ore-form suffix
    [InlineData("TORITE", "TORITE")]           // PROCESSING/COMPLETED show the base name
    [InlineData("Corundum (Raw)", "CORUNDUM")]
    public void BaseName_StripsOreSuffix(string raw, string expected)
        => Assert.Equal(expected, RefineryParser.BaseName(raw));

    [Fact]
    public void ExtractColumnarRows_SetupRow_SplitsNameAndNumbers_WithPlaceholderYield()
    {
        // SETUP layout: NAME QUALITY QTY YIELD(--), the name spanning two tokens incl. "(Ore)".
        var words = new[]
        {
            Word("Torite", 0, 100), Word("(Ore)", 70, 100),
            Word("262", 150, 100), Word("112", 220, 100), Word("--", 290, 100),
        };

        var row = Assert.Single(RefineryParser.ExtractColumnarRows(Region(words)).Rows);

        Assert.Equal("TORITE (ORE)", row.Name);
        Assert.Equal(262, row.Numbers[0]); // quality
        Assert.Equal(112, row.Numbers[1]); // qty
        Assert.Null(row.Numbers[2]);       // yield "--"
    }

    [Fact]
    public void ExtractColumnarRows_CompletedRow_NameQualityYield()
    {
        var words = new[] { Word("Torite", 0, 100), Word("262", 150, 100), Word("50", 220, 100) };

        var row = Assert.Single(RefineryParser.ExtractColumnarRows(Region(words)).Rows);

        Assert.Equal("TORITE", row.Name);
        Assert.Equal(new int?[] { 262, 50 }, row.Numbers);
    }

    // ---- H6: OCR-garbage 12-digit tokens must never throw an unchecked (int) overflow ----

    [Fact]
    public void ExtractColumnarRows_TwelveDigitGarbageColumn_BecomesNull_DoesNotThrow()
    {
        var words = new[]
        {
            Word("Torite", 0, 100), Word("262", 150, 100), Word("999999999999", 220, 100),
        };

        var ex = Record.Exception(() => RefineryParser.ExtractColumnarRows(Region(words)));
        Assert.Null(ex);

        var row = Assert.Single(RefineryParser.ExtractColumnarRows(Region(words)).Rows);
        Assert.Equal(262, row.Numbers[0]);
        Assert.Null(row.Numbers[1]); // garbage column dropped, not a wrapped/overflowed int
    }

    [Fact]
    public void ExtractYieldRows_TwelveDigitGarbageYield_RowDropped_DoesNotThrow()
    {
        var words = new[] { Word("Titanium", 0, 100), Word("999999999999", 70, 100) };

        var ex = Record.Exception(() => RefineryParser.ExtractYieldRows(Region(words)));

        Assert.Null(ex);
        Assert.Empty(RefineryParser.ExtractYieldRows(Region(words)).Rows);
    }

    [Fact]
    public void ParseYieldTotal_TwelveDigitGarbage_ReturnsNull_DoesNotThrow_LabelledForm()
    {
        var ex = Record.Exception(() => RefineryParser.ParseYieldTotal("YIELD 999999999999"));
        Assert.Null(ex);
        Assert.Null(RefineryParser.ParseYieldTotal("YIELD 999999999999"));
    }

    [Fact]
    public void ParseYieldTotal_TwelveDigitGarbage_ReturnsNull_DoesNotThrow_BareFallbackForm()
    {
        var ex = Record.Exception(() => RefineryParser.ParseYieldTotal("999999999999"));
        Assert.Null(ex);
        Assert.Null(RefineryParser.ParseYieldTotal("999999999999"));
    }
}
