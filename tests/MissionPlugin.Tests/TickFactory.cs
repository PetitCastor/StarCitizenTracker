using CaptureContracts;
using CaptureContracts.Proto;
using MissionPlugin;
using TrackerSdk;

namespace MissionPlugin.Tests;

/// <summary>
/// Builds ticks the way the engine would have sent them — a <see cref="TickResult"/> through
/// <see cref="TickData.From"/> — rather than faking the SDK type. The mapping (kind checks,
/// frame_rect, effective_scale) is part of what the logic depends on, so a hand-made shortcut
/// around it would let a tick that could never arrive on the wire pass a test.
/// </summary>
internal static class TickFactory
{
    // Interlocked because xUnit runs test classes in parallel and this factory is shared.
    private static long _seq;

    public static TickData Tick(string tabText, string paneText = "", bool manual = false)
    {
        var proto = new TickResult
        {
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FrameSeq = (ulong)Interlocked.Increment(ref _seq),
            FrameWidth = 2560,
            FrameHeight = 1440,
            Manual = manual,
        };

        proto.Results.Add(TextResult(Rois.Tab, tabText));
        proto.Results.Add(TextResult(Rois.Pane, paneText));

        return TickData.From(proto);
    }

    /// <summary>Reference space == frame space here: the fixtures are 2560x1440, so the engine
    /// would have scaled by 1 and echoed the subscribed rect straight back.</summary>
    private static RoiResult TextResult(RoiSubscription roi, string text) => new()
    {
        RoiId = roi.Id,
        Kind = RoiResultKind.Text,
        FrameRect = roi.Rect.ToProto(),
        EffectiveScale = roi.Scale,
        Text = text,
    };
}
