using GameCapture.Contracts;
using Xunit;
using static GameCapture.Sdk.Tests.TickFactory;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// Everything the host decides about a tick before the plugin sees it. Driven directly rather than
/// through a session, because every case here is a combination — a gap AND a failed region, a policy
/// AND a plugin that throws — and staging those against a live engine means racing it.
/// </summary>
public class TickDispatcherTests
{
    private const string PanelRoi = "panel";
    private const string ToggleRoi = "toggle";

    private static TickDispatcher New(StubPlugin plugin, out RecordingOutput output)
    {
        output = new RecordingOutput();
        var services = new PluginServices([], output, verbose: true, dumpFrame: null);
        return new TickDispatcher(plugin, services, output);
    }

    private static StubPlugin TwoRoiPlugin(RoiErrorPolicy policy) => new(errorPolicy: policy)
    {
        Rois =
        [
            new RoiSubscription(PanelRoi, new RoiRect(10, 10, 40, 20), 1.0, RoiKind.Text),
            new RoiSubscription(ToggleRoi, new RoiRect(60, 10, 40, 20), 1.0, RoiKind.Text),
        ],
    };

    // ---------- dispatch ----------

    [Fact]
    public async Task AnOrdinaryTick_ReachesOnTickAsync()
    {
        var plugin = new StubPlugin();
        var dispatcher = New(plugin, out _);

        await dispatcher.DispatchAsync(Tick(1, rois: (PanelRoi, "SETUP", false)), default);

        Assert.Single(plugin.Ticks);
        Assert.Empty(plugin.ManualTicks);
    }

    [Fact]
    public async Task AManualTick_ReachesOnManualTickAsync()
    {
        var plugin = new StubPlugin();
        var dispatcher = New(plugin, out _);

        await dispatcher.DispatchAsync(Tick(1, manual: true, rois: (PanelRoi, "SETUP", false)), default);

        Assert.Single(plugin.ManualTicks);
    }

    /// <summary>
    /// One bad tick must not end the run: one unparseable frame out of thousands is normal, and a
    /// plugin that dies on it loses everything it had accumulated.
    /// </summary>
    [Fact]
    public async Task WhenThePluginThrows_TheFailureIsLoggedAndSwallowed()
    {
        var plugin = new StubPlugin((_, _) => throw new InvalidOperationException("parser exploded"))
        {
            Name = "refinery",
        };
        var dispatcher = New(plugin, out var output);

        await dispatcher.DispatchAsync(Tick(1, rois: (PanelRoi, "SETUP", false)), default);
        await dispatcher.DispatchAsync(Tick(2, rois: (PanelRoi, "SETUP", false)), default);

        // The second tick still arrived, which is the whole point.
        Assert.Equal(2, plugin.Ticks.Count);
        Assert.Equal(2, output.Lines.Count(l => l.Contains("refinery: tick failed: parser exploded")));
    }

