using System.Threading.Channels;

namespace TrackingService.Trackers;

/// <summary>
/// Owns the scan cadence: pulls the latest frame from the shared capture session,
/// downloads it to CPU once, and hands it to every active tracker. Hotkey presses
/// are routed to all trackers as manual triggers on the next frame.
/// </summary>
public sealed class TrackerHost
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IdleRetry = TimeSpan.FromMilliseconds(200);

    private readonly MonitorCapture _capture;
    private readonly IReadOnlyList<ITracker> _trackers;
    private readonly Channel<DateTime> _manualPresses = Channel.CreateUnbounded<DateTime>();

    public TrackerHost(MonitorCapture capture, IReadOnlyList<ITracker> trackers)
    {
        _capture = capture;
        _trackers = trackers;
    }

    /// <summary>Thread-safe; called from the hotkey listener thread.</summary>
    public void TriggerManual() => _manualPresses.Writer.TryWrite(DateTime.Now);

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = _capture.TakeLatestFrame();
            if (frame is null)
            {
                // Idle screen produces no new frames; keep manual presses queued until one arrives.
                try { await Task.Delay(IdleRetry, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            var manualRequested = false;
            while (_manualPresses.Reader.TryRead(out _))
                manualRequested = true;

            try
            {
                using var bitmap = await OcrPipeline.ToSoftwareBitmapAsync(frame);

                foreach (var tracker in _trackers)
                {
                    try
                    {
                        if (manualRequested)
                            await tracker.OnManualTriggerAsync(bitmap, ct);
                        await tracker.ScanAsync(bitmap, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {tracker.Name}: scan failed: {ex.Message}");
                    }
                }
            }
            finally
            {
                frame.Dispose();
            }

            try { await Task.Delay(ScanInterval, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
