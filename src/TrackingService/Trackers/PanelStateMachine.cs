namespace TrackingService.Trackers;

/// <summary>Which refinery panel the middle-column state header is showing this tick.</summary>
public enum PanelState { None, Setup, Processing, Completed }

/// <summary>What the tracker should do with the ledger this tick — the machine's pure output.</summary>
public enum LedgerAction
{
    /// <summary>Nothing to persist.</summary>
    None,
    /// <summary>Build a SETUP-panel observation from the accumulator and Observe it.</summary>
    ObserveSetup,
    /// <summary>Build a COMPLETED/PROCESSING-panel observation (rows + yield total) and Observe it.</summary>
    ObserveCompleted,
    /// <summary>Mark the last-observed order Collected (the panel has closed after a confirmed delivery).</summary>
    MarkCollected,
}

/// <summary>One tick of OCR input for the machine: the classified panel state and whether the
/// Confirm-Delivery modal is on screen.</summary>
public readonly record struct PanelObservation(PanelState State, bool ModalVisible);

/// <summary>The machine's decision for one tick.</summary>
/// <param name="Action">What to do with the ledger.</param>
/// <param name="Occluded">The modal is covering the yield column/total this tick, so the read is
/// untrusted — the observation must be filed as <c>Completeness.Unknown</c> and can never promote a
/// record to <c>Complete</c> (H3).</param>
/// <param name="Note">Optional verbose log line (e.g. the G2 "left Ready, no confirm modal" residual).</param>
public readonly record struct StepResult(LedgerAction Action, bool Occluded, string? Note);

/// <summary>
/// Pure per-tick transition logic for the refinery panels, extracted from <see cref="RefineryTracker"/>
/// so it can be table-tested offline with no WinRT/OCR coupling (the reason the old tracker's only
/// unit-testable seam was its accumulator).
/// </summary>
/// <remarks>
/// Supersedes the old <c>_processingWasVisible</c> tri-state rising-edge bookkeeping: because every
/// tick classifies the panel state afresh and the ledger merges idempotently by identity, a leftover
/// PROCESSING panel from a prior order can no longer false-commit a fresh accumulator — a new SETUP
/// simply starts a new accumulator, and duplicate observes collapse in the ledger. This directly
/// fixes the old back-to-back-orders-without-closing limitation.
///
/// Delivery is recognized as COMPLETED → Confirm modal → panel gone. COMPLETED then gone WITHOUT an
/// intervening modal (G2) is a documented residual: the record is left Ready and a note is emitted —
/// never a fabricated Collected.
/// </remarks>
internal sealed class PanelStateMachine
{
    private bool _completedSeen; // saw a COMPLETED (or materials-bearing) panel in the current cycle
    private bool _modalSeen;     // saw the Confirm-Delivery modal while completed

    public StepResult Step(PanelObservation observed)
    {
        // The modal is the delivery signal regardless of what the header classifies as this tick
        // (it can overlay the header). Only meaningful once we've actually seen the completed panel.
        if (observed.ModalVisible && _completedSeen)
            _modalSeen = true;

        switch (observed.State)
        {
            case PanelState.Setup:
                // A fresh setup starts a new order cycle; drop any prior completed/modal tracking.
                _completedSeen = false;
                _modalSeen = false;
                return new StepResult(LedgerAction.ObserveSetup, Occluded: false, Note: null);

            case PanelState.Processing:
                // PROCESSING lists materials + yields (highest-value observation point — visible for
                // hours), so it is observed like the completed panel and advances state to Processing.
                return new StepResult(LedgerAction.ObserveCompleted, observed.ModalVisible, Note: null);

            case PanelState.Completed:
                _completedSeen = true;
                // Register the modal on the same tick the completed panel first appears (the
                // top-of-method check can't, since _completedSeen was still false then) — so a
                // cold start landing straight on COMPLETED+modal still recognizes the delivery.
                if (observed.ModalVisible)
                    _modalSeen = true;
                return new StepResult(LedgerAction.ObserveCompleted, observed.ModalVisible, Note: null);

            case PanelState.None:
            default:
                // Panel gone. Act only once the modal has cleared and the panel has actually closed.
                if (_completedSeen && _modalSeen && !observed.ModalVisible)
                {
                    Reset();
                    return new StepResult(LedgerAction.MarkCollected, Occluded: false, Note: null);
                }

                if (_completedSeen && !_modalSeen && !observed.ModalVisible)
                {
                    // G2: completed panel closed without a confirm modal — leave the record Ready.
                    _completedSeen = false;
                    return new StepResult(LedgerAction.None, Occluded: false,
                        Note: "completed panel closed without a confirm modal — leaving order Ready");
                }

                return new StepResult(LedgerAction.None, Occluded: false, Note: null);
        }
    }

    private void Reset()
    {
        _completedSeen = false;
        _modalSeen = false;
    }
}