    /// <summary>
    /// Cancellation is the one exception that must NOT be swallowed: it is the host shutting down,
    /// and treating it as a bad tick would spin the loop instead of ending the run.
    /// </summary>
    [Fact]
    public async Task WhenThePluginIsCancelled_TheCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var plugin = new StubPlugin((_, ct) => Task.FromCanceled(ct));
        var dispatcher = New(plugin, out _);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.DispatchAsync(Tick(1, rois: (PanelRoi, "", false)), cts.Token));
    }

    // ---------- dropped ticks ----------

    [Fact]
    public async Task ASequenceGap_RaisesTicksDroppedWithTheCount()
    {
        var plugin = new StubPlugin();
        var dispatcher = New(plugin, out _);

        await dispatcher.DispatchAsync(Tick(1, rois: (PanelRoi, "", false)), default);
        await dispatcher.DispatchAsync(Tick(5, rois: (PanelRoi, "", false)), default);

        var dropped = Assert.IsType<SessionEvent.TicksDropped>(Assert.Single(plugin.Events));
        Assert.Equal(3ul, dropped.Gap);

        // The tick itself is still delivered: the frames are gone, this one is not.
        Assert.Equal(2, plugin.Ticks.Count);
    }

    [Fact]
    public async Task ContiguousTicks_RaiseNothing()
    {
        var plugin = new StubPlugin();
        var dispatcher = New(plugin, out _);

        for (ulong seq = 1; seq <= 4; seq++)
            await dispatcher.DispatchAsync(Tick(seq, rois: (PanelRoi, "", false)), default);

        Assert.Empty(plugin.Events);
    }

    /// <summary>
    /// The reconnect case. The engine kept scanning while the client was away, so the first tick of
    /// the new session is legitimately far ahead — reporting that would fire the event on every
    /// reconnect there is, which is exactly what teaches an author to ignore it.
    /// </summary>
    [Fact]
    public async Task AfterOnConnected_TheNextTickIsNotAGap()
    {
        var plugin = new StubPlugin();
        var dispatcher = New(plugin, out _);

        await dispatcher.DispatchAsync(Tick(1, rois: (PanelRoi, "", false)), default);
        dispatcher.OnConnected();
        await dispatcher.DispatchAsync(Tick(900, rois: (PanelRoi, "", false)), default);

        Assert.Empty(plugin.Events);
    }

    // ---------- error policy ----------

    [Fact]
    public async Task AbortTick_WithdrawsATickWhoseSubscribedRoiFailed()
    {
        var plugin = TwoRoiPlugin(RoiErrorPolicy.AbortTick);
        var dispatcher = New(plugin, out var output);

        await dispatcher.DispatchAsync(
            Tick(1, rois: [(PanelRoi, "SETUP", false), (ToggleRoi, "", true)]), default);

        Assert.Empty(plugin.Ticks);
        Assert.Contains("ROI failure: toggle", output.Text);
        Assert.Contains("tick skipped", output.Text);
    }

    [Fact]
    public async Task AbortTick_DeliversATickWhoseRoisAllRead()
    {
        var plugin = TwoRoiPlugin(RoiErrorPolicy.AbortTick);
        var dispatcher = New(plugin, out _);

        await dispatcher.DispatchAsync(
            Tick(1, rois: [(PanelRoi, "SETUP", false), (ToggleRoi, "on", false)]), default);

        Assert.Single(plugin.Ticks);
    }

    [Fact]
    public async Task SkipErrored_DeliversTheTickAndNamesTheFailure()
    {
        var plugin = TwoRoiPlugin(RoiErrorPolicy.SkipErrored);
        var dispatcher = New(plugin, out var output);

        await dispatcher.DispatchAsync(
            Tick(1, rois: [(PanelRoi, "SETUP", false), (ToggleRoi, "", true)]), default);

        Assert.Single(plugin.Ticks);
        Assert.Contains("delivered anyway", output.Text);
    }

    [Fact]
    public async Task PassThrough_DeliversTheTickAndSaysNothing()
    {
        var plugin = TwoRoiPlugin(RoiErrorPolicy.PassThrough);
        var dispatcher = New(plugin, out var output);

        await dispatcher.DispatchAsync(
            Tick(1, rois: [(PanelRoi, "SETUP", false), (ToggleRoi, "", true)]), default);

        Assert.Single(plugin.Ticks);
        Assert.DoesNotContain("ROI failure", output.Text);
    }

    /// <summary>
    /// A ROI the plugin never subscribed is not the plugin's problem — the engine echoes ids back
    /// unvalidated, and a stray failed result must not withdraw every tick of the run.
    /// </summary>
    [Fact]
    public async Task AFailedRoiThePluginDidNotSubscribe_IsIgnored()
    {
        var plugin = TwoRoiPlugin(RoiErrorPolicy.AbortTick);
        var dispatcher = New(plugin, out _);

        await dispatcher.DispatchAsync(Tick(1, rois:
            [(PanelRoi, "SETUP", false), (ToggleRoi, "on", false), ("stray", "", true)]), default);

        Assert.Single(plugin.Ticks);
    }

    // ---------- the failure latch ----------

    /// <summary>
    /// A mistyped ROI constant fails on every frame, and at the engine's cadence an unlatched report
    /// is a line twice a second for as long as the plugin runs.
    /// </summary>
    [Fact]
    public async Task APersistentFailure_IsReportedOnce()
    {
        var plugin = TwoRoiPlugin(RoiErrorPolicy.AbortTick);
        var dispatcher = New(plugin, out var output);

        for (ulong seq = 1; seq <= 5; seq++)
            await dispatcher.DispatchAsync(
                Tick(seq, rois: [(PanelRoi, "SETUP", false), (ToggleRoi, "", true)]), default);

        Assert.Single(output.Lines, l => l.Contains("ROI failure"));
    }

    /// <summary>A second region going bad while the first is still bad is news.</summary>
    [Fact]
    public async Task AChangedFailureSet_IsReportedAgain()
    {
        var plugin = TwoRoiPlugin(RoiErrorPolicy.AbortTick);
        var dispatcher = New(plugin, out var output);

        await dispatcher.DispatchAsync(
            Tick(1, rois: [(PanelRoi, "SETUP", false), (ToggleRoi, "", true)]), default);
        await dispatcher.DispatchAsync(
            Tick(2, rois: [(PanelRoi, "", true), (ToggleRoi, "", true)]), default);

        Assert.Equal(2, output.Lines.Count(l => l.Contains("ROI failure")));
        Assert.Contains("ROI failure: panel, toggle", output.Text);
    }

    /// <summary>
    /// Recovery re-arms the latch, so an intermittent failure is reported each time it comes back
    /// rather than only the first time.
    /// </summary>
    [Fact]
    public async Task AFailureThatRecoversAndReturns_IsReportedTwice()
    {
        var plugin = TwoRoiPlugin(RoiErrorPolicy.AbortTick);
        var dispatcher = New(plugin, out var output);

        await dispatcher.DispatchAsync(
            Tick(1, rois: [(PanelRoi, "SETUP", false), (ToggleRoi, "", true)]), default);
        await dispatcher.DispatchAsync(
            Tick(2, rois: [(PanelRoi, "SETUP", false), (ToggleRoi, "on", false)]), default);
        await dispatcher.DispatchAsync(
            Tick(3, rois: [(PanelRoi, "SETUP", false), (ToggleRoi, "", true)]), default);

        Assert.Equal(2, output.Lines.Count(l => l.Contains("ROI failure")));
    }

    /// <summary>
    /// A reconnect is a new stretch — quite possibly against a restarted engine with a different
    /// frame size, which is one of the things that makes a ROI fail in the first place. Without the
    /// re-arm, an operator who reconnects while calibrating gets one warning for the whole run.
    /// </summary>
    [Fact]
    public async Task AFailureStillPresentAfterAReconnect_IsReportedAgain()
    {
        var plugin = TwoRoiPlugin(RoiErrorPolicy.AbortTick);
        var dispatcher = New(plugin, out var output);

        await dispatcher.DispatchAsync(
            Tick(1, rois: [(PanelRoi, "SETUP", false), (ToggleRoi, "", true)]), default);

        dispatcher.OnConnected();

        await dispatcher.DispatchAsync(
            Tick(2, rois: [(PanelRoi, "SETUP", false), (ToggleRoi, "", true)]), default);

        Assert.Equal(2, output.Lines.Count(l => l.Contains("ROI failure")));
    }

    /// <summary>
    /// The report goes through <see cref="IPluginServices.LogVerbose"/>, so a normal run stays quiet:
    /// it is a calibration diagnostic, not something a user is meant to read.
    /// </summary>
    [Fact]
    public async Task WithoutVerbose_TheFailureReportIsSilent()
    {
        var plugin = TwoRoiPlugin(RoiErrorPolicy.AbortTick);
        var output = new RecordingOutput();
        var services = new PluginServices([], output, verbose: false, dumpFrame: null);
        var dispatcher = new TickDispatcher(plugin, services, output);

        await dispatcher.DispatchAsync(
            Tick(1, rois: [(PanelRoi, "SETUP", false), (ToggleRoi, "", true)]), default);

        Assert.DoesNotContain("ROI failure", output.Text);

        // Still withdrawn, though: the policy is not a logging setting.
        Assert.Empty(plugin.Ticks);
    }
}
