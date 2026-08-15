using TrackingService;
using TrackingService.Trackers;
using Xunit;

namespace TrackingService.Tests;

public class RefineryParserTests
{
    private static OcrWordInfo Word(string text, double x, double y, double width = 60, double height = 20)
        => new(text, new RectF(x, y, width, height));

    private static OcrRegionResult Region(IReadOnlyList<OcrWordInfo> words, double effectiveScale = 1.0,
        uint roiWidth = 1000, uint roiHeight = 1000)
        => new(string.Join(' ', words.Select(w => w.Text)),
            [new OcrLineInfo(string.Join(' ', words.Select(w => w.Text)), words)],
            effectiveScale, RoiX: 0, RoiY: 0, roiWidth, roiHeight);

    [Fact]
    public void ExtractRows_EmptyWords_ReturnsEmpty()
    {
        var region = Region([]);
        Assert.Empty(RefineryParser.ExtractRows(region));
    }

    [Fact]
    public void ExtractRows_SingleWellFormedRow_Parses()
    {
        var words = new[]
        {
            Word("Titanium", 0, 100),
            Word("5000", 70, 100),
            Word("6000", 140, 100),
        };
        var region = Region(words);

        var rows = RefineryParser.ExtractRows(region);

        var row = Assert.Single(rows);
        Assert.Equal("TITANIUM", row.Name);
        Assert.Equal(50.00m, row.QtyScu);
        Assert.Equal(60.00m, row.YieldScu);
    }

    [Fact]
    public void ExtractRows_RepairsOcrDigitConfusion()
    {
        // O->0, l/I/i->1, S->5, B->8, commas/dots stripped.
        var words = new[]
        {
            Word("Gold", 0, 100),
            Word("S,OOl", 70, 100),  // -> 5001 cSCU -> 50.01
            Word("l2B4", 140, 100),  // -> 1284 cSCU -> 12.84
        };
        var region = Region(words);

        var row = Assert.Single(RefineryParser.ExtractRows(region));
        Assert.Equal(50.01m, row.QtyScu);
        Assert.Equal(12.84m, row.YieldScu);
    }

    [Fact]
    public void ExtractRows_RowTouchingTopEdge_Discarded()
    {
        // margin = 10 * EffectiveScale(1) = 10. A word whose top is inside the margin
        // gets its whole cluster discarded.
        var words = new[]
        {
            Word("Titanium", 0, 2, height: 20), // top=2 < margin(10)
            Word("5000", 70, 2, height: 20),
            Word("6000", 140, 2, height: 20),
        };
        var region = Region(words);

        Assert.Empty(RefineryParser.ExtractRows(region));
    }

    [Fact]
    public void ExtractRows_RowTouchingBottomEdge_Discarded()
    {
        var words = new[]
        {
            Word("Titanium", 0, 995, height: 20), // bottom=1015 > CropHeight(1000)-margin(10)=990
            Word("5000", 70, 995, height: 20),
            Word("6000", 140, 995, height: 20),
        };
        var region = Region(words, roiHeight: 1000);

        Assert.Empty(RefineryParser.ExtractRows(region));
    }

    [Fact]
    public void ExtractRows_UnparseableClusterSkipped_ValidClusterKept()
    {
        var words = new[]
        {
            // Row 1: garbage, missing the yield column.
            Word("Mystery", 0, 100),
            Word("5000", 70, 100),
            // Row 2 (far enough below to be its own cluster): valid.
            Word("Gold", 0, 300),
            Word("1000", 70, 300),
            Word("2000", 140, 300),
        };
        var region = Region(words);

        var row = Assert.Single(RefineryParser.ExtractRows(region));
        Assert.Equal("GOLD", row.Name);
    }

    [Fact]
    public void ExtractRows_TwoDistinctRows_BothParsed_InYOrder()
    {
        var words = new[]
        {
            Word("Gold", 0, 300),
            Word("1000", 70, 300),
            Word("2000", 140, 300),
            Word("Titanium", 0, 100),
            Word("5000", 70, 100),
            Word("6000", 140, 100),
        };
        var region = Region(words);

        var rows = RefineryParser.ExtractRows(region);

        Assert.Equal(2, rows.Count);
        Assert.Equal("TITANIUM", rows[0].Name); // lower Y (100) sorts first
        Assert.Equal("GOLD", rows[1].Name);
    }

