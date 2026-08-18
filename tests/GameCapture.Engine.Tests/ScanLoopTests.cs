using System.Threading.Channels;
using GameCapture.Contracts.Proto;
using Xunit;

namespace GameCapture.Engine.Tests;

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
    public async Task RunAsync_ManualFrameHandler_ReceivesTheManualTickFrame()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();
        var harness = NewHarness(sink);
        using var source = harness.Source;
        using var loop = harness.Loop;

        (int Width, int Height)? dumpedSize = null;
        harness.Loop.TriggerManual(frame =>
        {
            dumpedSize = (frame.PixelWidth, frame.PixelHeight);
            return Task.CompletedTask;
        });

        var client = harness.Registry.Register(replayMode: true);
        client.SetRois(new RoiSetUpdate { Rois = { EngineTestFixtures.PanelStateRoi() } });

        var ticks = await RunAndCollectAsync(harness, client, cts.Token);

        var manualTick = Assert.Single(ticks, tick => tick.Manual);
        Assert.NotNull(dumpedSize);
        Assert.Equal(((int)manualTick.FrameWidth, (int)manualTick.FrameHeight), dumpedSize.Value);
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

    /// <summary>
    /// One plugin leaving mid-replay must not end the run for the others. Replay writes are
    /// awaited rather than dropped — determinism is the point of a corpus run — and an awaited
    /// write to a completed channel throws, which is not the cancellation the loop's handler
    /// expects: it would escape RunAsync and take every client's run down with it, mid-corpus.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenOneClientChannelIsClosed_KeepsServingTheRest()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();
        var harness = NewHarness(sink);
        using var source = harness.Source;
        using var loop = harness.Loop;

        var rois = new RoiSetUpdate { Rois = { EngineTestFixtures.PanelStateRoi() } };

        var departing = harness.Registry.Register(replayMode: true);
        departing.SetRois(rois);

        var staying = harness.Registry.Register(replayMode: true);
        staying.SetRois(rois);

        // Exactly the state a plugin's DisposeAsync leaves behind: its channel is completed while
        // the loop may still be holding a snapshot taken before it was unregistered.
        departing.Out.Writer.TryComplete();

        var ticks = await RunAndCollectAsync(harness, staying, cts.Token);

        Assert.Equal(source.FrameCount, ticks.Count);
        Assert.True(staying.Out.Reader.Completion.IsCompletedSuccessfully);
    }
}
