using Xunit;

namespace RefineryPlugin.Tests;

/// <summary>
/// Offline transition-table tests for <see cref="PanelStateMachine"/> — the pure seam extracted from
/// RefineryTracker so the panel lifecycle can be verified with no WinRT/OCR coupling.
/// </summary>
public class PanelStateMachineTests
{
    private static readonly PanelObservation Setup = new(PanelState.Setup, false);
    private static readonly PanelObservation Processing = new(PanelState.Processing, false);
    private static readonly PanelObservation Completed = new(PanelState.Completed, false);
    private static readonly PanelObservation CompletedModal = new(PanelState.Completed, true);
    private static readonly PanelObservation Gone = new(PanelState.None, false);

    [Fact]
    public void Setup_ObservesSetup_NotOccluded()
    {
        var r = new PanelStateMachine().Step(Setup);
        Assert.Equal(LedgerAction.ObserveSetup, r.Action);
        Assert.False(r.Occluded);
    }

    [Fact]
    public void Processing_ObservesCompleted()
        => Assert.Equal(LedgerAction.ObserveCompleted, new PanelStateMachine().Step(Processing).Action);

    [Fact]
    public void Completed_ObservesCompleted_NotOccluded()
    {
        var r = new PanelStateMachine().Step(Completed);
        Assert.Equal(LedgerAction.ObserveCompleted, r.Action);
        Assert.False(r.Occluded);
    }

    [Fact]
    public void CompletedWithModal_ObservesCompleted_Occluded()
    {
        var r = new PanelStateMachine().Step(CompletedModal);
        Assert.Equal(LedgerAction.ObserveCompleted, r.Action);
        Assert.True(r.Occluded);
    }

    [Fact]
    public void Delivery_CompletedThenModalThenGone_MarksCollected()
    {
        var m = new PanelStateMachine();
        m.Step(Completed);
        m.Step(CompletedModal);

        var r = m.Step(Gone);

        Assert.Equal(LedgerAction.MarkCollected, r.Action);
    }

    [Fact]
    public void G2_CompletedThenGoneWithoutModal_LeavesReadyWithNote()
    {
        var m = new PanelStateMachine();
        m.Step(Completed);

        var r = m.Step(Gone);

        Assert.Equal(LedgerAction.None, r.Action);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public void G2_NoteEmittedOnce_NotRepeatedOnSubsequentGone()
    {
        var m = new PanelStateMachine();
        m.Step(Completed);
        Assert.NotNull(m.Step(Gone).Note);
        Assert.Null(m.Step(Gone).Note); // already resolved
    }

    [Fact]
    public void BackToBackOrders_NoPanelCloseBetween_ObservesEachState()
    {
        var m = new PanelStateMachine();

        var a = m.Step(Setup);      // order 1 setup
        var b = m.Step(Processing); // order 1 submitted
        var c = m.Step(Setup);      // order 2 setup, no close in between

        Assert.Equal(LedgerAction.ObserveSetup, a.Action);
        Assert.Equal(LedgerAction.ObserveCompleted, b.Action);
        Assert.Equal(LedgerAction.ObserveSetup, c.Action);
    }

    [Fact]
    public void ColdStart_DirectlyOnCompleted_ObservesWithNoPriorSetup()
        => Assert.Equal(LedgerAction.ObserveCompleted, new PanelStateMachine().Step(Completed).Action);

    [Fact]
    public void ColdStart_OnCompletedModalThenGone_MarksCollected()
    {
        var m = new PanelStateMachine();

        var occluded = m.Step(CompletedModal); // single modal tick registers the delivery
        var collected = m.Step(Gone);

        Assert.True(occluded.Occluded);
        Assert.Equal(LedgerAction.MarkCollected, collected.Action);
    }

    [Fact]
    public void GoneWithNothingSeen_IsNoop()
    {
        var r = new PanelStateMachine().Step(Gone);
        Assert.Equal(LedgerAction.None, r.Action);
        Assert.Null(r.Note);
    }

    [Fact]
    public void AfterCollected_SubsequentGone_IsNoop()
    {
        var m = new PanelStateMachine();
        m.Step(Completed);
        m.Step(CompletedModal);
        Assert.Equal(LedgerAction.MarkCollected, m.Step(Gone).Action);

        Assert.Equal(LedgerAction.None, m.Step(Gone).Action); // no repeat
    }

    // ---- Cancel vs delivery (the modal latch must not survive a dismissed modal) ----

    [Fact]
    public void Cancel_CompletedModalCancelledThenGone_NoCollect_G2NoteInstead()
    {
        // COMPLETED, modal appears, user hits CANCEL (modal gone, panel still COMPLETED), then the
        // panel closes. This must NEVER report MarkCollected — the order was never delivered.
        var m = new PanelStateMachine();
        m.Step(Completed);
        m.Step(CompletedModal);
        var afterCancel = m.Step(Completed); // modal dismissed, panel still showing COMPLETED

        var r = m.Step(Gone);

        Assert.Equal(LedgerAction.ObserveCompleted, afterCancel.Action);
        Assert.False(afterCancel.Occluded);
        Assert.Equal(LedgerAction.None, r.Action);
        Assert.NotNull(r.Note); // G2 residual, not a fabricated collect
    }

    [Fact]
    public void Delivery_CompletedModalThenGone_NoInterveningCompletedTick_StillMarksCollected()
    {
        // Sanity re-check of the legit path alongside the cancel test above: modal visible right up
        // to the panel closing (no intervening modal-less COMPLETED tick) still collects.
        var m = new PanelStateMachine();
        m.Step(Completed);
        m.Step(CompletedModal);
        m.Step(CompletedModal); // modal can linger several ticks — still fine

        var r = m.Step(Gone);

        Assert.Equal(LedgerAction.MarkCollected, r.Action);
    }

    [Fact]
    public void AmbiguousCase_ModalClearsOneTickBeforePanelCloses_TreatedAsCancel_ByDesign()
    {
        // Same four-tick shape as the cancel test: COMPLETED, COMPLETED+modal, COMPLETED (modal
        // already gone), then None. OCR alone cannot tell "the delivery closed and this modal-less
        // COMPLETED tick was a same-frame race" apart from "the user cancelled and the panel is just
        // sitting there" — the documented, intentional decision (see PanelStateMachine remarks) is to
        // treat any modal-less COMPLETED tick after the modal was seen as a reset. A real delivery
        // must keep the modal visible through to the tick the panel closes.
        var m = new PanelStateMachine();
        m.Step(Completed);
        m.Step(CompletedModal);
        m.Step(Completed); // modal already gone this tick, panel still open

        var r = m.Step(Gone); // panel closes on the very next tick

        Assert.Equal(LedgerAction.None, r.Action);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public void Cancel_ThenRealModalAgain_StillCollectsOnSubsequentDelivery()
    {
        // After a cancel resets the latch, the panel can still legitimately deliver later if the
        // modal is shown (and stays shown through to close) again.
        var m = new PanelStateMachine();
        m.Step(Completed);
        m.Step(CompletedModal);
        m.Step(Completed); // cancel: latch cleared

        m.Step(CompletedModal); // user re-opens confirm and this time delivers
        var r = m.Step(Gone);

        Assert.Equal(LedgerAction.MarkCollected, r.Action);
    }
}
