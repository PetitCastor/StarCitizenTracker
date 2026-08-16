namespace TrackerSdk;

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
    private readonly ITrackerPlugin _plugin;
    private readonly PluginServices _services;
    private readonly IPluginOutput _output;
    private readonly FrameSeqTracker _seq = new();
    private readonly RoiFailureLatch _failures = new();

    public TickDispatcher(ITrackerPlugin plugin, PluginServices services, IPluginOutput output)
    {
        _plugin = plugin;
        _services = services;
        _output = output;
    }

    /// <summary>
    /// A new session started. Resets the sequence baseline: the engine kept scanning while the client
    /// was away, so the first tick of the new session is legitimately far ahead of the last of the
    /// old one, and reporting that as dropped ticks would fire the event on every reconnect there is.
    /// </summary>
    public void OnConnected() => _seq.Reset();

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
    /// Asks per subscribed id rather than reading a list off the tick because that list does not
    /// exist yet — TASK-08 adds <c>TickData.ErroredRois</c> and this collapses into it. Only ids the
    /// plugin actually subscribed are consulted either way: an engine echoing back something else is
    /// not this plugin's problem.
    /// </remarks>
    private IReadOnlyList<string> ErroredRois(TickData tick)
    {
        List<string>? errored = null;
        foreach (var roi in _plugin.Rois)
        {
            if (tick.Error(roi.Id) is not null)
                (errored ??= []).Add(roi.Id);
        }

        return errored ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Remembers which regions were failing so a persistent failure is reported once rather than on
    /// every tick, and reported again when the SET of failures changes — a second ROI going bad while
    /// the first is still bad is news.
    /// </summary>
    private sealed class RoiFailureLatch
    {
        private string _reported = "";

        public bool ShouldReport(IReadOnlyList<string> errored)
        {
            // ROI ids are client-chosen and could contain anything printable, so the separator is
            // the ASCII unit separator rather than a comma.
            var key = errored.Count == 0 ? "" : string.Join('', errored);
            if (key == _reported)
                return false;

            _reported = key;
            return errored.Count > 0;
        }
    }
}
