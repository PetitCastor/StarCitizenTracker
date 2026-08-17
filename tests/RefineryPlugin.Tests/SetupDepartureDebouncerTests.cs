using Xunit;

namespace RefineryPlugin.Tests;

/// <summary>
/// Offline tests for <see cref="SetupDepartureDebouncer"/> — the pure seam that guards
/// <see cref="RefineryLogic"/>'s SETUP-accumulator lifecycle (reset-on-entry, submit-on-exit,
/// cancel-clear) against single-tick OCR flicker. See <see cref="RefineryLogic.OnTickAsync"/> for how
/// <see cref="SetupTransition.OpenedFresh"/> and <see cref="SetupTransition.DepartedTo"/> drive the
/// accumulator reset / submit / cancel-clear.
/// </summary>
/// <remarks>
/// The departure window is stated in wall time now, not a tick count. These tests advance a clock one
/// <see cref="Scan"/> per reading against a window of <c>3 * Scan</c>, so a departure confirms on the
/// third consecutive non-SETUP reading exactly as the monolith's three-tick rule did.
/// </remarks>
public class SetupDepartureDebouncerTests
{
    private static readonly TimeSpan Scan = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Window = 3 * Scan;
    private static readonly DateTime T0 = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int scans) => T0.Add(scans * Scan);

    [Fact]
    public void FirstSetupTick_OpensImmediately_NotDebounced()
    {
        var d = new SetupDepartureDebouncer(Window);
        var r = d.Observe(PanelState.Setup, At(0));
        Assert.True(r.OpenedFresh);
        Assert.Null(r.DepartedTo);
    }

    [Fact]
    public void NoneBeforeAnySetup_IsNoop()
    {
        var d = new SetupDepartureDebouncer(Window);
        var r = d.Observe(PanelState.None, At(0));
        Assert.False(r.OpenedFresh);
        Assert.Null(r.DepartedTo);
    }

    [Fact]
    public void OneTwoTickGlitchToNone_MidSetup_RevertsWithoutConfirming()
    {
        var d = new SetupDepartureDebouncer(Window);
        d.Observe(PanelState.Setup, At(0)); // opens
        d.Observe(PanelState.Setup, At(1));

        var glitch1 = d.Observe(PanelState.None, At(2)); // 1 away tick
        var glitch2 = d.Observe(PanelState.None, At(3)); // 2 away ticks — still short of the window
        var backToSetup = d.Observe(PanelState.Setup, At(4)); // reverts — the window never elapsed

        Assert.Null(glitch1.DepartedTo);
        Assert.Null(glitch2.DepartedTo);
        Assert.False(backToSetup.OpenedFresh); // still the SAME session — not a fresh open
        Assert.Null(backToSetup.DepartedTo);
    }

    [Fact]
    public void OneTwoTickGlitchToProcessing_MidSetup_DoesNotFireSubmit()
    {
        var d = new SetupDepartureDebouncer(Window);
        d.Observe(PanelState.Setup, At(0));

        var glitch = d.Observe(PanelState.Processing, At(1)); // 1 away tick — a spurious misread
        var reverted = d.Observe(PanelState.Setup, At(2));    // OCR corrects itself next tick

        Assert.Null(glitch.DepartedTo); // no submit signal
        Assert.False(reverted.OpenedFresh); // same session continues, accumulator untouched
    }

    [Fact]
    public void ThreeConsecutiveNoneTicks_ConfirmsCancel()
    {
        var d = new SetupDepartureDebouncer(Window);
        d.Observe(PanelState.Setup, At(0));

        d.Observe(PanelState.None, At(1));
        d.Observe(PanelState.None, At(2));
        var confirmed = d.Observe(PanelState.None, At(3)); // window elapsed since the last SETUP

        Assert.Equal(PanelState.None, confirmed.DepartedTo);
    }

    [Fact]
    public void ThreeConsecutiveNonSetupTicks_ConfirmsSubmit_EvenIfTheSpecificStateChanges()
    {
        // The window counts ANY non-SETUP reading, not a specific repeated value — a genuine
        // transition often shows Processing for a tick or two before settling on Completed, and all
        // of those ticks should count toward the same departure.
        var d = new SetupDepartureDebouncer(Window);
        d.Observe(PanelState.Setup, At(0));

        d.Observe(PanelState.Processing, At(1)); // away 1
        d.Observe(PanelState.Completed, At(2));  // away 2 (different value, still counts)
        var confirmed = d.Observe(PanelState.Completed, At(3)); // away 3

        Assert.Equal(PanelState.Completed, confirmed.DepartedTo);
    }

    [Fact]
    public void DepartureConfirmed_ReportedOnlyOnce()
    {
        var d = new SetupDepartureDebouncer(Window);
        d.Observe(PanelState.Setup, At(0));
        d.Observe(PanelState.None, At(1));
        d.Observe(PanelState.None, At(2));
        var first = d.Observe(PanelState.None, At(3));
        var second = d.Observe(PanelState.None, At(4)); // already closed — no repeat signal

        Assert.Equal(PanelState.None, first.DepartedTo);
        Assert.Null(second.DepartedTo);
    }

    [Fact]
    public void AfterConfirmedDeparture_NewSetupTick_OpensFreshSession()
    {
        var d = new SetupDepartureDebouncer(Window);
        d.Observe(PanelState.Setup, At(0));
        d.Observe(PanelState.None, At(1));
        d.Observe(PanelState.None, At(2));
        d.Observe(PanelState.None, At(3)); // confirmed closed

        var reopened = d.Observe(PanelState.Setup, At(4));

        Assert.True(reopened.OpenedFresh);
    }

    [Fact]
    public void RevertingResetsTheAwayStreakCompletely_NotJustPauses()
    {
        // Two away-ticks, revert, two more away-ticks — must NOT combine into a false confirmation:
        // the revert pushes the SETUP anchor forward, so the window restarts from it.
        var d = new SetupDepartureDebouncer(Window);
        d.Observe(PanelState.Setup, At(0));

        d.Observe(PanelState.None, At(1)); // away 1
        d.Observe(PanelState.None, At(2)); // away 2
        d.Observe(PanelState.Setup, At(3)); // revert — anchor moves to here

        var away1Again = d.Observe(PanelState.None, At(4));
        var away2Again = d.Observe(PanelState.None, At(5));

        Assert.Null(away1Again.DepartedTo);
        Assert.Null(away2Again.DepartedTo); // only 2 scans since the revert — window not yet elapsed
    }
}