    [Theory]
    [InlineData("  titanium (ore)  ", "TITANIUM (ORE)")]
    [InlineData("Gold,", "GOLD")]
    [InlineData("quantanium.", "QUANTANIUM")]
    [InlineData("Iron - Ore", "IRON - ORE")]
    public void NormalizeName_TrimsAndUppercases(string raw, string expected)
        => Assert.Equal(expected, RefineryParser.NormalizeName(raw));

    [Theory]
    [InlineData("5000", true, 5000)]
    [InlineData("S,OOl", true, 5001)]
    [InlineData("l2B4", true, 1284)]
    [InlineData("1.234", true, 1234)]
    [InlineData("", false, 0)]
    [InlineData("abc", false, 0)]
    public void TryParseCscu_RepairsAndParses(string token, bool expectedSuccess, int expectedValue)
    {
        var ok = RefineryParser.TryParseCscu(token, out var value);
        Assert.Equal(expectedSuccess, ok);
        if (expectedSuccess)
            Assert.Equal(expectedValue, value);
    }

    [Fact]
    public void ParseStation_ReturnsFirstNonTrivialLine()
    {
        var header = "\r\n  \r\nHU\r\nRayari Anvik Station\r\n";
        Assert.Equal("Rayari Anvik Station", RefineryParser.ParseStation(header));
    }

    [Fact]
    public void ParseStation_NoQualifyingLine_ReturnsNull()
        => Assert.Null(RefineryParser.ParseStation("\r\n \r\nHU\r\n"));

    [Fact]
    public void ParseProcess_MatchesAndReturnsCapture()
        => Assert.Equal("Diffusion Process", RefineryParser.ParseProcess("Header text Diffusion Process footer"));

    [Fact]
    public void ParseProcess_NoMatch_ReturnsNull()
        => Assert.Null(RefineryParser.ParseProcess("no relevant text here"));

    [Fact]
    public void ParseCost_MatchesAndAppendsUnit()
        => Assert.Equal("12,345 aUEC", RefineryParser.ParseCost("Total: 12,345 aUEC due"));

    [Fact]
    public void ParseCost_NoMatch_ReturnsNull()
        => Assert.Null(RefineryParser.ParseCost("no cost here"));

    [Theory]
    [InlineData("Ready in 33m 45s", "33m 45s")]
    [InlineData("Ready in 1h 33m 45s", "1h 33m 45s")]
    [InlineData("Ready in 03:12:36", "03:12:36")]
    public void ParseTime_MatchesBothFormats(string text, string expected)
        => Assert.Equal(expected, RefineryParser.ParseTime(text));

    [Fact]
    public void ParseTime_NoMatch_ReturnsNull()
        => Assert.Null(RefineryParser.ParseTime("no time here"));

    // ---- ClampCscu (H6: unchecked (int) casts on OCR-garbage overflow) ----

    [Theory]
    [InlineData("0")]
    [InlineData("5000")]
    [InlineData("10000000")] // exactly at the sane upper bound
    public void ClampCscu_InRange_ReturnsValue(string token)
    {
        Assert.True(RefineryParser.TryParseCscu(token, out var v));
        Assert.Equal((int)v, RefineryParser.ClampCscu(v));
    }

    [Fact]
    public void ClampCscu_TwelveDigitGarbageToken_ReturnsNull_DoesNotThrow()
    {
        // The row/total regexes admit up to 12 digits to tolerate OCR noise glued onto a real number.
        // A fully-garbage 12-digit read (~1e12) is ~500x int.MaxValue — an unchecked (int) cast throws
        // OverflowException here; ClampCscu must reject it as unparsed instead.
        Assert.True(RefineryParser.TryParseCscu("999999999999", out var v));
        var ex = Record.Exception(() => RefineryParser.ClampCscu(v));
        Assert.Null(ex);
        Assert.Null(RefineryParser.ClampCscu(v));
    }

    [Fact]
    public void ClampCscu_JustOverSaneBound_ReturnsNull()
        => Assert.Null(RefineryParser.ClampCscu(10_000_001m));
}
