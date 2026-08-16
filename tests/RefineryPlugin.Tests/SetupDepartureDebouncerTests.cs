using Xunit;

namespace RefineryPlugin.Tests;

/// <summary>
/// Offline tests for <see cref="SetupDepartureDebouncer"/> — the pure seam that guards RefineryTracker's
/// SETUP-accumulator lifecycle (reset-on-entry, submit-on-exit, cancel-clear) against single-tick OCR
/// flicker. See RefineryLogic.ScanAsync for how <see cref="SetupTransition.OpenedFresh"/> and
/// <see cref="SetupTransition.DepartedTo"/> drive the accumulator reset / submit / cancel-clear.
/// </summary>
public class SetupDepartureDebouncerTests
{
    [Fact]
    public void FirstSetupTick_OpensImmediately_NotDebounced()
    {
        var d = new SetupDepartureDebouncer();
        var r = d.Observe(PanelState.Setup);
        Assert.True(r.OpenedFresh);
        Assert.Null(r.DepartedTo);
    }

    [Fact]
    public void NoneBeforeAnySetup_IsNoop()
    {
        var d = new SetupDepartureDebouncer();
        var r = d.Observe(PanelState.None);
        Assert.False(r.OpenedFresh);
        Assert.Null(r.DepartedTo);
    }

    [Fact]
    public void OneTwoTickGlitchToNone_MidSetup_RevertsWithoutConfirming()
    {
        var d = new SetupDepartureDebouncer();
        d.Observe(PanelState.Setup); // opens
        d.Observe(PanelState.Setup);

        var glitch1 = d.Observe(PanelState.None); // 1 away tick
        var glitch2 = d.Observe(PanelState.None); // 2 away ticks — still short of ConfirmTicks (3)
        var backToSetup = d.Observe(PanelState.Setup); // reverts — the streak never completed

        Assert.Null(glitch1.DepartedTo);
        Assert.Null(glitch2.DepartedTo);
        Assert.False(backToSetup.OpenedFresh); // still the SAME session — not a fresh open
        Assert.Null(backToSetup.DepartedTo);
    }

    [Fact]
    public void OneTwoTickGlitchToProcessing_MidSetup_DoesNotFireSubmit()
    {
        var d = new SetupDepartureDebouncer();
        d.Observe(PanelState.Setup);

        var glitch = d.Observe(PanelState.Processing); // 1 away tick — a spurious misread
        var reverted = d.Observe(PanelState.Setup);    // OCR corrects itself next tick

        Assert.Null(glitch.DepartedTo); // no submit signal
        Assert.False(reverted.OpenedFresh); // same session continues, accumulator untouched
    }

    [Fact]
    public void ThreeConsecutiveNoneTicks_ConfirmsCancel()
    {
        var d = new SetupDepartureDebouncer();
        d.Observe(PanelState.Setup);

        d.Observe(PanelState.None);
        d.Observe(PanelState.None);
        var confirmed = d.Observe(PanelState.None); // 3rd consecutive away tick

        Assert.Equal(PanelState.None, confirmed.DepartedTo);
    }

    [Fact]
    public void ThreeConsecutiveNonSetupTicks_ConfirmsSubmit_EvenIfTheSpecificStateChanges()
    {
        // The away-streak counts ANY non-SETUP reading, not a specific repeated value — a genuine
        // transition often shows Processing for a tick or two before settling on Completed, and all
        // of those ticks should count toward the same departure.
        var d = new SetupDepartureDebouncer();
        d.Observe(PanelState.Setup);

        d.Observe(PanelState.Processing); // away 1
        d.Observe(PanelState.Completed);  // away 2 (different value, still counts)
        var confirmed = d.Observe(PanelState.Completed); // away 3

        Assert.Equal(PanelState.Completed, confirmed.DepartedTo);
    }

    [Fact]
    public void DepartureConfirmed_ReportedOnlyOnce()
    {
        var d = new SetupDepartureDebouncer();
        d.Observe(PanelState.Setup);
        d.Observe(PanelState.None);
        d.Observe(PanelState.None);
        var first = d.Observe(PanelState.None);
        var second = d.Observe(PanelState.None); // already closed — no repeat signal

        Assert.Equal(PanelState.None, first.DepartedTo);
        Assert.Null(second.DepartedTo);
    }

    [Fact]
    public void AfterConfirmedDeparture_NewSetupTick_OpensFreshSession()
    {
        var d = new SetupDepartureDebouncer();
        d.Observe(PanelState.Setup);
        d.Observe(PanelState.None);
        d.Observe(PanelState.None);
        d.Observe(PanelState.None); // confirmed closed

        var reopened = d.Observe(PanelState.Setup);

        Assert.True(reopened.OpenedFresh);
    }

    [Fact]
    public void RevertingResetsTheAwayStreakCompletely_NotJustPauses()
    {
        // Two away-ticks, revert, two more away-ticks — must NOT combine into a false 4-tick streak.
        var d = new SetupDepartureDebouncer();
        d.Observe(PanelState.Setup);

        d.Observe(PanelState.None); // away 1
        d.Observe(PanelState.None); // away 2
        d.Observe(PanelState.Setup); // revert — streak drops to 0

        var away1Again = d.Observe(PanelState.None);
        var away2Again = d.Observe(PanelState.None);

        Assert.Null(away1Again.DepartedTo);
        Assert.Null(away2Again.DepartedTo); // only 2 consecutive since the revert — not yet confirmed
    }
}
