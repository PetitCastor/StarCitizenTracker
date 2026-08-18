namespace GameCapture.Sdk;

/// <summary>
/// Everything the host decides about one tick before the plugin sees it: whether frames were
/// missed, whether a failed region should withdraw the tick, which of the two tick methods to call,
/// and what to do when the plugin throws.
/// </summary>
/// <remarks>
/// Its own type because these decisions have state that outlives a single tick — the last sequence
/// number, and which regions were already reported as failing — and because they are the part of
/// the host worth testing without a pipe. The connect/reconnect loop around it has nothing to say
/// about any of it beyond calling <see cref="OnConnected"/>.
/// </remarks>
internal sealed class TickDispatcher
{
    private readonly IGameCapturePlugin _plugin;
    private readonly PluginServices _services;
    private readonly IPluginOutput _output;
    private readonly FrameSeqTracker _seq = new();
    private readonly RoiFailureLatch _failures = new();

    public TickDispatcher(IGameCapturePlugin plugin, PluginServices services, IPluginOutput output)
    {
        _plugin = plugin;
        _services = services;
        _output = output;
    }

    /// <summary>
    /// A new session started.
    /// </summary>
    /// <remarks>
    /// Resets the sequence baseline, because the engine kept scanning while the client was away: the
    /// first tick of the new session is legitimately far ahead of the last of the old one, and
    /// reporting that as dropped ticks would fire the event on every reconnect there is.
    /// <para>
    /// Re-arms the failure latch too. A reconnect is a new stretch — quite possibly against a
    /// restarted engine with a different frame size, which is one of the things that makes a ROI fail
    /// — and an operator who reconnects while calibrating would otherwise get one warning for the
    /// whole run instead of one per session.
    /// </para>
    /// </remarks>
    public void OnConnected()
    {
        _seq.Reset();
        _failures.Reset();
    }

    /// <summary>Applies the policies and hands the tick to the plugin.</summary>
    public async Task DispatchAsync(TickData tick, CancellationToken ct)
    {
        if (_seq.TryObserve(tick.FrameSeq, out var gap))
            _plugin.OnSessionEvent(new SessionEvent.TicksDropped(gap));

        var policy = _plugin.ErrorPolicy;
        if (policy != RoiErrorPolicy.PassThrough)
        {
            var errored = ErroredRois(tick);

            // Reported once per failure stretch, not per tick: a mistyped ROI constant fails on every
            // frame, and at the engine's cadence that is a line twice a second for as long as the
            // plugin runs.
            if (_failures.ShouldReport(errored))
                _services.LogVerbose(
                    $"[{_plugin.Name}] ROI failure: {string.Join(", ", errored)} — " +
                    (policy == RoiErrorPolicy.AbortTick ? "tick skipped" : "delivered anyway"));

            if (policy == RoiErrorPolicy.AbortTick && errored.Count > 0)
                return;
        }

        // As the monolith did per tracker: one bad tick must not end the run. A genuine transport
        // failure is not swallowed — the next read from the stream raises it again and the host's
        // reconnect handles it.
        try
        {
            var ctx = new TickContext(tick, _services);
            await (tick.Manual
                ? _plugin.OnManualTickAsync(ctx, ct)
                : _plugin.OnTickAsync(ctx, ct));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {_plugin.Name}: tick failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Which of the subscribed regions the engine flagged as failed on this tick.
    /// </summary>
    /// <remarks>
    /// Intersected with the plugin's own subscription rather than taken from
    /// <see cref="TickData.ErroredRois"/> wholesale: the engine echoes ids back unvalidated, and a
    /// stray failed result must not withdraw every tick of a run under
    /// <see cref="RoiErrorPolicy.AbortTick"/>. <see cref="TickData.HasErrors"/> short-circuits the
    /// walk on the ticks that are fine, which is nearly all of them.
    /// </remarks>
    private IReadOnlyList<RoiId> ErroredRois(TickData tick)
    {
        if (!tick.HasErrors)
            return [];

        List<RoiId>? errored = null;
        foreach (var roi in _plugin.Rois)
        {
            if (tick.Status(roi.Id) == RoiStatus.Failed)
                (errored ??= []).Add(roi.Id);
        }

        return errored ?? (IReadOnlyList<RoiId>)[];
    }

    /// <summary>
    /// Remembers which regions were failing so a persistent failure is reported once rather than on
    /// every tick, and reported again when the SET of failures changes — a second ROI going bad while
    /// the first is still bad is news.
    /// </summary>
    private sealed class RoiFailureLatch
    {
        private string _reported = "";

        public bool ShouldReport(IReadOnlyList<RoiId> errored)
        {
            // ROI ids are client-chosen and could contain anything printable, so the separator is
            // the ASCII unit separator rather than a comma.
            var key = errored.Count == 0 ? "" : string.Join('', errored);
            if (key == _reported)
                return false;

            _reported = key;
            return errored.Count > 0;
        }

        /// <summary>Forgets what was reported, so the next failure is news again.</summary>
        public void Reset() => _reported = "";
    }
}
