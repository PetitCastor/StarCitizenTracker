namespace RefineryPlugin;

/// <summary>One tick's confirmed verdict from <see cref="SetupDepartureDebouncer"/>.</summary>
/// <param name="OpenedFresh">A brand-new SETUP session was just confirmed to start this tick (entering
/// SETUP is immediate/undebounced, mirroring the old tracker — only *leaving* SETUP needs proof).</param>
/// <param name="DepartedTo">Non-null exactly on the tick a SETUP departure is confirmed: the raw
/// panel state it departed to (<see cref="PanelState.None"/> for a cancel/abandon,
/// <see cref="PanelState.Processing"/> or <see cref="PanelState.Completed"/> for a submit).</param>
internal readonly record struct SetupTransition(bool OpenedFresh, PanelState? DepartedTo);

/// <summary>
/// Debounces the SETUP panel's *departure* so a single OCR-flicker tick can't reset the scroll-stitch
/// accumulator or fire a premature submit with a half-stitched order — restores, in spirit, the old
/// pre-rewrite tracker's <c>AnchorGoneThreshold</c> (dropped in the PanelStateMachine rewrite).
/// </summary>
/// <remarks>
/// Only the SETUP-accumulator *lifecycle bookkeeping* (reset-on-entry, submit-on-exit, cancel-clear)
/// goes through this debouncer — <see cref="RefineryLogic"/> still feeds every tick's raw
/// classification straight to <see cref="PanelStateMachine"/> and the panel-content readers, so a
/// panel that has genuinely already transitioned is still read immediately (no reading lag, no risk
/// of misreading a ROI meant for a different panel layout).
///
/// Entering SETUP is immediate (not debounced): a single SETUP tick starts a session right away, same
/// as the old tracker's Idle → Accumulating edge. Leaving SETUP requires the panel to read non-SETUP
/// for at least <see cref="_window"/> after the last SETUP reading — a raw SETUP reading at any point
/// pushes that anchor forward, so a session that blips away for a tick or two and comes right back is
/// treated as if nothing happened (no accumulator reset, no submit). The confirming ticks do NOT need
/// to be the same specific state (e.g. Processing then Completed then Completed still counts) — only
/// that none of them is SETUP — because once the panel is genuinely away from SETUP, which exact
/// non-SETUP state it settles on is irrelevant to "did SETUP really close."
/// </remarks>
/// <remarks>
/// The window is stated in wall time (from the tick's own <c>Timestamp</c>) rather than in a tick
/// count, because the plugin no longer owns the scan cadence — the engine reports it. The plugin
/// passes three scan intervals, so at the stock 500 ms this confirms on the third consecutive
/// non-SETUP tick exactly as the monolith's <c>AnchorGoneThreshold</c> of 3 did, but the rule now
/// survives an engine configured to scan at a different rate.
/// </remarks>
internal sealed class SetupDepartureDebouncer
{
    /// <summary>How long the panel must read non-SETUP, measured from the most recent SETUP reading,
    /// before a departure is trusted. Same spirit as the old tracker's <c>AnchorGoneThreshold</c>.</summary>
    private readonly TimeSpan _window;

    private bool _open;               // a SETUP session is currently believed open
    private DateTime _lastSetupSeen;  // timestamp of the most recent raw == Setup tick, while open

    public SetupDepartureDebouncer(TimeSpan window) => _window = window;

    /// <summary>Feeds one tick's raw panel classification and the frame's own timestamp.</summary>
    public SetupTransition Observe(PanelState raw, DateTime timestamp)
    {
        if (!_open)
        {
            if (raw != PanelState.Setup)
                return default; // still closed; nothing to do

            _open = true;
            _lastSetupSeen = timestamp;
            return new SetupTransition(OpenedFresh: true, DepartedTo: null);
        }

        if (raw == PanelState.Setup)
        {
            _lastSetupSeen = timestamp; // any SETUP reading proves the session never really left
            return default;
        }

        if (timestamp - _lastSetupSeen < _window)
            return default; // still within the grace window — too soon to trust the departure

        _open = false;
        return new SetupTransition(OpenedFresh: false, DepartedTo: raw);
    }
}
