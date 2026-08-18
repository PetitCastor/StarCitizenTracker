using GameCapture.Contracts;
using GameCapture.Contracts.Proto;
using Google.Protobuf;
using Xunit;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// The status surface, region by region. The distinction being pinned here is the one the whole
/// type exists for: a region that FAILED and a region that was genuinely EMPTY answer the same
/// empty string, and a plugin that cannot tell them apart files an order that never completed.
/// </summary>
public class TickDataTests
{
    private const string Roi = "panel";

    // ---------- Status ----------

    [Fact]
    public void Status_OfAReadRegion_IsOk()
        => Assert.Equal(RoiStatus.Ok, Text(Roi, "SETUP").Status(Roi));

    [Fact]
    public void Status_OfAnEmptyButSuccessfulRead_IsOk()
        => Assert.Equal(RoiStatus.Ok, Text(Roi, "").Status(Roi));

    [Fact]
    public void Status_OfAFailedRegion_IsFailed()
        => Assert.Equal(RoiStatus.Failed, Failed(Roi).Status(Roi));

    /// <summary>
    /// The typo case, and the reason this is not a bool: an id that never reached the engine reads
    /// as "no data" exactly like a failure would, and only this tells the author which mistake it is.
    /// </summary>
    [Fact]
    public void Status_OfAnIdTheTickDoesNotCarry_IsNotSubscribed()
        => Assert.Equal(RoiStatus.NotSubscribed, Text(Roi, "SETUP").Status("panle"));

    /// <summary>Ids are matched the way the engine matches them: exactly.</summary>
    [Fact]
    public void Status_IsCaseSensitive()
        => Assert.Equal(RoiStatus.NotSubscribed, Text(Roi, "SETUP").Status("PANEL"));

    // ---------- TryGetText ----------

    [Fact]
    public void TryGetText_OfAReadRegion_YieldsTheText()
    {
        Assert.True(Text(Roi, "SETUP").TryGetText(Roi, out var text));
        Assert.Equal("SETUP", text);
    }

    /// <summary>
    /// The half of the truth table that the old <c>Text()</c> could not express: true, and the
    /// string is empty because the panel really was.
    /// </summary>
    [Fact]
    public void TryGetText_OfAGenuinelyEmptyRegion_SucceedsWithAnEmptyString()
    {
        Assert.True(Text(Roi, "").TryGetText(Roi, out var text));
        Assert.Equal("", text);
    }

    [Fact]
    public void TryGetText_OfAFailedRegion_Fails()
    {
        Assert.False(Failed(Roi).TryGetText(Roi, out var text));
        Assert.Equal("", text);
    }

    [Fact]
    public void TryGetText_OfAnIdTheTickDoesNotCarry_Fails()
        => Assert.False(Text(Roi, "SETUP").TryGetText("other", out _));

    /// <summary>
    /// A PIXELS result carries no text, and its empty <c>text</c> field is a proto3 default — handing
    /// it back would report a colour probe as a successfully read empty panel.
    /// </summary>
    [Fact]
    public void TryGetText_OfAPixelsRegion_Fails()
        => Assert.False(Pixels(Roi).TryGetText(Roi, out _));

    // ---------- TryGetOcr / TryGetPixels ----------

    [Fact]
    public void TryGetOcr_OfATextRegion_SucceedsWithoutWordGeometry()
    {
        Assert.True(Text(Roi, "SETUP").TryGetOcr(Roi, out var ocr));
        Assert.Equal("SETUP", ocr.Text);
        Assert.Empty(ocr.Lines);
    }

    [Fact]
    public void TryGetOcr_OfAFailedRegion_Fails()
        => Assert.False(Failed(Roi).TryGetOcr(Roi, out _));

    [Fact]
    public void TryGetOcr_OfAPixelsRegion_Fails()
        => Assert.False(Pixels(Roi).TryGetOcr(Roi, out _));

    [Fact]
    public void TryGetPixels_OfAPixelsRegion_Succeeds()
    {
        Assert.True(Pixels(Roi).TryGetPixels(Roi, out var pixels));
        Assert.Equal(((byte)10, (byte)20, (byte)30), pixels.AveragePatch(0, 0));
    }

    [Fact]
    public void TryGetOcr_OfAnIdTheTickDoesNotCarry_Fails()
        => Assert.False(Text(Roi, "SETUP").TryGetOcr("other", out _));

    [Fact]
    public void TryGetPixels_OfATextRegion_Fails()
        => Assert.False(Text(Roi, "SETUP").TryGetPixels(Roi, out _));

    [Fact]
    public void TryGetPixels_OfAnIdTheTickDoesNotCarry_Fails()
        => Assert.False(Pixels(Roi).TryGetPixels("other", out _));

