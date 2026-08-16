using System.Threading.Channels;
using CaptureContracts.Proto;
using Common;
using Xunit;

namespace CaptureEngine.Tests;

/// <summary>
/// The scan loop with the registry, without gRPC: everything the wire contract promises about a
/// tick — one per frame, monotonic sequence, one result per subscribed ROI, all from the same
/// frame — is a property of this layer, and asserting it here says which layer broke when it
/// does. Needs a real Windows OCR language pack (the loop really OCRs the corpus).
/// </summary>
[Trait("Category", "Integration")]
public class ScanLoopTests
{
    /// <summary>Generous: three 1440p frames x two ROIs of real OCR, on a shared runner.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Registry + loop over the fixture corpus, wired exactly as EngineHost wires them.</summary>
    private sealed record Harness(
        ScanLoop Loop, SubscriptionRegistry Registry, ReplayFrameSource Source, EngineStatus Status);

    private static Harness NewHarness(ConsoleSink sink)
    {
        var ocr = new OcrPipeline();
        var status = new EngineStatus(ocr.LanguageTag, replayMode: true);
        var registry = new SubscriptionRegistry(status);
        var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var loop = new ScanLoop(source, ocr, registry, status, sink, new EngineConfig(), verbose: false);

        return new Harness(loop, registry, source, status);
    }

    /// <summary>Runs the loop to completion while draining the client, and returns every tick.</summary>
    private static async Task<List<TickResult>> RunAndCollectAsync(
        Harness harness, ClientConnection client, CancellationToken ct)
    {
        var run = harness.Loop.RunAsync(ct);

        var ticks = new List<TickResult>();
        await foreach (var response in client.Out.Reader.ReadAllAsync(ct))
            ticks.Add(response.Tick);

        await run;
        return ticks;
    }

    [Fact]
    public async Task RunAsync_OverReplayCorpus_EmitsOneCompleteTickPerFrame()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();
        var harness = NewHarness(sink);
        using var source = harness.Source;
        using var loop = harness.Loop;

        var client = harness.Registry.Register(replayMode: true);
        client.SetRois(new RoiSetUpdate
        {
            Rois = { EngineTestFixtures.PanelStateRoi(), EngineTestFixtures.ToggleStripRoi() },
        });

        var ticks = await RunAndCollectAsync(harness, client, cts.Token);

        // Non-empty first: "one tick per frame" is trivially true of a corpus that failed to copy.
        Assert.NotEmpty(ticks);
        Assert.Equal(source.FrameCount, ticks.Count);

        for (var i = 0; i < ticks.Count; i++)
        {
            var tick = ticks[i];

            // Sequence numbers are how a plugin tells a fresh decision from a repeat of one it
            // already made, so gaps and restarts matter as much as the count.
            Assert.Equal((ulong)(i + 1), tick.FrameSeq);
            Assert.True(tick.FrameWidth > 0 && tick.FrameHeight > 0);
            Assert.False(tick.Manual);

            Assert.Equal(["panel", "toggle"], tick.Results.Select(r => r.RoiId));

            var text = tick.Results[0];
            Assert.False(text.Error, text.ErrorMessage);
            Assert.True(text.EffectiveScale > 0);
            Assert.True(text.FrameRect.Width > 0 && text.FrameRect.Height > 0);

            var pixels = tick.Results[1];
            Assert.False(pixels.Error, pixels.ErrorMessage);
            Assert.True(pixels.PixelsWidth > 0 && pixels.PixelsHeight > 0);
            Assert.True(pixels.PixelsStride >= pixels.PixelsWidth * 4);
            Assert.True(pixels.PixelsBgra.Length >= pixels.PixelsWidth * pixels.PixelsHeight * 4);
        }

        // Corpus exhausted completes the channel, which is what ends a plugin's Track stream and
        // lets it run its finalisers instead of waiting forever on a finished engine.
        Assert.True(client.Out.Reader.Completion.IsCompletedSuccessfully);
        Assert.Equal((ulong)source.FrameCount, harness.Status.Snapshot().FrameSeq);
    }

    [Fact]
    public async Task RunAsync_WithOneUnreadableRoi_FailsOnlyThatRoi()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();
        var harness = NewHarness(sink);
        using var source = harness.Source;
        using var loop = harness.Loop;

        var client = harness.Registry.Register(replayMode: true);
        client.SetRois(new RoiSetUpdate
        {
            Rois = { EngineTestFixtures.PanelStateRoi(), EngineTestFixtures.OffFrameRoi() },
        });

        var ticks = await RunAndCollectAsync(harness, client, cts.Token);

        Assert.NotEmpty(ticks);
        foreach (var tick in ticks)
        {
            var good = Assert.Single(tick.Results, r => r.RoiId == "panel");
            Assert.False(good.Error, good.ErrorMessage);

            // Clamping alone would have turned the off-frame rect into a 1x1 sliver and reported
            // a successful empty read — a plausible answer to a question nobody asked.
            var bad = Assert.Single(tick.Results, r => r.RoiId == "offscreen");
            Assert.True(bad.Error);
            Assert.NotEmpty(bad.ErrorMessage);
            Assert.Empty(bad.Text);
        }
    }
}
