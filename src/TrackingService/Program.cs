using TrackingService;
using TrackingService.Trackers;
using Windows.Graphics.Imaging;

Console.WriteLine("=== Star Citizen Scraper — Tracker Host (Phase 2) ===");

var config = ProbeConfig.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));

// CLI: --track <name> (repeatable, overrides config), --save-frames, --verbose,
//      --replay <dir> (feed saved PNGs through the trackers instead of live capture)
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
    ["refinery"] = () => new RefineryTracker(ocr, Emit, verbose, debugDir),
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
    // no hotkey — deterministic verification without the game running.
    if (!Directory.Exists(replayDir))
    {
        Console.Error.WriteLine($"Replay directory not found: {replayDir}");
        return 1;
    }

    var frames = Directory.GetFiles(replayDir, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
    Console.WriteLine($"Replaying {frames.Count} frames from {replayDir}");
    Console.WriteLine($"Trackers:  {string.Join(", ", trackers.Select(t => t.Name))}");
    Console.WriteLine();

    foreach (var framePath in frames)
    {
        if (verbose)
            Console.WriteLine($"--- {Path.GetFileName(framePath)} ---");

        using var fileStream = File.OpenRead(framePath);
        var decoder = await BitmapDecoder.CreateAsync(fileStream.AsRandomAccessStream());
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

        foreach (var tracker in trackers)
            await tracker.ScanAsync(bitmap, CancellationToken.None);
    }

    Console.WriteLine();
    Console.WriteLine($"=== Replay summary: {records.Count} captures ===");
    foreach (var g in records.GroupBy(r => (r.Tracker, r.Trigger)))
        Console.WriteLine($"  {g.Key.Tracker} ({g.Key.Trigger}): {g.Count()}");
    return 0;
}

if (saveFrames)
    trackers.Add(new FrameDumpTracker(config.OutputDir)); // hotkey saves full frame for replay corpora

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

var (modifiers, virtualKey) = HotkeyListener.ParseHotkey(config.Hotkey);

Console.WriteLine($"Capturing: [{monitorIndex}] {monitor.DeviceName} {monitor.Width}x{monitor.Height}");
Console.WriteLine($"Trackers:  {string.Join(", ", trackers.Select(t => t.Name))}");
Console.WriteLine($"Hotkey:    {config.Hotkey} (manual trigger)");
Console.WriteLine($"OCR:       {ocr.Language}");
Console.WriteLine($"Debug:     {(saveFrames ? $"saving debug PNG+txt and hotkey frames to {config.OutputDir}" : "in-memory only, no files")}");

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
