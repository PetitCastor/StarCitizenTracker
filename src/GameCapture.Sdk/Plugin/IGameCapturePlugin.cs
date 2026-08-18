namespace GameCapture.Sdk;

/// <summary>
/// What a plugin is: a name, a set of regions, and something to do when a tick carrying them
/// arrives. Everything else — connecting, subscribing, reconnecting, cancelling, summarising — is
/// <see cref="GameCapturePluginHost"/>'s.
/// </summary>
/// <remarks>
/// Every member past <see cref="OnTickAsync"/> has a default implementation, so the smallest real
/// plugin is three members. That is the point: the ~90 lines of lifecycle each existing plugin
/// carries today were copied between them verbatim, comments included, and a copy is where the two
/// quietly stop agreeing about what a reconnect means.
/// </remarks>
public interface IGameCapturePlugin
{
    /// <summary>
    /// Identifies the plugin twice over: it is the client name on the Track stream (what the engine
    /// lists in its status, and what a user reads when two plugins fight over one engine) and the
    /// <see cref="CaptureRecord.Plugin"/> tag on everything emitted.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The regions to subscribe, in reference space. Read once per connect and sent as the initial
    /// subscription, so it must be complete before the first tick — per-tick atomicity means there
    /// is no mid-tick round-trip to add a region.
    /// </summary>
    IReadOnlyList<RoiSubscription> Rois { get; }

    /// <summary>
    /// What the host should do with a tick in which one of the subscribed regions failed to read.
    /// Defaults to <see cref="RoiErrorPolicy.AbortTick"/>: a parser that treats a failed region's
    /// empty text as a successfully read empty panel is the single most likely way to corrupt a
    /// state machine, and opting into seeing failures is safer than opting out.
    /// </summary>
    RoiErrorPolicy ErrorPolicy => RoiErrorPolicy.AbortTick;

    /// <summary>
    /// One scanned frame. Called sequentially — the host never overlaps two ticks — so a plugin's
    /// state needs no locking of its own.
    /// </summary>
    /// <remarks>
    /// Throwing here does not end the run: the host logs it and delivers the next tick, because one
    /// unparseable frame out of thousands is a normal event and a plugin that dies on it loses every
    /// order it had accumulated. A genuine transport failure is not routed through here at all.
    /// </remarks>
    Task OnTickAsync(TickContext ctx, CancellationToken ct);

    /// <summary>
    /// A tick on which the engine's hotkey was pressed. Defaults to
    /// <see cref="OnTickAsync"/>, which is right for a plugin whose manual capture is just its
    /// normal capture forced; override when the hotkey means something else — committing whatever
    /// is on screen right now, typically.
    /// </summary>
    Task OnManualTickAsync(TickContext ctx, CancellationToken ct) => OnTickAsync(ctx, ct);

    /// <summary>
    /// Connected, reconnecting, ticks dropped, ended. Called synchronously on the host's loop, so it
    /// must not block; anything slow belongs on the next tick.
    /// </summary>
    /// <remarks>
    /// Deliberately not cancellable and deliberately void: these are notifications, not work. A
    /// plugin that needs to persist something before the process exits does it in
    /// <see cref="SummaryLines"/>' caller order — that is, on the <see cref="SessionEvent.Ended"/>
    /// it receives before the summary is printed.
    /// </remarks>
    void OnSessionEvent(SessionEvent evt) { }

    /// <summary>
    /// Extra lines to print under the host's own end-of-run summary — a ledger's contents, a count
    /// per state, whatever the plugin counted that the host cannot see.
    /// </summary>
    IEnumerable<string> SummaryLines() => [];
}

/// <summary>
/// What the host does with a tick in which at least one subscribed region errored.
/// </summary>
/// <remarks>
/// A tick states per-region status explicitly (<see cref="TickData.Status"/>,
/// <see cref="TickData.TryGetText"/>), so a plugin CAN tell a failed region from an empty one. The
/// policy exists because most plugins would rather not: whether a degraded tick should reach the
/// parser at all is one decision, made once, instead of a check every reader has to remember.
/// </remarks>
public enum RoiErrorPolicy
{
    /// <summary>
    /// Skip the tick entirely. The safe default: nothing the plugin sees is ever a degraded reading.
    /// </summary>
    AbortTick,

    /// <summary>
    /// Deliver the tick, having logged which regions failed. The plugin is expected to check before
    /// trusting a reading — this is the mode for a plugin whose regions are genuinely independent.
    /// </summary>
    SkipErrored,

    /// <summary>
    /// Deliver the tick with no host-side filtering or logging at all. What both existing plugins do
    /// de facto today, named so it is a decision rather than an omission.
    /// </summary>
    PassThrough,
}
