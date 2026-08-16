using CaptureContracts;
using RefineryPlugin;
using RefineryPlugin.Orders;
using TrackerSdk;

// First statement so every later write goes through it and disposal (status-bar erase,
// cursor restore) is guaranteed on every return path.
using var sink = new ConsoleSink();

sink.WriteLine("=== Star Citizen Tracker — Refinery Plugin ===");

var config = RefineryConfig.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));

// CLI: --pipe <name> (overrides config), --ledger <path> (overrides config), --verbose
var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);

// -1 when absent. A flag with nothing after it is a typo worth reporting: silently falling back
// to the config value would connect to a different engine, or write a different ledger, than the
// one the user just named.
int FlagIndex(string name) => Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

var pipeArg = FlagIndex("--pipe");
if (pipeArg >= 0 && pipeArg + 1 >= args.Length)
{
    Console.Error.WriteLine("--pipe needs a pipe name after it.");
    return 1;
}

var ledgerArg = FlagIndex("--ledger");
if (ledgerArg >= 0 && ledgerArg + 1 >= args.Length)
{
    Console.Error.WriteLine("--ledger needs a file path after it.");
    return 1;
}

var ledgerOverride = ledgerArg >= 0 ? args[ledgerArg + 1] : null;
if (ledgerOverride is not null && string.IsNullOrWhiteSpace(ledgerOverride))
{
    // A blank path beats the config value and then fails deep inside the first append, as an
    // ArgumentException the ledger's IO catch does not cover — a tick loop that reports a failure
    // twice a second and never records an order.
    Console.Error.WriteLine("--ledger needs a non-blank file path.");
    return 1;
}

var pipeName = pipeArg >= 0 ? args[pipeArg + 1] : config.PipeName;
if (string.IsNullOrWhiteSpace(pipeName))
{
    Console.Error.WriteLine("Pipe name must not be blank (set \"pipeName\" in config.json or pass --pipe).");
    return 1;
}

var records = new List<TrackerRecord>();

// One sink call per capture: each WriteLine erases/redraws the status bar, so five
// separate calls would flicker it five times per tracker event.
void Emit(TrackerRecord record)
{
    records.Add(record);
    sink.WriteLine(string.Join(Environment.NewLine,
        "",
        $"===== {record.Tracker} capture ({record.Trigger}) at {record.Timestamp:HH:mm:ss.fff} =====",
        record.RawText,
        "=====================================================",
        ""));
}

// Opened on the first successful connect, not here: a replay run must never append corpus orders
// to the user's real orders.jsonl — the monolith redirected to a throwaway file whenever it was
// handed --replay, and now only the engine knows it is replaying. A later reconnect keeps the file
// the run started with, the same way the logic keeps its panel state across one.
OrderLedger? ledger = null;
var ledgerPath = "";

void WriteLedgerSummary()
{
    if (ledger is null)
    {
        sink.WriteLine("Ledger: not opened (never connected to an engine)");
        return;
    }

    sink.WriteLine($"Ledger: {ledger.All.Count} orders ({ledgerPath})");
    foreach (var g in ledger.All.GroupBy(w => w.State).OrderBy(g => g.Key))
        sink.WriteLine($"  {g.Key}: {g.Count()}");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var client = new CaptureClient(pipeName);

// Debug dumps are the engine's to write — the frame never crosses the boundary, only the path
// it was written to. Null switches the whole debug path off inside the logic.
Func<RoiRect?, string, Task<string?>>? dumpFrame = config.SaveDebugFrames
    ? (roi, prefix) => client.DumpFrameAsync(roi, prefix, cts.Token)
    : null;

sink.WriteLine($"Pipe:      {pipeName}");
sink.WriteLine($"Debug:     {(config.SaveDebugFrames ? "asking the engine for a completed-panel PNG per order" : "in-memory only, no files")}");
sink.WriteLine();

// The connect / subscribe / consume loop lives in RefineryRunner so the replay-parity tests can
// drive the same path this process does. The ledger is opened from inside it, on the first
// successful connect: only the engine knows whether it is replaying a corpus, and a replay must
// never append to the user's real orders.jsonl (the monolith redirected the same way when it was
// handed --replay). An explicit --ledger still wins — that is how the parity harness points a
// replay at a file it can read afterwards.
await RefineryRunner.RunAsync(client, pipeName, status =>
{
    var target = LedgerTargetResolver.Resolve(
        status.ReplayMode, config.LedgerEnabled, ledgerOverride, config.LedgerPath);
    ledgerPath = target.Path;

    ledger = new OrderLedger(ledgerPath, sink.WriteLine);
    ledger.Load();

    sink.WriteLine($"Ledger:    {ledgerPath}{target.Note}");

    return new RefineryLogic(Emit, sink, verbose, dumpFrame, ledger);
}, sink, cts.Token);

sink.WriteLine();
sink.WriteLine($"=== Summary: {records.Count} captures ===");
foreach (var g in records.GroupBy(r => (r.Tracker, r.Trigger)))
    sink.WriteLine($"  {g.Key.Tracker} ({g.Key.Trigger}): {g.Count()}");
WriteLedgerSummary();

return 0;
