using Xunit;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// Gap detection, which decides when <see cref="SessionEvent.TicksDropped"/> fires. Unit tests
/// because every interesting case is a boundary an integration test cannot reach on demand — the
/// first tick of a session, the first tick after a reconnect, an engine that restarted and is
/// counting from zero again.
/// </summary>
public class FrameSeqTrackerTests
{
    [Fact]
    public void FirstTick_IsNeverAGap()
    {
        var tracker = new FrameSeqTracker();

        // Even a large first sequence: the engine has been scanning since it started, and a plugin
        // that connects to a long-running engine has not "missed" the frames from before it existed.
        Assert.False(tracker.TryObserve(5_000, out var gap));
        Assert.Equal(0ul, gap);
    }

    [Fact]
    public void ContiguousTicks_AreNotAGap()
    {
        var tracker = new FrameSeqTracker();
        tracker.TryObserve(1, out _);

        Assert.False(tracker.TryObserve(2, out var gap));
        Assert.Equal(0ul, gap);
    }

    [Theory]
    [InlineData(1ul, 3ul, 1ul)]
    [InlineData(10ul, 15ul, 4ul)]
    [InlineData(0ul, 100ul, 99ul)]
    public void SkippedTicks_ReportTheCount(ulong first, ulong second, ulong expected)
    {
        var tracker = new FrameSeqTracker();
        tracker.TryObserve(first, out _);

        Assert.True(tracker.TryObserve(second, out var gap));
        Assert.Equal(expected, gap);
    }

    [Fact]
    public void RepeatedSequence_IsNotAGap()
    {
        var tracker = new FrameSeqTracker();
        tracker.TryObserve(7, out _);

        Assert.False(tracker.TryObserve(7, out var gap));
        Assert.Equal(0ul, gap);
    }

    /// <summary>
    /// The case unsigned arithmetic gets wrong if nobody thinks about it: a restarted engine counts
    /// from zero again, so <c>seq - last - 1</c> would wrap to something near ulong.MaxValue and
    /// report a restart as billions of dropped frames.
    /// </summary>
    [Fact]
    public void SequenceGoingBackwards_IsNotAnEnormousGap()
    {
        var tracker = new FrameSeqTracker();
        tracker.TryObserve(9_000, out _);

        Assert.False(tracker.TryObserve(1, out var gap));
        Assert.Equal(0ul, gap);
    }

    [Fact]
    public void AfterBackwardsJump_CountsFromTheNewSequence()
    {
        var tracker = new FrameSeqTracker();
        tracker.TryObserve(9_000, out _);
        tracker.TryObserve(1, out _);

        // The backwards tick was still recorded, so the next gap is measured against it.
        Assert.True(tracker.TryObserve(4, out var gap));
        Assert.Equal(2ul, gap);
    }

    /// <summary>
    /// Reset is what a reconnect does. Without it the first tick of the new session would be
    /// compared against the last of the old one — and the engine kept scanning the whole time the
    /// client was away, so the event would fire on every single reconnect.
    /// </summary>
    [Fact]
    public void Reset_MakesTheNextTickAFirstObservation()
    {
        var tracker = new FrameSeqTracker();
        tracker.TryObserve(10, out _);

        tracker.Reset();

        Assert.False(tracker.TryObserve(900, out var gap));
        Assert.Equal(0ul, gap);
    }
}
