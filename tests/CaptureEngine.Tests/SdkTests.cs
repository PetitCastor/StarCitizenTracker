using CaptureContracts;
using CaptureContracts.Proto;
using TrackerSdk;
using Xunit;

namespace CaptureEngine.Tests;

/// <summary>
/// The SDK against a real engine over a real pipe. Everything the SDK does is a translation —
/// subscriptions out, ticks in — and a translation layer is exactly the kind of code that passes
/// its own unit tests while disagreeing with the thing on the other side of the wire, so these
/// drive the in-proc engine host rather than a stub.
/// </summary>
public class SdkTests
{
    /// <summary>Generous: the session tests OCR the whole fixture corpus over the pipe.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long a plugin is willing to wait for an engine that is already up.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static string NewPipeName() => $"sc-sdk-{Guid.NewGuid():N}";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WaitForEngineAsync_AgainstRunningHost_ReturnsStatus()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();

        var pipeName = NewPipeName();
        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            new ReplayFrameSource(EngineTestFixtures.ReplayDir), sink, verbose: false);
        await engine.StartAsync(cts.Token);

        using var client = new CaptureClient(pipeName);
        var status = await client.WaitForEngineAsync(ConnectTimeout, cts.Token);

        Assert.NotEmpty(status.EngineVersion);
        Assert.True(status.ReplayMode);

        // The scan loop was never started, so the wait must have succeeded on the RPC answering —
        // not on any frame having been produced.
        Assert.Equal(0ul, status.FrameSeq);
    }

    [Fact]
    public async Task WaitForEngineAsync_AgainstDeadPipe_ThrowsTimeout()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        // Nothing is listening: connecting to an absent pipe blocks rather than failing, which is
        // precisely why the wait bounds each attempt by its remaining budget instead of trusting
        // the first call to come back.
        using var client = new CaptureClient(NewPipeName());

        var timeout = TimeSpan.FromSeconds(1);
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => client.WaitForEngineAsync(timeout, cts.Token));

        Assert.Contains("did not answer", ex.Message);
    }

    /// <summary>
    /// Ctrl+C during the wait. A plugin host shuts down on OperationCanceledException; if its own
    /// cancellation came back as RpcException(Cancelled) instead, the host would take a clean stop
    /// for an engine failure — and the deadline-bounded attempt is where nearly all of the wait is
    /// actually spent, so this is the likely place for it to land.
    /// </summary>
    [Fact]
    public async Task WaitForEngineAsync_WhenTheCallerCancels_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        using var client = new CaptureClient(NewPipeName());

        // Long budget against a pipe nobody serves: the wait is parked inside the RPC, not
        // between polls, when the token fires.
        var wait = client.WaitForEngineAsync(TimeSpan.FromMinutes(1), cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    /// <summary>
    /// The shape every plugin will have: connect, subscribe a mixed ROI set, consume ticks until
    /// the engine says there are no more. The ending matters as much as the ticks — a plugin runs
    /// its finalisers off the stream completing, so a replay that finished without reaching the
    /// SDK would leave a tracker's last order uncommitted.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TrackAsync_OverReplayCorpus_YieldsEveryTickThenCompletes()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();

        var pipeName = NewPipeName();
        var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var frameCount = source.FrameCount;

        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            source, sink, verbose: false);
        await engine.StartAsync(cts.Token);

        // Started before anyone subscribes: the loop holds the corpus until a client is ready.
        var scan = engine.RunScanAsync(cts.Token);

        using var client = new CaptureClient(pipeName);
        await client.WaitForEngineAsync(ConnectTimeout, cts.Token);

        var ticks = new List<TickData>();
        await using (var session = await client.TrackAsync("test",
            [EngineTestFixtures.PanelStateSubscription(), EngineTestFixtures.ToggleStripSubscription()],
            cts.Token))
        {
            // Completing normally IS the assertion about replay end reaching the SDK: if the
            // engine failed to complete the stream this loop would hang until the timeout.
            await foreach (var tick in session.Ticks(cts.Token))
                ticks.Add(tick);
        }

        await scan;

        Assert.NotEmpty(ticks);
        Assert.Equal(frameCount, ticks.Count);

        for (var i = 0; i < ticks.Count; i++)
        {
            var tick = ticks[i];
            Assert.Equal((ulong)(i + 1), tick.FrameSeq);
            Assert.True(tick.FrameWidth > 0 && tick.FrameHeight > 0);
            Assert.False(tick.Manual);
            Assert.Null(tick.Error("panel"));
            Assert.Null(tick.Error("toggle"));

            var pixels = tick.Pixels("toggle");
            Assert.NotNull(pixels);
            Assert.True(pixels.Width > 0 && pixels.Height > 0);

            // Sampling by frame coordinates is the whole reason frame_rect crosses the wire: get
            // the origin wrong and this clamps to an edge or indexes out of the buffer. A patch
            // taken inside the ROI must therefore land on real pixels, not on the clamped black
            // an empty sampler returns.
            var (b, g, r) = pixels.AveragePatch(pixels.FrameX + pixels.Width / 2,
                pixels.FrameY + pixels.Height / 2);
            Assert.True(b > 0 || g > 0 || r > 0);
        }

        // The panel ROI is a text region in every fixture frame; if it OCRs empty everywhere, the
        // geometry made it across the wire wrong.
        Assert.Contains(ticks, t => t.Text("panel").Length > 0);
    }

    /// <summary>
    /// Re-subscribing without reopening the stream. A tracker changes its ROI set when the UI it
    /// watches changes screens, and it must not have to drop and re-establish the session — the
    /// gap would cost it ticks.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpdateRoisAsync_MidStream_LaterTicksCarryOnlyTheNewSet()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        using var sink = new ConsoleSink();

        var pipeName = NewPipeName();
        var source = new GatedFrameSource(EngineTestFixtures.ReplayDir);

        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            source, sink, verbose: false);
        await engine.StartAsync(cts.Token);

        var scan = engine.RunScanAsync(scanCts.Token);
        try
        {
            using var client = new CaptureClient(pipeName);
            await client.WaitForEngineAsync(ConnectTimeout, cts.Token);

            await using var session = await client.TrackAsync("switcher",
                [EngineTestFixtures.PanelStateSubscription()], cts.Token);

            await using var ticks = session.Ticks(cts.Token).GetAsyncEnumerator(cts.Token);

            source.Release();
            Assert.True(await ticks.MoveNextAsync());

            var beforeUpdate = ticks.Current;
            Assert.NotNull(beforeUpdate.Ocr("panel"));
            Assert.Null(beforeUpdate.Pixels("toggle"));

            await session.UpdateRoisAsync([EngineTestFixtures.ToggleStripSubscription()]);

            // The update is applied by the engine's request pump, which runs independently of the
            // scan loop, so no fixed number of ticks is both quick and safe: a frame already in
            // flight still carries the old set, and on a loaded machine the pump may not have been
            // scheduled at all yet. Read until the new set shows up rather than guessing how long
            // that takes; the enumerator's token bounds the wait, so a set that never arrives
            // fails the test instead of looping forever.
            TickData afterUpdate;
            do
            {
                source.Release();
                Assert.True(await ticks.MoveNextAsync());
                afterUpdate = ticks.Current;
            }
            while (afterUpdate.Pixels("toggle") is null);

            // Absent, not merely empty: a full replacement that behaved as a merge would still
            // answer Text("panel") with real OCR, and Ocr/Error tell absence from failure.
            Assert.Null(afterUpdate.Ocr("panel"));
            Assert.Null(afterUpdate.Error("panel"));
            Assert.Equal(string.Empty, afterUpdate.Text("panel"));
        }
        finally
        {
            // Stop the loop before the host disposes the gated source out from under it.
            scanCts.Cancel();
            try { await scan; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// The mapping on its own. The lookups are total by design and the three "nothing here"
    /// paths — absent id, engine-flagged error, and a successful read — must stay distinguishable,
    /// because a tracker that cannot tell them apart will treat a failed OCR as a cleared panel.
    /// </summary>
    [Fact]
    public void From_MapsResultsAndKeepsMissingAndErroredRoisApart()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var proto = new TickResult
        {
            TimestampMs = timestamp,
            FrameSeq = 7,
            FrameWidth = 2560,
            FrameHeight = 1440,
            Manual = true,
        };

        proto.Results.Add(new RoiResult
        {
            RoiId = "panel",
            Kind = RoiResultKind.Text,
            FrameRect = new RoiRect(900, 265, 250, 55).ToProto(),
            EffectiveScale = 3.0,
            Text = "PROCESSING",
        });

        proto.Results.Add(new RoiResult
        {
            RoiId = "toggle",
            Kind = RoiResultKind.Pixels,
            FrameRect = new RoiRect(640, 700, 2, 2).ToProto(),
            EffectiveScale = 1.0,
            PixelsBgra = Google.Protobuf.ByteString.CopyFrom(new byte[2 * 2 * 4]),
            PixelsStride = 2 * 4,
            PixelsWidth = 2,
            PixelsHeight = 2,
        });

        proto.Results.Add(new RoiResult { RoiId = "offscreen", Error = true, ErrorMessage = "outside the frame" });

        var tick = TickData.From(proto);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime, tick.Timestamp);
        Assert.Equal(7ul, tick.FrameSeq);
        Assert.Equal(2560, tick.FrameWidth);
        Assert.Equal(1440, tick.FrameHeight);
        Assert.True(tick.Manual);

        // Present and readable.
        Assert.Equal("PROCESSING", tick.Text("panel"));
        var ocr = tick.Ocr("panel");
        Assert.NotNull(ocr);
        Assert.Equal(900u, ocr.RoiX);
        Assert.Null(tick.Error("panel"));

        var pixels = tick.Pixels("toggle");
        Assert.NotNull(pixels);
        Assert.Equal(640, pixels.FrameX);
        Assert.Equal((0, 0, 0), pixels.AveragePatch(640, 700));

        // Missing: nothing was subscribed under this id, which is not a failure.
        Assert.Equal(string.Empty, tick.Text("nope"));
        Assert.Null(tick.Ocr("nope"));
        Assert.Null(tick.Pixels("nope"));
        Assert.Null(tick.Error("nope"));

        // Errored: the payload fields are unset, so every accessor reads as nothing — but Error
        // says why, and that is the difference a state machine has to act on.
        Assert.Equal("outside the frame", tick.Error("offscreen"));
        Assert.Equal(string.Empty, tick.Text("offscreen"));
        Assert.Null(tick.Ocr("offscreen"));
        Assert.Null(tick.Pixels("offscreen"));
    }

    /// <summary>
    /// Reading a ROI as the mode it was not subscribed as. The unfilled half of a result is all
    /// proto3 defaults, and those defaults are readable: without the kind check a text ROI hands
    /// back a valid 0x0 sampler that answers black forever, so a colour probe registered under
    /// the wrong RoiKind reads "off" for the life of the process with nothing flagging it.
    /// </summary>
    [Fact]
    public void From_OnAModeMixup_AnswersNothingRatherThanAnEmptyReading()
    {
        var proto = new TickResult { FrameWidth = 2560, FrameHeight = 1440 };

        proto.Results.Add(new RoiResult
        {
            RoiId = "probe_as_text",
            Kind = RoiResultKind.Text,
            FrameRect = new RoiRect(640, 700, 40, 40).ToProto(),
            EffectiveScale = 1.0,
        });

        proto.Results.Add(new RoiResult
        {
            RoiId = "panel_as_pixels",
            Kind = RoiResultKind.Pixels,
            FrameRect = new RoiRect(900, 265, 2, 2).ToProto(),
            EffectiveScale = 1.0,
            PixelsBgra = Google.Protobuf.ByteString.CopyFrom(new byte[2 * 2 * 4]),
            PixelsStride = 2 * 4,
            PixelsWidth = 2,
            PixelsHeight = 2,
        });

        var tick = TickData.From(proto);

        Assert.Null(tick.Pixels("probe_as_text"));
        Assert.Null(tick.Ocr("panel_as_pixels"));

        // A mixup is the client's bug, not a ROI the engine failed to read: Error stays null, as
        // TickData.Error documents for anything the boundary rejects rather than the engine did.
        Assert.Null(tick.Error("probe_as_text"));
        Assert.Null(tick.Error("panel_as_pixels"));

        // The correctly-read halves are untouched.
        Assert.NotNull(tick.Ocr("probe_as_text"));
        Assert.NotNull(tick.Pixels("panel_as_pixels"));
    }

    /// <summary>
    /// Disposing a session twice, which is what an `await using` plus an explicit cleanup path
    /// produces — TrackAsync's own failure handler is one. Cleanup code that throws is worse than
    /// no cleanup code, because it masks whatever sent the plugin down that path.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DisposeAsync_CalledTwice_IsQuiet()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();

        var pipeName = NewPipeName();
        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            new ReplayFrameSource(EngineTestFixtures.ReplayDir), sink, verbose: false);
        await engine.StartAsync(cts.Token);

        using var client = new CaptureClient(pipeName);
        await client.WaitForEngineAsync(ConnectTimeout, cts.Token);

        var session = await client.TrackAsync("disposer",
            [EngineTestFixtures.PanelStateSubscription()], cts.Token);

        await session.DisposeAsync();
        await session.DisposeAsync();

        // And the stream really is closed: a write after dispose is the caller's bug and must say
        // so, rather than throwing out of the semaphore the first dispose used to destroy.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.UpdateRoisAsync([EngineTestFixtures.ToggleStripSubscription()]));
    }
}
