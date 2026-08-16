// TRANSITIONAL DUPLICATE of src/TrackingService/Trackers/SetupDepartureDebouncer.cs. The monolith
// stays untouched until ENGINE-SPLIT TASK-8, which deletes its refinery path; until then both copies
// are live and must be edited together.
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
/// as the old tracker's Idle → Accumulating edge. Leaving SETUP requires <see cref="ConfirmTicks"/>
/// consecutive non-SETUP ticks with no reversion back to SETUP in between — a raw SETUP reading at any
/// point resets the away-streak to zero, so a session that blips away for a tick or two and comes
/// right back is treated as if nothing happened (no accumulator reset, no submit). The confirming
/// ticks do NOT need to be the same specific state (e.g. Processing then Completed then Completed
/// still counts) — only that none of them is SETUP — because once the panel is genuinely away from
/// SETUP, which exact non-SETUP state it settles on is irrelevant to "did SETUP really close."
/// </remarks>
internal sealed class SetupDepartureDebouncer
{
    /// <summary>Consecutive non-SETUP ticks required before a SETUP departure is trusted. Same
    /// constant and spirit as the old tracker's <c>AnchorGoneThreshold</c>.</summary>
    internal const int ConfirmTicks = 3;

    private bool _open;      // a SETUP session is currently believed open
    private int _awayStreak; // consecutive raw != Setup ticks since the last raw == Setup, while open

    /// <summary>Feeds one tick's raw panel classification.</summary>
    public SetupTransition Observe(PanelState raw)
    {
        if (!_open)
        {
            if (raw != PanelState.Setup)
                return default; // still closed; nothing to do

            _open = true;
            _awayStreak = 0;
            return new SetupTransition(OpenedFresh: true, DepartedTo: null);
        }

        if (raw == PanelState.Setup)
        {
            _awayStreak = 0; // any SETUP reading proves the session never really left
            return default;
        }

        if (++_awayStreak < ConfirmTicks)
            return default; // still within the grace window — too soon to trust the departure

        _open = false;
        _awayStreak = 0;
        return new SetupTransition(OpenedFresh: false, DepartedTo: raw);
    }
}
