using CaptureContracts;
using Common;
using Grpc.Core;
using MissionPlugin;
using TrackerSdk;

// First statement so every later write goes through it and disposal (status-bar erase,
// cursor restore) is guaranteed on every return path.
using var sink = new ConsoleSink();

sink.WriteLine("=== Star Citizen Tracker — Mission Plugin ===");

var config = MissionConfig.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));

// CLI: --pipe <name> (overrides config), --verbose
var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);

string? ArgValue(string name) => args
    .Select((a, i) => (a, i))
    .Where(t => t.a.Equals(name, StringComparison.OrdinalIgnoreCase) && t.i + 1 < args.Length)
    .Select(t => args[t.i + 1])
    .FirstOrDefault();

var pipeName = ArgValue("--pipe") ?? config.PipeName;
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

var logic = new MissionLogic(Emit, sink, verbose, dumpFrame);

sink.WriteLine($"Pipe:      {pipeName}");
sink.WriteLine($"Debug:     {(config.SaveDebugFrames ? "asking the engine for a pane PNG per capture" : "in-memory only, no files")}");
sink.WriteLine();

// WaitForEngineAsync needs a finite budget: Timeout.InfiniteTimeSpan is negative and would go
// straight to its timeout branch, and TimeSpan.MaxValue overflows the RPC deadline. A day is
// "forever" for a plugin left running — the loop below retries anyway, and cancellation, not
// this, is what ends the wait.
var engineWait = TimeSpan.FromDays(1);

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

        await using var session = await client.TrackAsync(MissionLogic.Name, Rois.All, cts.Token);

        sink.WriteLine($"Engine:    {status.EngineVersion}{(status.ReplayMode ? " (replay)" : "")}");
        sink.WriteLine($"Frame:     {(status.FrameWidth == 0
            ? "no frame scanned yet"
            : $"{status.FrameWidth}x{status.FrameHeight}")}");
        sink.WriteLine($"ROIs:      {string.Join(", ", Rois.All.Select(r => r.Id))}");
        sink.WriteLine();
        sink.WriteLine("Running. Ctrl+C to quit.");
        sink.WriteLine();

        await foreach (var tick in session.Ticks(cts.Token))
            await logic.OnTickAsync(tick);
    }
    catch (OperationCanceledException)
    {
        break; // our own Ctrl+C: the channel maps a cancelled call to this, not RpcException
    }
    catch (TimeoutException)
    {
        continue; // engine still not serving; the line above already says we are waiting
    }
    catch (RpcException)
    {
        // The engine went away mid-session. Reconnecting means a fresh subscription, and the
        // logic's counter state is deliberately kept: the missions it already saw are still
        // accepted, and the first tab read after reconnect is a re-sighting, not an accept.
        sink.WriteLine("engine connection lost — reconnecting");
        continue;
    }

    break; // stream ended normally (engine replay finished or shutdown)
}

sink.WriteLine();
sink.WriteLine($"=== Summary: {records.Count} captures ===");
foreach (var g in records.GroupBy(r => (r.Tracker, r.Trigger)))
    sink.WriteLine($"  {g.Key.Tracker} ({g.Key.Trigger}): {g.Count()}");

return 0;
