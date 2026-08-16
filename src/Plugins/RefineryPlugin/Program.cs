using CaptureContracts;
using Common;
using Grpc.Core;
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

// A disabled ledger writes to a throwaway temp file so a smoke run never touches the user's real
// orders.jsonl; --ledger points it wherever the caller wants (the parity harness uses a temp file).
// The ledger is loaded once up front (rebuild from disk).
var ledgerPath = ledgerOverride
    ?? (config.LedgerEnabled
        ? config.LedgerPath
        : Path.Combine(Path.GetTempPath(), $"sc-tracker-ephemeral-{Guid.NewGuid():N}.jsonl"));
var ledger = new OrderLedger(ledgerPath, sink.WriteLine);
ledger.Load();

void WriteLedgerSummary()
{
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

var logic = new RefineryLogic(Emit, sink, verbose, dumpFrame, ledger);

sink.WriteLine($"Pipe:      {pipeName}");
sink.WriteLine($"Ledger:    {ledgerPath}{(config.LedgerEnabled || ledgerOverride is not null ? "" : " (disabled — throwaway file)")}");
sink.WriteLine($"Debug:     {(config.SaveDebugFrames ? "asking the engine for a completed-panel PNG per order" : "in-memory only, no files")}");
sink.WriteLine();

// WaitForEngineAsync needs a finite budget: Timeout.InfiniteTimeSpan is negative and would go
// straight to its timeout branch, and TimeSpan.MaxValue overflows the RPC deadline. A day is
// "forever" for a plugin left running — the loop below retries anyway, and cancellation, not
// this, is what ends the wait.
var engineWait = TimeSpan.FromDays(1);

// Breathing room between a lost session and the next dial; see the RpcException branch below.
var reconnectDelay = TimeSpan.FromMilliseconds(500);

// Announced once per disconnected stretch rather than per retry: a plugin started before the
// engine would otherwise scroll the same line every few seconds.
var announcedWait = false;

while (true)
{
    if (!announcedWait)
    {
        sink.WriteLine($"waiting for engine on pipe '{pipeName}'...");
        announcedWait = true;
    }

    try
    {
        var status = await client.WaitForEngineAsync(engineWait, cts.Token);
        announcedWait = false;

        await using var session = await client.TrackAsync(RefineryLogic.Name, Rois.All, cts.Token);

        sink.WriteLine($"Engine:    {status.EngineVersion}{(status.ReplayMode ? " (replay)" : "")}");
        sink.WriteLine($"Frame:     {(status.FrameWidth == 0
            ? "no frame scanned yet"
            : $"{status.FrameWidth}x{status.FrameHeight}")}");
        sink.WriteLine($"ROIs:      {string.Join(", ", Rois.All.Select(r => r.Id))}");
        sink.WriteLine();
        sink.WriteLine("Running. Ctrl+C to quit.");
        sink.WriteLine();

        await foreach (var tick in session.Ticks(cts.Token))
        {
            // As TrackerHost did per tracker: one bad tick must not end the run. A genuine
            // transport failure is not swallowed — the next read from the stream raises it
            // again and the reconnect below handles it.
            try
            {
                await logic.OnTickAsync(tick, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sink.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {RefineryLogic.Name}: tick failed: {ex.Message}");
            }
        }
    }
    catch (OperationCanceledException)
    {
        break; // our own Ctrl+C: the channel maps a cancelled call to this, not RpcException
    }
    catch (RpcException) when (cts.IsCancellationRequested)
    {
        // Ctrl+C again: the channel's OCE mapping covers the call, but a write already in flight
        // on the request stream can still surface as CANCELLED. Not an engine failure.
        break;
    }
    catch (TimeoutException)
    {
        continue; // engine still not serving; the line above already says we are waiting
    }
    catch (RpcException)
    {
        // The engine went away mid-session. Reconnecting means a fresh subscription, and the
        // logic's panel state is deliberately kept: the ledger merges idempotently, so a panel
        // still on screen after the reconnect re-observes into the same record.
        sink.WriteLine("engine connection lost — reconnecting");

        // Paced: WaitForEngineAsync returns immediately whenever GetStatus answers, so an engine
        // that is up but cannot serve a Track stream (mid-shutdown, for one) would otherwise spin
        // this loop with no delay at all.
        try { await Task.Delay(reconnectDelay, cts.Token); }
        catch (OperationCanceledException) { break; }

        continue;
    }

    break; // stream ended normally (engine replay finished or shutdown)
}

sink.WriteLine();
sink.WriteLine($"=== Summary: {records.Count} captures ===");
foreach (var g in records.GroupBy(r => (r.Tracker, r.Trigger)))
    sink.WriteLine($"  {g.Key.Tracker} ({g.Key.Trigger}): {g.Count()}");
WriteLedgerSummary();

return 0;
