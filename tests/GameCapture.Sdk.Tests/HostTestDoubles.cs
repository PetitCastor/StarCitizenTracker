using GameCapture.Contracts;
using GameCapture.Contracts.Proto;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// An <see cref="IPluginOutput"/> that keeps what was written. The host's user-visible behaviour —
/// the summary, the waiting line, the reconnect notice — is output, so the tests need to read it
/// back; going through the real console would mean a process-global
/// <see cref="Console.SetOut"/> that no two parallel tests could hold at once.
/// </summary>
internal sealed class RecordingOutput : IPluginOutput
{
    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return _lines.ToArray(); }
    }

    /// <summary>Everything written, joined — for asserting on a fragment without caring which line
    /// it landed on.</summary>
    public string Text => string.Join(Environment.NewLine, Lines);

    public void WriteLine(string message = "")
    {
        lock (_gate) _lines.Add(message);
    }

    /// <summary>Dropped: a status row is transient by construction and asserting on one would be
    /// asserting on a redraw.</summary>
    public void UpdateStatus(string statusText) { }
}

/// <summary>
/// A plugin that does nothing but remember what the host did to it.
/// </summary>
internal sealed class StubPlugin : IGameCapturePlugin
{
    private readonly Func<TickContext, CancellationToken, Task>? _onTick;

    public StubPlugin(Func<TickContext, CancellationToken, Task>? onTick = null,
        RoiErrorPolicy errorPolicy = RoiErrorPolicy.PassThrough)
    {
        _onTick = onTick;
        ErrorPolicy = errorPolicy;
    }

    public string Name { get; init; } = "stub";

    public IReadOnlyList<RoiSubscription> Rois { get; init; } =
        [new RoiSubscription("panel", new RoiRect(10, 10, 40, 20), 1.0, RoiKind.Text)];

    public RoiErrorPolicy ErrorPolicy { get; }

    public List<SessionEvent> Events { get; } = [];
    public List<TickData> Ticks { get; } = [];
    public List<TickData> ManualTicks { get; } = [];

    /// <summary>Extra lines the host must print under its own summary.</summary>
    public List<string> Summary { get; } = [];

    public async Task OnTickAsync(TickContext ctx, CancellationToken ct)
    {
        Ticks.Add(ctx.Tick);
        if (_onTick is not null)
            await _onTick(ctx, ct);
    }

    public Task OnManualTickAsync(TickContext ctx, CancellationToken ct)
    {
        ManualTicks.Add(ctx.Tick);
        return OnTickAsync(ctx, ct);
    }

    public void OnSessionEvent(SessionEvent evt) => Events.Add(evt);

    public IEnumerable<string> SummaryLines() => Summary;
}

/// <summary>
/// Builds ticks the way the engine would have sent them — a <see cref="TickResult"/> through
/// <see cref="TickData.From"/> — rather than faking the SDK type, so a tick that could never arrive
/// on the wire cannot pass a test.
/// </summary>
internal static class TickFactory
{
    public static TickData Tick(ulong seq = 1, bool manual = false,
        params (string RoiId, string Text, bool Error)[] rois)
    {
        var proto = new TickResult
        {
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FrameSeq = seq,
            FrameWidth = 2560,
            FrameHeight = 1440,
            Manual = manual,
        };

        foreach (var (roiId, text, error) in rois)
        {
            proto.Results.Add(error
                ? new RoiResult { RoiId = roiId, Error = true, ErrorMessage = "ROI outside the frame." }
                : new RoiResult
                {
                    RoiId = roiId,
                    Kind = RoiResultKind.Text,
                    FrameRect = new RoiRect(10, 10, 40, 20).ToProto(),
                    EffectiveScale = 1.0,
                    Text = text,
                });
        }

        return TickData.From(proto);
    }
}
