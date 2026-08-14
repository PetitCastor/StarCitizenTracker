using TrackingService.Trackers;
using Xunit;

namespace TrackingService.Tests;

/// <summary>
/// Offline transition-table tests for <see cref="PanelStateMachine"/> — the pure seam extracted from
/// RefineryTracker so the panel lifecycle can be verified with no WinRT/OCR coupling.
/// </summary>
public class RefineryTrackerPanelStateTests
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
}
