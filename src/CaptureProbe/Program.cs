using System.Diagnostics;
using System.Threading.Channels;
using CaptureProbe;

Console.WriteLine("=== Star Citizen Capture Probe (Phase 1) ===");

var config = ProbeConfig.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));

var monitors = MonitorCapture.EnumerateMonitors();
if (monitors.Count == 0)
{
    Console.Error.WriteLine("No monitors found.");
    return 1;
}

Console.WriteLine("Monitors:");
for (var i = 0; i < monitors.Count; i++)
{
    var m = monitors[i];
    Console.WriteLine($"  [{i}] {m.DeviceName}  {m.Width}x{m.Height}{(m.IsPrimary ? "  (primary)" : "")}");
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
Console.WriteLine($"Hotkey:    {config.Hotkey}");
Console.WriteLine($"Output:    {config.OutputDir}");

using var capture = new MonitorCapture(monitor.Handle);
if (!capture.BorderDisabled)
    Console.WriteLine("Note: OS refused to remove the yellow capture border (cosmetic only).");

// Hotkey presses are timestamped on the listener thread, processed here on the main loop.
var presses = Channel.CreateUnbounded<long>();
using var hotkey = new HotkeyListener(modifiers, virtualKey, () => presses.Writer.TryWrite(Stopwatch.GetTimestamp()));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine();
Console.WriteLine($"Armed. Press {config.Hotkey} in-game to capture. Ctrl+C here to quit.");
Console.WriteLine();

var pressCount = 0;
var failures = 0;
var latenciesMs = new List<double>();

try
{
    await foreach (var pressedAt in presses.Reader.ReadAllAsync(cts.Token))
    {
        pressCount++;
        var frame = capture.TakeLatestFrame();
        if (frame is null)
        {
            failures++;
            Console.WriteLine($"[{pressCount,3}] NO FRAME — screen may be idle or capture not started yet.");
            continue;
        }

        try
        {
            // Frame timestamps and Stopwatch both use QPC (time since boot), so this measures
            // how stale the frame was at the moment the hotkey was pressed. Slightly negative
            // means a fresher frame arrived between the press and the snapshot.
            var pressSinceBoot = Stopwatch.GetElapsedTime(0, pressedAt);
            var frameAgeMs = (pressSinceBoot - frame.SystemRelativeTime).TotalMilliseconds;

            var path = await FrameSaver.SavePngAsync(frame, config.OutputDir);
            var latencyMs = Stopwatch.GetElapsedTime(pressedAt).TotalMilliseconds;
            latenciesMs.Add(latencyMs);

            Console.WriteLine(
                $"[{pressCount,3}] saved {Path.GetFileName(path)}  " +
                $"{frame.ContentSize.Width}x{frame.ContentSize.Height}  " +
                $"latency {latencyMs,7:F1} ms  frame age {frameAgeMs,6:F1} ms");
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"[{pressCount,3}] FAILED: {ex.Message}");
        }
        finally
        {
            frame.Dispose();
        }
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C — fall through to summary.
}

Console.WriteLine();
Console.WriteLine("=== Summary ===");
Console.WriteLine($"Presses:  {pressCount}");
Console.WriteLine($"Saved:    {latenciesMs.Count}");
Console.WriteLine($"Failures: {failures}");
if (latenciesMs.Count > 0)
{
    Console.WriteLine($"Latency ms  min {latenciesMs.Min():F1}  avg {latenciesMs.Average():F1}  max {latenciesMs.Max():F1}");
}

return 0;
