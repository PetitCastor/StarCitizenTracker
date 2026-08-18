using GameCapture.Contracts;
using GameCapture.Sdk;
using GameCapture.Sdk.Testing;
using Xunit;

namespace GameCapture.Sdk.Testing.Tests;

/// <summary>
/// Checklist against the two private factories this builder replaces
/// (<c>MissionPlugin.Tests/TickFactory.cs</c>, <c>RefineryPlugin.Tests/TickFactory.cs</c>): TEXT,
/// DETAILED with word geometry, PIXELS, bare-error injection, the manual flag, and a stable frame
/// sequence. Every one of those constructs gets a test here.
/// </summary>
public class TickDataBuilderTests
{
    [Fact]
    public void Text_RoundTrips()
    {
        var tick = new TickDataBuilder().Text("panel", "hello").Build();

        Assert.Equal(RoiStatus.Ok, tick.Status("panel"));
        Assert.True(tick.TryGetText("panel", out var text));
        Assert.Equal("hello", text);
    }

    [Fact]
    public void Detailed_CarriesWordGeometry()
    {
        var word = new OcrWordSpec("42", new RectF(700, 10, 150, 40));
        var tick = new TickDataBuilder()
            .Detailed("list", new OcrLineSpec("Gold 42", [word]))
            .Build();

        Assert.True(tick.TryGetOcr("list", out var ocr));
        var line = Assert.Single(ocr.Lines);
        Assert.Equal("Gold 42", line.Text);
        var w = Assert.Single(line.Words);
        Assert.Equal("42", w.Text);
        Assert.Equal(700, w.CropRect.X);
    }

    [Fact]
    public void Detailed_PlainStringLine_NeedsNoWordGeometry()
    {
        var tick = new TickDataBuilder().Detailed("list", "line one", "line two").Build();

        Assert.True(tick.TryGetOcr("list", out var ocr));
        Assert.Equal(2, ocr.Lines.Count);
        Assert.Empty(ocr.Lines[0].Words);
        Assert.Equal("line one", ocr.Lines[0].Text);
    }

    [Fact]
    public void Pixels_ProducesSolidStrip()
    {
        var tick = new TickDataBuilder().Pixels("toggle", b: 20, g: 40, r: 200, w: 10, h: 4).Build();

        Assert.True(tick.TryGetPixels("toggle", out var pixels));
        Assert.Equal(10, pixels.Width);
        Assert.Equal(4, pixels.Height);
    }

    [Fact]
    public void Errored_CarriesNoPayload()
    {
        var tick = new TickDataBuilder().Errored("panel", "fabricated failure").Build();

        Assert.Equal(RoiStatus.Failed, tick.Status("panel"));
        Assert.Equal("fabricated failure", tick.ErrorMessage("panel"));
        Assert.False(tick.TryGetText("panel", out _));
        Assert.Contains((RoiId)"panel", tick.ErroredRois);
    }

    [Fact]
    public void Manual_DefaultsFalse_SetsTrue()
    {
        Assert.False(new TickDataBuilder().Text("panel", "x").Build().Manual);
        Assert.True(new TickDataBuilder().Text("panel", "x").Manual().Build().Manual);
    }

    [Fact]
    public void FrameSeq_Override_IsExact()
    {
        var tick = new TickDataBuilder().Text("panel", "x").FrameSeq(777).Build();

        Assert.Equal(777UL, tick.FrameSeq);
    }

    [Fact]
    public void FrameSeq_WithoutOverride_IsUniquePerBuild()
    {
        var a = new TickDataBuilder().Text("panel", "x").Build();
        var b = new TickDataBuilder().Text("panel", "x").Build();

        Assert.NotEqual(a.FrameSeq, b.FrameSeq);
    }

    [Fact]
    public void At_Override_SetsTimestamp()
    {
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var tick = new TickDataBuilder().Text("panel", "x").At(at).Build();

        Assert.Equal(at.LocalDateTime, tick.Timestamp);
    }

    [Fact]
    public void AllSubscribedRoisAreAnswered_UnsubscribedOnesAreNot()
    {
        var tick = new TickDataBuilder()
            .Text("panel", "a")
            .Text("modal", "")
            .Pixels("toggle", 0, 0, 0, 1, 1)
            .Build();

        Assert.Equal(RoiStatus.Ok, tick.Status("panel"));
        Assert.Equal(RoiStatus.Ok, tick.Status("modal"));
        Assert.Equal(RoiStatus.Ok, tick.Status("toggle"));
        Assert.Equal(RoiStatus.NotSubscribed, tick.Status("never-added"));
    }

    [Fact]
    public void SameId_AddedTwice_LastWins()
    {
        var tick = new TickDataBuilder().Text("panel", "first").Text("panel", "second").Build();

        Assert.True(tick.TryGetText("panel", out var text));
        Assert.Equal("second", text);
        Assert.False(tick.HasErrors);
    }

    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(1920, 1080)]
    public void NonReferenceResolution_StillWorks(int width, int height)
    {
        var tick = new TickDataBuilder(width, height).Text("panel", "hi").Build();

        Assert.Equal(width, tick.FrameWidth);
        Assert.Equal(height, tick.FrameHeight);
        Assert.True(tick.TryGetText("panel", out var text));
        Assert.Equal("hi", text);
    }
}
