using RefineryPlugin.Orders;
using TrackerSdk;

// The class below shares its name with this namespace, which shadows the static Rois holder for any
// unqualified reference inside it (member lookup wins over enclosing-namespace lookup) — this alias
// is the least noisy way to reach Rois from there without spelling out `global::` each time. Same
// grandfathered shape as MissionPlugin.
using RefineryRois = global::RefineryPlugin.Rois;

namespace RefineryPlugin;

/// <summary>
/// Tracks refinery work orders across the SETUP / PROCESSING / COMPLETED panels. A thin lifecycle
/// shell over <see cref="RefineryLogic"/>: it owns the order ledger — opened on the first connect and
/// kept across reconnects — and hands each tick to the logic, which does the parsing, the
/// scroll-stitching and the state machine. Everything the split isolates (connecting, subscribing,
/// reconnecting, cancelling, summarising) is <see cref="TrackerPluginHost"/>'s.
/// </summary>
public sealed class RefineryPlugin : ITrackerPlugin
{
    private readonly RefineryConfig _config;
    private readonly Func<string?>? _ledgerOverride;
    private readonly Action<OrderLedger>? _onLedgerOpened;

    private OrderLedger? _ledger;
    private string _ledgerPath = "";
    private RefineryLogic? _logic;

    /// <param name="config">Plugin settings; the host reads its own <c>PipeName</c>/<c>SaveDebugFrames</c>
    /// from the same instance via <see cref="PluginHostOptions.Config"/>.</param>
    /// <param name="ledgerOverride">Resolves the <c>--ledger</c> CLI value at connect time, or null.
    /// A closure rather than a string because the host parses the argument after this plugin is
    /// constructed but before the first <see cref="SessionEvent.Connected"/> that reads it.</param>
    /// <param name="onLedgerOpened">Test seam: invoked with the ledger the moment it is opened, so a
    /// replay-parity test can assert on what a full host run wrote without a path to reload.</param>
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

    /// <summary>
    /// Opens the ledger on the FIRST connect only. A reconnect keeps it — the same way it keeps
    /// <see cref="RefineryLogic"/>'s panel state — because the merge is idempotent and what the plugin
    /// already recorded is still true. Only the engine knows whether it is replaying a corpus, which
    /// is why the throwaway-vs-real decision cannot be made until this event arrives.
    /// </summary>
    public void OnSessionEvent(SessionEvent evt)
    {
        if (evt is not SessionEvent.Connected connected || _ledger is not null)
            return;

        var target = LedgerTargetResolver.Resolve(
            connected.Engine.ReplayMode, _config.LedgerEnabled, _ledgerOverride?.Invoke(), _config.LedgerPath);
        _ledgerPath = target.Path;
        _ledger = new OrderLedger(_ledgerPath);
        _ledger.Load();
        _onLedgerOpened?.Invoke(_ledger);
    }

    public Task OnTickAsync(TickContext ctx, CancellationToken ct)
    {
        // Built on the first tick, when the services are in hand and the ledger the preceding
        // Connected event opened is ready. The debounce window is three scans at the engine's own
        // reported cadence — 1.5 s at the stock 500 ms — which is the monolith's three-tick rule
        // restated so it holds whatever the engine is configured to scan at.
        _logic ??= new RefineryLogic(ctx.Services, _ledger!, 3 * ctx.Services.Engine.ScanInterval);
        return _logic.OnTickAsync(ctx.Tick, ct);
    }

    /// <summary>The end-of-run ledger summary the monolith printed: a count per state under the path
    /// it wrote to (content of the old <c>WriteLedgerSummary</c>).</summary>
    public IEnumerable<string> SummaryLines()
    {
        if (_ledger is null)
        {
            yield return "Ledger: not opened (never connected to an engine)";
            yield break;
        }

        yield return $"Ledger: {_ledger.All.Count} orders ({_ledgerPath})";
        foreach (var g in _ledger.All.GroupBy(w => w.State).OrderBy(g => g.Key))
            yield return $"  {g.Key}: {g.Count()}";
    }
}
