using GameCapture.Engine;
using GameCapture.Engine.Metrics;

// First statement so every later write goes through it and disposal (status-bar erase,
// cursor restore) is guaranteed on every return path.
using var sink = new ConsoleSink();

sink.WriteLine("=== GameCapture — Capture Engine ===");

var config = EngineConfig.Load(Path.Combine(AppContext.BaseDirectory, "engine-config.json"));

// CLI: --pipe <name>, --ocr-lang <bcp47>, --monitor <index> (each overrides config),
//      --replay <dir> (feed saved PNGs through the engine instead of live capture),
//      --save-frames (save full-frame PNG on manual trigger), --verbose
var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
var saveFrames = args.Contains("--save-frames", StringComparer.OrdinalIgnoreCase);

string? ArgValue(string name) => args
    .Select((a, i) => (a, i))
    .Where(t => t.a.Equals(name, StringComparison.OrdinalIgnoreCase) && t.i + 1 < args.Length)
    .Select(t => args[t.i + 1])
    .FirstOrDefault();

var pipeName = ArgValue("--pipe") ?? config.PipeName;
if (string.IsNullOrWhiteSpace(pipeName))
{
    Console.Error.WriteLine("Pipe name must not be blank (set \"pipeName\" in engine-config.json or pass --pipe).");
    return 1;
}

if (ArgValue("--monitor") is { } monitorArg)
{
    if (!int.TryParse(monitorArg, out var monitorIndex) || monitorIndex < 0)
    {
        Console.Error.WriteLine($"--monitor expects a non-negative index, got '{monitorArg}'.");
        return 1;
    }
    config.MonitorIndex = monitorIndex;
}

var replayDir = ArgValue("--replay");
if (replayDir is not null && !Directory.Exists(replayDir))
{
    Console.Error.WriteLine($"Replay directory not found: {replayDir}");
    return 1;
}

if (saveFrames && replayDir is not null)
{
    Console.Error.WriteLine("--save-frames cannot be combined with --replay.");
    return 1;
}

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

// Replay is offline and deterministic: no capture session, no hotkey (there is no live screen to
// trigger against), and no metrics timer — a 1 Hz status bar over a batch run is pure flicker.
IFrameSource source;
string captureLine;

if (replayDir is not null)
{
    var replay = new ReplayFrameSource(replayDir);
    source = replay;
    captureLine = $"Replay:    {replay.FrameCount} frame(s) from {replayDir}";
}
else
{
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
    var capture = new MonitorCapture(monitor.Handle);
    if (!capture.BorderDisabled)
        sink.WriteLine("Note: OS refused to remove the yellow capture border (cosmetic only).");

    source = new LiveFrameSource(capture);
    captureLine = $"Capturing: [{monitorIndex}] {monitor.DeviceName} {monitor.Width}x{monitor.Height}";
}

await using var engine = EngineHost.Create(pipeName, config, ocr, source, sink, verbose);

// Same fail-with-a-message contract as the OCR pack check above: a pipe name collision
// (second instance already bound, or an invalid name) is user error, not a bug.
try
{
    await engine.StartAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to start on pipe '{pipeName}': {ex.Message}");
    return 1;
}

sink.WriteLine($"Pipe:      {pipeName}");
sink.WriteLine(captureLine);
var otherOcrPacks = OcrPipeline.AvailableLanguageTags.Where(t => t != ocr.LanguageTag).ToArray();
sink.WriteLine($"OCR:       {ocr.Language}{(otherOcrPacks.Length > 0
    ? $" — also installed: {string.Join(", ", otherOcrPacks)}"
    : "")}");
sink.WriteLine($"Dumps:     {config.OutputDir}");
sink.WriteLine($"SaveFrames: {(saveFrames ? "on" : "off")}");
sink.WriteLine($"Verbose:   {(verbose ? "on" : "off")}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

HotkeyListener? hotkey = null;
MetricsReporter? metrics = null;

if (replayDir is null)
{
    var (modifiers, virtualKey) = HotkeyListener.ParseHotkey(config.Hotkey);
    Action onHotkey;
    if (saveFrames)
    {
        var frameDumper = new FrameDumpService(config.OutputDir, sink);
        onHotkey = () =>
        {
            engine.ScanLoop.TriggerManual(frameDumper.DumpFrameAsync);
        };
    }
    else
    {
        onHotkey = engine.ScanLoop.TriggerManual;
    }

    hotkey = new HotkeyListener(modifiers, virtualKey, onHotkey);
    sink.WriteLine($"Hotkey:    {config.Hotkey} (manual trigger{(saveFrames ? " + save frame" : "")})");
    sink.WriteLine($"Metrics:   {(config.MetricsEnabled ? $"live status bar every {config.MetricsIntervalMs} ms" : "disabled")}");
}

sink.WriteLine();
sink.WriteLine(replayDir is null
    ? "Scanning. Ctrl+C to quit."
    : "Waiting for a plugin to subscribe before replaying the corpus. Ctrl+C to quit.");
sink.WriteLine();

try
{
    // Created after the banner so it disposes before the sink: the timer is fully stopped
    // (in-flight tick drained) before the sink erases the status line on shutdown.
    if (replayDir is null && config.MetricsEnabled)
        metrics = new MetricsReporter(sink, TimeSpan.FromMilliseconds(config.MetricsIntervalMs));

    await engine.RunScanAsync(cts.Token);
}
finally
{
    metrics?.Dispose(); // stop status updates before the summary prints
    hotkey?.Dispose();
}

await engine.StopAsync();

sink.WriteLine();
sink.WriteLine($"Engine stopped after {engine.Status.Snapshot().FrameSeq} frame(s).");
return 0;
