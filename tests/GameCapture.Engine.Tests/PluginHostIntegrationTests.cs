using GameCapture.Sdk;
using Xunit;
using Xunit.Abstractions;

namespace GameCapture.Engine.Tests;

/// <summary>
/// <see cref="GameCapturePluginHost"/> against a real engine over a real pipe. The host is ~90 lines of
/// lifecycle whose every interesting decision is about a failure — a stream that ended, an engine
/// that vanished, a tick that threw — and none of those can be honestly staged against a stub of the
/// thing that is supposed to produce them.
/// </summary>
/// <remarks>
/// Lives with the engine's suite because this is the only project that can own both ends of the
/// pipe. <see cref="NullPlugin"/> is deliberately the plugin under all of it: a real parser's
/// behaviour would be indistinguishable from the host's in the assertions.
/// </remarks>
public class PluginHostIntegrationTests(ITestOutputHelper output)
{
    /// <summary>
    /// A hang bound, not a performance budget: the corpus is three frames of real Windows OCR, which
    /// measures in seconds. Anything near this means something is stuck rather than slow.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    private static string NewPipeName() => $"sc-host-{Guid.NewGuid():N}";

    /// <summary>
    /// Config loading off and the console left alone: a test host must not have a config.json written
    /// next to its assembly, and installing a Ctrl+C handler would take the test runner's own
    /// interrupt.
    /// </summary>
    private static PluginHostOptions Options(RecordingOutput sink, CancellationToken shutdown = default)
        => new()
        {
            Output = sink,
            ConfigFileName = null,
            HandleCancelKeyPress = false,
            ShutdownToken = shutdown,
            ReconnectDelay = TimeSpan.FromMilliseconds(50),
        };

    /// <summary>
    /// The acceptance test for the whole task: a plugin that is nothing but an
    /// <see cref="IGameCapturePlugin"/> runs to completion over the corpus without owning one line of
    /// lifecycle code, and sees every frame the engine scanned.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task OverReplayCorpus_DispatchesEveryTickThenEndsWithReplayCompleted()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var engineSink = new ConsoleSink();
        var hostOutput = new RecordingOutput();