    /// <summary>
    /// A payload the engine considered fine but that violates the wire invariants is not an engine
    /// error: the status stays Ok and only the read fails, which is the boundary check doing its job
    /// rather than the engine reporting one.
    /// </summary>
    [Fact]
    public void TryGetPixels_OfATruncatedBuffer_FailsWithoutBecomingAnEngineError()
    {
        var tick = TickData.From(One(new RoiResult
        {
            RoiId = Roi,
            Kind = RoiResultKind.Pixels,
            FrameRect = new RoiRect(0, 0, 4, 4).ToProto(),
            PixelsBgra = ByteString.CopyFrom(new byte[4]),
            PixelsStride = 16,
            PixelsWidth = 4,
            PixelsHeight = 4,
        }));

        Assert.False(tick.TryGetPixels(Roi, out _));
        Assert.Equal(RoiStatus.Ok, tick.Status(Roi));
        Assert.Null(tick.ErrorMessage(Roi));
    }

    // ---------- ErrorMessage ----------

    [Fact]
    public void ErrorMessage_OfAFailedRegion_IsWhatTheEngineSaid()
        => Assert.Equal("ROI outside the frame.", Failed(Roi).ErrorMessage(Roi));

    /// <summary>A failure the engine did not describe still has to read as a failure.</summary>
    [Fact]
    public void ErrorMessage_OfAFailureWithNoMessage_IsStated()
        => Assert.Equal("the engine reported a ROI failure.",
            TickData.From(One(new RoiResult { RoiId = Roi, Error = true })).ErrorMessage(Roi));

    [Fact]
    public void ErrorMessage_OfAReadRegion_IsNull()
        => Assert.Null(Text(Roi, "").ErrorMessage(Roi));

    [Fact]
    public void ErrorMessage_OfAnIdTheTickDoesNotCarry_IsNull()
        => Assert.Null(Text(Roi, "").ErrorMessage("other"));

    // ---------- ErroredRois / HasErrors ----------

    [Fact]
    public void ATickWhoseRegionsAllRead_HasNoErrors()
    {
        var tick = TickFactory.Tick(1, rois: [(Roi, "SETUP", false), ("toggle", "on", false)]);

        Assert.False(tick.HasErrors);
        Assert.Empty(tick.ErroredRois);
    }

    /// <summary>In the order the engine reported them, which is the order a plugin printing the list
    /// will show — not whatever order a hash map happens to enumerate in.</summary>
    [Fact]
    public void ErroredRois_NamesEveryFailedRegionInWireOrder()
    {
        var tick = TickFactory.Tick(1, rois:
            [("footer", "", true), ("toggle", "on", false), (Roi, "", true)]);

        Assert.True(tick.HasErrors);
        Assert.Equal([new RoiId("footer"), new RoiId(Roi)], tick.ErroredRois);
    }

    /// <summary>
    /// A client that subscribed one id twice gets one reading — the indexer keeps the last — so the
    /// failure list must not report it as two separate failures.
    /// </summary>
    [Fact]
    public void ADuplicatedFailedId_IsListedOnce()
    {
        var tick = TickFactory.Tick(1, rois: [(Roi, "", true), (Roi, "", true)]);

        Assert.Equal([new RoiId(Roi)], tick.ErroredRois);
    }

    /// <summary>
    /// Last one wins, and the failure list agrees with the lookup: a duplicate whose final result
    /// read cleanly is not a failure, whatever the earlier one said.
    /// </summary>
    [Fact]
    public void ADuplicatedIdWhoseLastResultRead_IsNotAFailure()
    {
        var tick = TickFactory.Tick(1, rois: [(Roi, "", true), (Roi, "SETUP", false)]);

        Assert.False(tick.HasErrors);
        Assert.Equal(RoiStatus.Ok, tick.Status(Roi));
        Assert.True(tick.TryGetText(Roi, out var text));
        Assert.Equal("SETUP", text);
    }

    // ---------- fixtures ----------

    private static TickData Text(RoiId id, string text)
        => TickFactory.Tick(1, rois: (id.Value, text, false));

    private static TickData Failed(RoiId id)
        => TickFactory.Tick(1, rois: (id.Value, "", true));

    private static TickData Pixels(RoiId id)
    {
        // One BGRA pixel, stride included: the sampler's invariants are checked at the boundary and
        // a fixture that violated them would fail for the wrong reason.
        var bgra = new byte[] { 10, 20, 30, 255 };

        return TickData.From(One(new RoiResult
        {
            RoiId = id.Value,
            Kind = RoiResultKind.Pixels,
            FrameRect = new RoiRect(0, 0, 1, 1).ToProto(),
            PixelsBgra = ByteString.CopyFrom(bgra),
            PixelsStride = 4,
            PixelsWidth = 1,
            PixelsHeight = 1,
        }));
    }

    private static TickResult One(RoiResult result)
    {
        var proto = new TickResult
        {
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FrameSeq = 1,
            FrameWidth = 2560,
            FrameHeight = 1440,
        };
        proto.Results.Add(result);
        return proto;
    }
}
