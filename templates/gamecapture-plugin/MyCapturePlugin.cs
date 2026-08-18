using GameCapture.Sdk;

namespace MyCapturePlugin;

/// <summary>
/// Watches one region for a counter and emits a record every time the value changes.
/// Replace this with your own tracking logic — the shape (ROI in, CaptureRecord out) stays the same.
/// </summary>
/// <remarks>
/// Named independently of the project (not after <c>sourceName</c>, unlike the namespace and the
/// file names): a class named after the project would collide with the namespace of the same name
/// on every unqualified reference to it — see the shadowing note on the <c>Rois</c> access below —
/// and, worse, a project name containing a dot (a normal .NET convention, e.g. <c>-n Acme.MyPlugin</c>)
/// would splice straight into the class declaration and fail to compile.
/// </remarks>
public sealed class CounterPlugin : IGameCapturePlugin
{
    private string? _last;

    /// <summary>The client name on the Track stream and the tag on every record emitted.</summary>
    public string Name => "MyCapturePlugin";

    // Namespace-qualified, not a bare `Rois.All`: this class implements the interface's own `Rois`
    // property below, and a member always shadows a same-named type for unqualified lookup inside
    // the class that declares it — the qualified form is what reaches the static holder instead.
    public IReadOnlyList<RoiSubscription> Rois => MyCapturePlugin.Rois.All;

    /// <summary>Default. The host skips any tick in which a subscribed region failed, so
    /// nothing below ever reads a degraded value.</summary>
    public RoiErrorPolicy ErrorPolicy => RoiErrorPolicy.AbortTick;

    public Task OnTickAsync(TickContext ctx, CancellationToken ct)
    {
        // TryGetText, not Text: a failed region and a genuinely blank panel both answer "",
        // and only the bool tells them apart.
        if (!ctx.Tick.TryGetText(MyCapturePlugin.Rois.Counter.Id, out var text))
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
    /// reading is emitted whether or not it changed — and, while you're calibrating, this is also
    /// where the region gets dumped so you can see exactly what the engine read (see README.md).</summary>
    public async Task OnManualTickAsync(TickContext ctx, CancellationToken ct)
    {
        if (!ctx.Tick.TryGetText(MyCapturePlugin.Rois.Counter.Id, out var text))
            return;

        var value = text.Trim();
        if (value.Length == 0)
            return;

        // Advance the same state the auto path keeps. Without this, a press on a value that
        // has not been seen yet emits it as Manual and the very next tick emits it again as
        // Auto — one screen, two records.
        _last = value;

        ctx.Services.Emit(new CaptureRecord(ctx.Tick.Timestamp, Name, TriggerKind.Manual, value));

        // Calibration aid, not tracking logic: emit the record first (above), then dump inside a
        // try — DumpFrameAsync returns null (and this is a no-op) unless config.json's
        // saveDebugFrames is true, which is the ordinary case.
        try
        {
            var png = await ctx.Services.DumpFrameAsync(MyCapturePlugin.Rois.Counter.Rect, "counter", ct);
            if (png is not null)
                ctx.Services.LogVerbose($"counter read '{value}' — frame dumped to {png}");
        }
        catch
        {
            // Debugging aid only: a failed dump must never take down the tick that already emitted.
        }
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
