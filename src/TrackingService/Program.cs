using TrackingService;
using TrackingService.Metrics;
using TrackingService.Replay;
using TrackingService.Trackers;
using Windows.Graphics.Imaging;

// First statement so every later write goes through it and disposal (status-bar erase,
// cursor restore) is guaranteed on every return path. Declared before the metrics
// reporter on purpose: disposed last, so the timer stops before the sink shuts down.
using var sink = new ConsoleSink();

sink.WriteLine("=== Star Citizen Scraper — Tracker Host (Phase 2) ===");

var config = ProbeConfig.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));

// CLI: --track <name> (repeatable, overrides config), --save-frames, --verbose,
//      --replay <dir> (feed saved PNGs through the trackers instead of live capture),
//      --ocr-lang <bcp47> (overrides config; blank = Windows display language)
var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
var saveFrames = args.Contains("--save-frames", StringComparer.OrdinalIgnoreCase);

string? ArgValue(string name) => args
    .Select((a, i) => (a, i))
    .Where(t => t.a.Equals(name, StringComparison.OrdinalIgnoreCase) && t.i + 1 < args.Length)
    .Select(t => args[t.i + 1])
    .FirstOrDefault();

var replayDir = ArgValue("--replay");
var trackerNames = args
    .Select((a, i) => (a, i))
    .Where(t => t.a.Equals("--track", StringComparison.OrdinalIgnoreCase) && t.i + 1 < args.Length)
    .Select(t => args[t.i + 1])
    .ToList();
if (trackerNames.Count == 0)
    trackerNames = config.Trackers;

// Missing/unsupported pack is user setup, not a bug: fail with the fix instructions, no stack trace.
OcrPipeline ocr;
try
{
    ocr = new OcrPipeline(ArgValue("--ocr-lang") ?? config.OcrLanguage);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
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

var debugDir = saveFrames ? config.OutputDir : null;
var available = new Dictionary<string, Func<ITracker>>(StringComparer.OrdinalIgnoreCase)
{
    ["missions"] = () => new MissionTracker(ocr, Emit, sink, verbose, debugDir),
    ["refinery"] = () => new RefineryTracker(ocr, Emit, sink, verbose, debugDir),
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

if (replayDir is not null)
{
    // Offline mode: run saved full-frame PNGs (see FrameDumpTracker) through the trackers
    // in filename order (= chronological, FrameSaver names are timestamped). No capture,
    // no hotkey — deterministic verification without the game running. No metrics timer
    // either: a 1 Hz status bar over a sub-second batch run is pure flicker.
    if (!Directory.Exists(replayDir))
    {
        Console.Error.WriteLine($"Replay directory not found: {replayDir}");
        return 1;
    }

    sink.WriteLine($"Trackers:  {string.Join(", ", trackers.Select(t => t.Name))}");
    sink.WriteLine();

    var frameCount = await ReplayRunner.RunAsync(replayDir, trackers, sink, verbose);
    sink.WriteLine($"Replayed {frameCount} frames from {replayDir}");

    sink.WriteLine();
    sink.WriteLine($"=== Replay summary: {records.Count} captures ===");
    foreach (var g in records.GroupBy(r => (r.Tracker, r.Trigger)))
        sink.WriteLine($"  {g.Key.Tracker} ({g.Key.Trigger}): {g.Count()}");
    return 0;
}

if (saveFrames)
    trackers.Add(new FrameDumpTracker(config.OutputDir, sink)); // hotkey saves full frame for replay corpora

var monitors = MonitorCapture.EnumerateMonitors();
if (monitors.Count == 0)
{
    Console.Error.WriteLine("No monitors found.");
    return 1;
}

var monitorIndex = config.MonitorIndex;
if (monitorIndex < 0 || monitorIndex >= monitors.Count)
{
    sink.WriteLine($"monitorIndex {monitorIndex} out of range, falling back to 0 (primary).");
    monitorIndex = 0;
}
var monitor = monitors[monitorIndex];

var (modifiers, virtualKey) = HotkeyListener.ParseHotkey(config.Hotkey);

sink.WriteLine($"Capturing: [{monitorIndex}] {monitor.DeviceName} {monitor.Width}x{monitor.Height}");
sink.WriteLine($"Trackers:  {string.Join(", ", trackers.Select(t => t.Name))}");
sink.WriteLine($"Hotkey:    {config.Hotkey} (manual trigger)");
var otherOcrPacks = OcrPipeline.AvailableLanguageTags.Where(t => t != ocr.LanguageTag).ToArray();
sink.WriteLine($"OCR:       {ocr.Language}{(otherOcrPacks.Length > 0
    ? $" — also installed: {string.Join(", ", otherOcrPacks)}"
    : "")}");
sink.WriteLine($"Debug:     {(saveFrames ? $"saving debug PNG+txt and hotkey frames to {config.OutputDir}" : "in-memory only, no files")}");
sink.WriteLine($"Metrics:   {(config.MetricsEnabled ? $"live status bar every {config.MetricsIntervalMs} ms" : "disabled")}");

using var capture = new MonitorCapture(monitor.Handle);
if (!capture.BorderDisabled)
    sink.WriteLine("Note: OS refused to remove the yellow capture border (cosmetic only).");

var host = new TrackerHost(capture, trackers, sink);
using var hotkey = new HotkeyListener(modifiers, virtualKey, host.TriggerManual);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

sink.WriteLine();
sink.WriteLine("Running. Ctrl+C to quit.");
sink.WriteLine();

// Declared after sink so it disposes first: the timer is fully stopped (in-flight tick
// drained) before the sink erases the status line on shutdown.
using var metrics = config.MetricsEnabled
    ? new MetricsReporter(sink, TimeSpan.FromMilliseconds(config.MetricsIntervalMs))
    : null;

await host.RunAsync(cts.Token);

metrics?.Dispose(); // stop status updates before the summary prints (using-dispose is a harmless no-op after this)

sink.WriteLine();
sink.WriteLine($"=== Summary: {records.Count} captures ===");
foreach (var g in records.GroupBy(r => (r.Tracker, r.Trigger)))
    sink.WriteLine($"  {g.Key.Tracker} ({g.Key.Trigger}): {g.Count()}");

return 0;
