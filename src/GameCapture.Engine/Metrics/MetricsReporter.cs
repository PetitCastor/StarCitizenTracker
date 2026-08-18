
namespace GameCapture.Engine.Metrics;

/// <summary>
/// Ticks a <see cref="MetricsSampler"/> on its own timer and pushes each formatted
/// snapshot to the sink's status bar. One-shot re-arming timer: the next tick is only
/// scheduled after the current one finishes, so a slow sample can never overlap the
/// next. Starts immediately on construction (same idiom as HotkeyListener).
/// </summary>
public sealed class MetricsReporter : IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

    private readonly ConsoleSink _sink;
    private readonly TimeSpan _interval;
    private readonly MetricsSampler _sampler = new();
    private readonly Timer _timer;
    private bool _disposed;

    public MetricsReporter(ConsoleSink sink, TimeSpan interval)
    {
        _sink = sink;
        // Hand-edited config can hold 0/negative; never let that become a tight loop.
        _interval = interval < MinInterval ? MinInterval : interval;
        _timer = new Timer(_ => Tick(), null, _interval, Timeout.InfiniteTimeSpan);
    }

    private void Tick()
    {
        // An unhandled exception on a thread-pool timer callback kills the process;
        // metrics must never take the tracker down. Stop re-arming on failure.
        try
        {
            _sink.UpdateStatus(MetricsFormatter.Format(_sampler.Sample()));
            lock (_timer)
            {
                if (!_disposed)
                    _timer.Change(_interval, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            _sink.WriteLine($"[metrics] disabled after unexpected error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_timer)
        {
            _disposed = true;
        }
        // Dispose(WaitHandle) blocks until any in-flight callback finishes, so the
        // sampler is never disposed under a running Tick.
        using var callbacksDone = new ManualResetEvent(false);
        if (_timer.Dispose(callbacksDone))
            callbacksDone.WaitOne();
        _sampler.Dispose();
    }
}
