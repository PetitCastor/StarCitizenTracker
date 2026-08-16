using CaptureEngine;
using CaptureEngine.Grpc;
using Common;
using Microsoft.AspNetCore.Builder;

// First statement so every later write goes through it and disposal (status-bar erase,
// cursor restore) is guaranteed on every return path.
using var sink = new ConsoleSink();

sink.WriteLine("=== Star Citizen Tracker — Capture Engine ===");

var config = EngineConfig.Load(Path.Combine(AppContext.BaseDirectory, "engine-config.json"));

// CLI: --pipe <name>, --ocr-lang <bcp47>, --monitor <index> (each overrides config), --verbose
var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);

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

var status = new EngineStatus(ocr.LanguageTag, replayMode: false);

var app = GrpcHost.BuildGrpcHost(pipeName, status);

// Same fail-with-a-message contract as the OCR pack check above: a pipe name collision
// (second instance already bound, or an invalid name) is user error, not a bug.
try
{
    await app.StartAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to start on pipe '{pipeName}': {ex.Message}");
    return 1;
}

sink.WriteLine($"Pipe:      {pipeName}");
sink.WriteLine($"Monitor:   index {config.MonitorIndex} (capture starts in TASK-3)");
var otherOcrPacks = OcrPipeline.AvailableLanguageTags.Where(t => t != ocr.LanguageTag).ToArray();
sink.WriteLine($"OCR:       {ocr.Language}{(otherOcrPacks.Length > 0
    ? $" — also installed: {string.Join(", ", otherOcrPacks)}"
    : "")}");
sink.WriteLine($"Verbose:   {(verbose ? "on" : "off")}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

sink.WriteLine();
sink.WriteLine("Listening. Ctrl+C to quit.");
sink.WriteLine();

// No scan loop yet (TASK-3): the process exists only to serve the pipe until cancelled.
try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
}

await app.StopAsync();

sink.WriteLine("Engine stopped.");
return 0;