        var pipeName = NewPipeName();
        var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var frameCount = source.FrameCount;

        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            source, engineSink, verbose: false);
        await engine.StartAsync(cts.Token);

        // Started before anyone subscribes: in replay the loop holds the corpus until a client is
        // ready, so no frame is burned before the plugin is listening.
        var scan = engine.RunScanAsync(cts.Token);

        var plugin = new NullPlugin();
        plugin.Summary.Add("null: nothing to report");

        var exit = await GameCapturePluginHost
            .RunAsync(plugin, ["--pipe", pipeName], Options(hostOutput, cts.Token))
            .WaitAsync(TestTimeout);

        await scan;
        await engine.StopAsync();

        output.WriteLine($"{frameCount} frame(s) replayed, {plugin.TickCount} tick(s) dispatched");

        Assert.False(cts.IsCancellationRequested, "timed out");
        Assert.Equal(0, exit);

        // Non-empty first: an empty corpus would satisfy the equality below while proving nothing.
        Assert.NotEqual(0, frameCount);
        Assert.Equal(frameCount, plugin.TickCount);

        var connected = Assert.Single(plugin.EventsOf<SessionEvent.Connected>());
        Assert.NotEmpty(connected.Engine.EngineVersion);
        Assert.True(connected.Engine.ReplayMode);
        Assert.Equal(1u, connected.Engine.NegotiatedProtocol);

        // ReplayCompleted rather than EngineShutdown: the engine ran out of corpus, and a plugin
        // that persists anything cares which of the two happened.
        var ended = Assert.Single(plugin.EventsOf<SessionEvent.Ended>());
        Assert.Equal(StreamEndReason.ReplayCompleted, ended.Reason);

        Assert.Empty(plugin.EventsOf<SessionEvent.Reconnecting>());
        Assert.Empty(plugin.EventsOf<SessionEvent.TicksDropped>());

        // The summary is the host's, and the plugin's lines sit under it.
        Assert.Contains("=== Summary: 0 captures ===", hostOutput.Lines);
        Assert.Contains("null: nothing to report", hostOutput.Lines);
    }

    /// <summary>
    /// One bad tick must not end the run. The monolith swallowed a tracker's exception per tick for
    /// the same reason: one unparseable frame out of thousands is normal, and a plugin that dies on
    /// it loses every order it had accumulated.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WhenEveryTickThrows_TheRunStillCompletes()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var engineSink = new ConsoleSink();
        var hostOutput = new RecordingOutput();

        var pipeName = NewPipeName();
        var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var frameCount = source.FrameCount;

        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            source, engineSink, verbose: false);
        await engine.StartAsync(cts.Token);
        var scan = engine.RunScanAsync(cts.Token);

        var plugin = new NullPlugin { ThrowOnTick = new InvalidOperationException("parser exploded") };

        var exit = await GameCapturePluginHost
            .RunAsync(plugin, ["--pipe", pipeName], Options(hostOutput, cts.Token))
            .WaitAsync(TestTimeout);

        await scan;
        await engine.StopAsync();

        Assert.Equal(0, exit);
        Assert.Equal(frameCount, plugin.TickCount);

        // Reported rather than swallowed silently, and naming the plugin: with two plugins on one
        // engine, "tick failed" alone does not say whose.
        Assert.Contains("null: tick failed: parser exploded", hostOutput.Text);

        Assert.Equal(StreamEndReason.ReplayCompleted,
            Assert.Single(plugin.EventsOf<SessionEvent.Ended>()).Reason);
    }

    /// <summary>
    /// AbortTick against a ROI the engine genuinely cannot read. The failure is real — an off-frame
    /// rect, the mistake a mistyped constant produces — rather than a synthesised error result, so
    /// the policy is tested against the thing it exists to protect against.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AbortTickPolicy_WithdrawsEveryTickThatCarriesAFailedRoi()
    {
        var (exit, plugin, hostOutput, frameCount) = await RunWithOffFrameRoiAsync(RoiErrorPolicy.AbortTick);

        Assert.Equal(0, exit);
        Assert.NotEqual(0, frameCount);

        // Not one tick reaches the plugin: every frame fails the same ROI.
        Assert.Equal(0, plugin.TickCount);

        // Reported once per failure stretch, not once per tick — at the engine's cadence the latter
        // is a line twice a second for as long as the plugin runs.
        Assert.Single(hostOutput.Lines, l => l.Contains("ROI failure"));

        // And the run still ends cleanly: a persistently failing ROI is a plugin bug to be reported,
        // not a reason for the host to give up.
        Assert.Equal(StreamEndReason.ReplayCompleted,
            Assert.Single(plugin.EventsOf<SessionEvent.Ended>()).Reason);
    }

    /// <summary>
    /// PassThrough is what both existing plugins do de facto today: the host filters nothing and the
    /// plugin decides. Named so it is a decision rather than an omission.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PassThroughPolicy_DeliversTicksWithFailedRoisAndSaysNothing()
    {
        var (exit, plugin, hostOutput, frameCount) = await RunWithOffFrameRoiAsync(RoiErrorPolicy.PassThrough);

        Assert.Equal(0, exit);
        Assert.Equal(frameCount, plugin.TickCount);
        Assert.DoesNotContain("ROI failure", hostOutput.Text);
    }

    /// <summary>
    /// SkipErrored delivers the tick having said which regions failed; the plugin is expected to
    /// check before trusting a reading.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SkipErroredPolicy_DeliversTheTickAndNamesTheFailedRoi()
    {
        var (exit, plugin, hostOutput, frameCount) = await RunWithOffFrameRoiAsync(RoiErrorPolicy.SkipErrored);

        Assert.Equal(0, exit);
        Assert.Equal(frameCount, plugin.TickCount);
        Assert.Contains("offscreen", hostOutput.Text);
    }

    /// <summary>
    /// The engine going away mid-session, which is the failure the whole reconnect loop exists for.
    /// The first engine serves the handshake and then dies without ever scanning a frame; the second
    /// takes over the same pipe and runs the corpus. A host that treated the drop as a clean stream
    /// end would exit 0 here with zero ticks — passing every other assertion in this file.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WhenTheEngineRestarts_TheHostReconnectsAndFinishesTheRun()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var engineSink = new ConsoleSink();
        var hostOutput = new RecordingOutput();

        var pipeName = NewPipeName();
        var plugin = new NullPlugin();

        // Scan loop deliberately not started: the client connects, subscribes, and is waiting on a
        // tick that will never come when the engine is pulled out from under it.
        var first = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            new ReplayFrameSource(EngineTestFixtures.ReplayDir), engineSink, verbose: false);
        await first.StartAsync(cts.Token);

        var run = GameCapturePluginHost.RunAsync(plugin, ["--pipe", pipeName], Options(hostOutput, cts.Token));

        await WaitUntilAsync(() => plugin.EventsOf<SessionEvent.Connected>().Count == 1, cts.Token);

        await first.DisposeAsync();

        // Same pipe, fresh engine, corpus running this time.
        var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var frameCount = source.FrameCount;

        await using var second = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            source, engineSink, verbose: false);
        await second.StartAsync(cts.Token);
        var scan = second.RunScanAsync(cts.Token);

        var exit = await run.WaitAsync(TestTimeout);

        await scan;
        await second.StopAsync();

        output.WriteLine($"{plugin.TickCount} tick(s) after reconnect; events: " +
            string.Join(", ", plugin.Events.Select(e => e.GetType().Name)));

        Assert.False(cts.IsCancellationRequested, "timed out");
        Assert.Equal(0, exit);

        // Reconnected rather than exited: two connects, at least one reconnect notice between them.
        Assert.Equal(2, plugin.EventsOf<SessionEvent.Connected>().Count);
        Assert.NotEmpty(plugin.EventsOf<SessionEvent.Reconnecting>());
        Assert.Contains("engine connection lost — reconnecting", hostOutput.Text);

        // The attempt counter restarts per disconnected stretch, so the first notice of this one is 1.
        Assert.Equal(1, plugin.EventsOf<SessionEvent.Reconnecting>()[0].Attempt);

        // And the second engine's corpus actually reached the plugin.
        Assert.Equal(frameCount, plugin.TickCount);
        Assert.Equal(StreamEndReason.ReplayCompleted,
            Assert.Single(plugin.EventsOf<SessionEvent.Ended>()).Reason);

        // The gap tracker was reset across the reconnect: the second engine's sequence numbers start
        // over, and reporting that as dropped ticks would fire the event on every reconnect there is.
        Assert.Empty(plugin.EventsOf<SessionEvent.TicksDropped>());
    }

    /// <summary>
    /// Ctrl+C mid-run. The host installs a handler that does nothing but cancel this token, so
    /// cancelling it drives the identical path without the test process interrupting itself.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WhenCancelledMidSession_ExitsZeroWithTheSummary()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        using var engineSink = new ConsoleSink();
        var hostOutput = new RecordingOutput();

        var pipeName = NewPipeName();
        var plugin = new NullPlugin();

        // No scan loop: the plugin is parked on a stream that is alive and silent, which is where a
        // real Ctrl+C almost always lands.
        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            new ReplayFrameSource(EngineTestFixtures.ReplayDir), engineSink, verbose: false);
        await engine.StartAsync(cts.Token);

        var run = GameCapturePluginHost.RunAsync(plugin, ["--pipe", pipeName],
            Options(hostOutput, shutdown.Token));

        await WaitUntilAsync(() => plugin.EventsOf<SessionEvent.Connected>().Count == 1, cts.Token);
        await shutdown.CancelAsync();

        var exit = await run.WaitAsync(TestTimeout);

        Assert.Equal(0, exit);
        Assert.Contains("=== Summary: 0 captures ===", hostOutput.Lines);

        // Cancelled, not EngineShutdown: the engine is still up, and this plugin chose to stop.
        Assert.Equal(StreamEndReason.Cancelled,
            Assert.Single(plugin.EventsOf<SessionEvent.Ended>()).Reason);
    }

    /// <summary>
    /// Runs the corpus with a plugin subscribing one readable ROI and one that cannot be read at all,
    /// under the given policy.
    /// </summary>
    private async Task<(int Exit, NullPlugin Plugin, RecordingOutput Output, int FrameCount)>
        RunWithOffFrameRoiAsync(RoiErrorPolicy policy)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var engineSink = new ConsoleSink();
        var hostOutput = new RecordingOutput();

        var pipeName = NewPipeName();
        var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var frameCount = source.FrameCount;

        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            source, engineSink, verbose: false);
        await engine.StartAsync(cts.Token);
        var scan = engine.RunScanAsync(cts.Token);

        var plugin = new NullPlugin(policy)
        {
            Rois =
            [
                EngineTestFixtures.PanelStateSubscription(),
                RoiSubscription.FromProto(EngineTestFixtures.OffFrameRoi()),
            ],
        };

        // Verbose so the host's ROI-failure line is written at all: it is diagnostic, and a plugin
        // author reads it while calibrating rather than during a normal run.
        var exit = await GameCapturePluginHost
            .RunAsync(plugin, ["--pipe", pipeName, "--verbose"], Options(hostOutput, cts.Token))
            .WaitAsync(TestTimeout);

        await scan;
        await engine.StopAsync();

        Assert.False(cts.IsCancellationRequested, "timed out");
        return (exit, plugin, hostOutput, frameCount);
    }

    /// <summary>
    /// Polls a condition the host reaches on its own schedule. A fixed delay would either be flaky on
    /// a loaded box or slow on an idle one, and there is no event to await — the host raises its
    /// notifications into the plugin, which is what is being polled.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(25, ct);
        }
    }
}
