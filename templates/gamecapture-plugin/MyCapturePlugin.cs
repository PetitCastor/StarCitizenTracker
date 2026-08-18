using GameCapture.Contracts;
using GameCapture.Sdk;

// The plugin class below shares its name with this namespace, which shadows the static Rois
// holder for any unqualified reference inside it (member lookup wins over enclosing-namespace
// lookup) — this alias is the least noisy way to reach Rois from there without `global::` each
// time. Same pattern the shipped plugins use (see RefineryPlugin.cs).
using MyCapturePluginRois = global::MyCapturePlugin.Rois;

namespace MyCapturePlugin;

/// <summary>
/// The regions this plugin subscribes, in reference space (2560x1440). Static for the life of the
/// process because the host reads it once per connect and sends it as the initial subscription:
/// per-tick atomicity means there is no mid-tick round-trip that could add a region later.
/// </summary>
public static class Rois
{
    /// <summary>The panel line the counter lives on. Scale is the OCR upscale factor —
    /// small text needs 2-4; 0 means "engine default". Nudge the rect and scale once you have a
    /// real corpus: see the calibration workflow in README.md.</summary>
    public static readonly RoiSubscription Counter =
        new("counter", new RoiRect(1000, 110, 420, 100), 3.0, RoiKind.Text);

    /// <summary>A field, not <c>=> [Counter]</c>: the set never changes, and an
    /// expression-bodied property would build a fresh array on every read.</summary>
    public static readonly IReadOnlyList<RoiSubscription> All = [Counter];
}

/// <summary>
/// Watches one region for a counter and emits a record every time the value changes.
/// Replace this with your own tracking logic — the shape (ROI in, CaptureRecord out) stays the same.
/// </summary>
public sealed class MyCapturePlugin : IGameCapturePlugin
{
    private string? _last;

    /// <summary>The client name on the Track stream and the tag on every record emitted.</summary>
    public string Name => "MyCapturePlugin";

    public IReadOnlyList<RoiSubscription> Rois => MyCapturePluginRois.All;

    /// <summary>Default. The host skips any tick in which a subscribed region failed, so
    /// nothing below ever reads a degraded value.</summary>
    public RoiErrorPolicy ErrorPolicy => RoiErrorPolicy.AbortTick;

    public Task OnTickAsync(TickContext ctx, CancellationToken ct)
    {
        // TryGetText, not Text: a failed region and a genuinely blank panel both answer "",
        // and only the bool tells them apart.
        if (!ctx.Tick.TryGetText(MyCapturePluginRois.Counter.Id, out var text))
            return Task.CompletedTask;

        var value = text.Trim();
        if (value.Length == 0 || value == _last)
            return Task.CompletedTask;

        _last = value;

        // The tick's own timestamp, not DateTime.Now: the engine buffers a few ticks per
        // client, so processing time can trail the frame it describes.
        ctx.Services.Emit(new CaptureRecord(ctx.Tick.Timestamp, Name, TriggerKind.Auto, value));
        return Task.CompletedTask;
    }

    /// <summary>The hotkey means "capture what is on screen right now" here, so the current
    /// reading is emitted whether or not it changed.</summary>
    public Task OnManualTickAsync(TickContext ctx, CancellationToken ct)
    {
        if (!ctx.Tick.TryGetText(MyCapturePluginRois.Counter.Id, out var text))
            return Task.CompletedTask;

        var value = text.Trim();
        if (value.Length == 0)
            return Task.CompletedTask;

        // Advance the same state the auto path keeps. Without this, a press on a value that
        // has not been seen yet emits it as Manual and the very next tick emits it again as
        // Auto — one screen, two records.
        _last = value;

        ctx.Services.Emit(new CaptureRecord(ctx.Tick.Timestamp, Name, TriggerKind.Manual, value));
        return Task.CompletedTask;
    }

    /// <summary>Frames this plugin never saw. A tracker watching for an edge can miss it
    /// across a gap, so the next reading is re-reported as a fresh sighting rather than
    /// assumed to be the successor of the last one. A reconnect is NOT in here: the host
    /// deliberately keeps plugin state across one.</summary>
    public void OnSessionEvent(SessionEvent evt)
    {
        if (evt is SessionEvent.TicksDropped)
            _last = null;
    }

    public IEnumerable<string> SummaryLines() => [$"  last counter: {_last ?? "none"}"];
}
