using RefineryPlugin.Orders;
using GameCapture.Sdk;

// The class below shares its name with this namespace, which shadows the static Rois holder for any
// unqualified reference inside it (member lookup wins over enclosing-namespace lookup) — this alias
// is the least noisy way to reach Rois from there without spelling out `global::` each time. Same
// grandfathered shape as MissionPlugin.
using RefineryRois = global::RefineryPlugin.Rois;

namespace RefineryPlugin;

/// <summary>
/// Tracks refinery work orders across the SETUP / PROCESSING / COMPLETED panels. A thin lifecycle
/// shell over <see cref="RefineryLogic"/>: it owns the order ledger — opened on the first tick and
/// kept across reconnects — and hands each tick to the logic, which does the parsing, the
/// scroll-stitching and the state machine. Everything the split isolates (connecting, subscribing,
/// reconnecting, cancelling, summarising) is <see cref="GameCapturePluginHost"/>'s.
/// </summary>
public sealed class RefineryPlugin : IGameCapturePlugin
{
    private readonly RefineryConfig _config;
    private readonly Func<string?>? _ledgerOverride;
    private readonly Action<OrderLedger>? _onLedgerOpened;

    private OrderLedger? _ledger;
    private string _ledgerPath = "";
    private RefineryLogic? _logic;

    /// <param name="config">Plugin settings; the host reads its own <c>PipeName</c>/<c>SaveDebugFrames</c>
    /// from the same instance via <see cref="PluginHostOptions.Config"/>.</param>
    /// <param name="ledgerOverride">Resolves the <c>--ledger</c> CLI value when the ledger is opened,
    /// or null. A closure rather than a string because the host parses the argument after this plugin
    /// is constructed but before the first tick that reads it.</param>
    /// <param name="onLedgerOpened">Test seam: invoked with the ledger the moment it is opened (on the
    /// first tick), so a replay-parity test can assert on what a full host run wrote without a path to
    /// reload.</param>
    public RefineryPlugin(RefineryConfig config, Func<string?>? ledgerOverride = null,
        Action<OrderLedger>? onLedgerOpened = null)
    {
        _config = config;
        _ledgerOverride = ledgerOverride;
        _onLedgerOpened = onLedgerOpened;
    }

    public string Name => "refinery";

    public IReadOnlyList<RoiSubscription> Rois => RefineryRois.All;

    // SkipErrored, not the default AbortTick: the host would withdraw the whole tick on ANY errored
    // region, but this plugin's per-ROI granularity — abort the tick only on a failed panel or modal,
    // locally skip a failed setup-list / toggle-strip / yield read — is genuine domain logic and lives
    // in RefineryLogic. The host still latches once-per-change ROI-failure reporting on its behalf.
    public RoiErrorPolicy ErrorPolicy => RoiErrorPolicy.SkipErrored;

    public Task OnTickAsync(TickContext ctx, CancellationToken ct)
    {
        // Everything is built on the FIRST tick, kept for the rest of the run: this is the earliest
        // point the plugin holds an IPluginServices to open the ledger against (SessionEvent.Connected
        // carries no output handle), and by now services.Engine is the real connected engine, not the
        // placeholder — so ReplayMode is authoritative. A reconnect keeps both ledger and logic (the
        // _logic ??= guard), the same reason the monolith's RefineryRunner kept its factory result:
        // the merge is idempotent, so a panel still on screen after a reconnect re-observes into the
        // same record.
        _logic ??= Build(ctx.Services);
        return _logic.OnTickAsync(ctx.Tick, ct);
    }

    private RefineryLogic Build(IPluginServices services)
    {
        // Only the engine knows whether it is replaying a corpus, which is why the throwaway-vs-real
        // ledger decision waits until here. Warn goes to services.Log so the ledger's durability
        // diagnostics — malformed-line skips, read failures, and the "write failed, keeping in memory"
        // notice — are actually surfaced (the monolith passed sink.WriteLine for exactly this).
        var target = LedgerTargetResolver.Resolve(
            services.Engine.ReplayMode, _config.LedgerEnabled, _ledgerOverride?.Invoke(), _config.LedgerPath);
        _ledgerPath = target.Path;
        _ledger = new OrderLedger(_ledgerPath, services.Log);
        _ledger.Load();

        // The connect-time announcement the monolith printed, including the replay/disabled note that
        // tells the user a throwaway file is in use rather than their real orders.jsonl.
        services.Log($"Ledger:    {_ledgerPath}{target.Note}");
        _onLedgerOpened?.Invoke(_ledger);

        return new RefineryLogic(services, _ledger);
    }

    /// <summary>The end-of-run ledger summary the monolith printed: a count per state under the path
    /// it wrote to (content of the old <c>WriteLedgerSummary</c>).</summary>
    public IEnumerable<string> SummaryLines()
    {
        if (_ledger is null)
        {
            yield return "Ledger: not opened (never received a tick from an engine)";
            yield break;
        }

        yield return $"Ledger: {_ledger.All.Count} orders ({_ledgerPath})";
        foreach (var g in _ledger.All.GroupBy(w => w.State).OrderBy(g => g.Key))
            yield return $"  {g.Key}: {g.Count()}";
    }
}
