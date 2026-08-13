using CaptureProbe;
using CaptureProbe.Trackers;

Console.WriteLine("=== Star Citizen Scraper — Tracker Host (Phase 2) ===");

var config = ProbeConfig.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));

// CLI: --track <name> (repeatable, overrides config), --save-frames, --verbose
var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
var saveFrames = args.Contains("--save-frames", StringComparer.OrdinalIgnoreCase);
var trackerNames = args
    .Select((a, i) => (a, i))
    .Where(t => t.a.Equals("--track", StringComparison.OrdinalIgnoreCase) && t.i + 1 < args.Length)
    .Select(t => args[t.i + 1])
    .ToList();
if (trackerNames.Count == 0)
    trackerNames = config.Trackers;

var monitors = MonitorCapture.EnumerateMonitors();
if (monitors.Count == 0)
{
    Console.Error.WriteLine("No monitors found.");
    return 1;
}

var monitorIndex = config.MonitorIndex;
if (monitorIndex < 0 || monitorIndex >= monitors.Count)
{
    Console.WriteLine($"monitorIndex {monitorIndex} out of range, falling back to 0 (primary).");
    monitorIndex = 0;
}
var monitor = monitors[monitorIndex];

var ocr = new OcrPipeline();
var records = new List<TrackerRecord>();

void Emit(TrackerRecord record)
{
    records.Add(record);
    Console.WriteLine();
    Console.WriteLine($"===== {record.Tracker} capture ({record.Trigger}) at {record.Timestamp:HH:mm:ss.fff} =====");
    Console.WriteLine(record.RawText);
    Console.WriteLine("=====================================================");
    Console.WriteLine();
}

var debugDir = saveFrames ? config.OutputDir : null;
var available = new Dictionary<string, Func<ITracker>>(StringComparer.OrdinalIgnoreCase)
{
    ["missions"] = () => new MissionTracker(ocr, Emit, verbose, debugDir),
};

var trackers = new List<ITracker>();
foreach (var name in trackerNames)
{
    if (available.TryGetValue(name, out var factory))
        trackers.Add(factory());
    else
    {
        Console.Error.WriteLine($"Unknown tracker '{name}'. Available: {string.Join(", ", available.Keys)}");
        return 1;
    }
}

var (modifiers, virtualKey) = HotkeyListener.ParseHotkey(config.Hotkey);

Console.WriteLine($"Capturing: [{monitorIndex}] {monitor.DeviceName} {monitor.Width}x{monitor.Height}");
Console.WriteLine($"Trackers:  {string.Join(", ", trackers.Select(t => t.Name))}");
Console.WriteLine($"Hotkey:    {config.Hotkey} (manual trigger)");
Console.WriteLine($"OCR:       {ocr.Language}");
Console.WriteLine($"Debug:     {(saveFrames ? $"saving pane PNG+txt to {config.OutputDir}" : "in-memory only, no files")}");

using var capture = new MonitorCapture(monitor.Handle);
if (!capture.BorderDisabled)
    Console.WriteLine("Note: OS refused to remove the yellow capture border (cosmetic only).");

var host = new TrackerHost(capture, trackers);
using var hotkey = new HotkeyListener(modifiers, virtualKey, host.TriggerManual);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine();
Console.WriteLine("Running. Ctrl+C to quit.");
Console.WriteLine();

await host.RunAsync(cts.Token);

Console.WriteLine();
Console.WriteLine($"=== Summary: {records.Count} captures ===");
foreach (var g in records.GroupBy(r => (r.Tracker, r.Trigger)))
    Console.WriteLine($"  {g.Key.Tracker} ({g.Key.Trigger}): {g.Count()}");

return 0;
