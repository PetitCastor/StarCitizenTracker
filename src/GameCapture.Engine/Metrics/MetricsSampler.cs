using System.Diagnostics;

namespace GameCapture.Engine.Metrics;

/// <summary>
/// Reads process CPU/memory plus per-process GPU usage from the PDH "GPU Engine" /
/// "GPU Process Memory" counter categories (same source Task Manager uses).
/// <see cref="Sample"/> never throws: GPU counters are known-flaky (RDP, driver
/// resets) and degrade to null for the tick instead of failing the app.
/// </summary>
public sealed class MetricsSampler : IDisposable
{
    private const string EngineCategory = "GPU Engine";
    private const string EngineCounter = "Utilization Percentage";
    private const string MemoryCategory = "GPU Process Memory";
    private const string MemoryCounter = "Dedicated Usage";

    private readonly Process _process = Process.GetCurrentProcess();
    private readonly string _pidPrefix = $"pid_{Environment.ProcessId}_";
    private readonly bool _gpuEngineAvailable;
    private readonly bool _gpuMemoryAvailable;
    private readonly Dictionary<string, PerformanceCounter> _engineCounters = [];
    private readonly Dictionary<string, PerformanceCounter> _memoryCounters = [];

    private long _lastTimestamp;
    private TimeSpan _lastCpuTime;

    public MetricsSampler()
    {
        // Probed once: a machine without these categories won't grow them mid-run.
        try
        {
            _gpuEngineAvailable = PerformanceCounterCategory.Exists(EngineCategory);
            _gpuMemoryAvailable = PerformanceCounterCategory.Exists(MemoryCategory);
        }
        catch
        {
            _gpuEngineAvailable = _gpuMemoryAvailable = false;
        }
    }

    public MetricsSnapshot Sample()
    {
        _process.Refresh(); // Process properties cache at first read; without this they never move

        var now = Stopwatch.GetTimestamp();
        var cpuTime = _process.TotalProcessorTime;
        var cpuPercent = _lastTimestamp == 0
            ? 0 // first tick has no baseline
            : ComputeCpuPercent(cpuTime - _lastCpuTime, Stopwatch.GetElapsedTime(_lastTimestamp, now), Environment.ProcessorCount);
        (_lastTimestamp, _lastCpuTime) = (now, cpuTime);

        var gpuMemory = SampleGpuCategory(_gpuMemoryAvailable, MemoryCategory, MemoryCounter, _memoryCounters);

        return new MetricsSnapshot(
            DateTime.Now,
            cpuPercent,
            _process.WorkingSet64,
            _process.PrivateMemorySize64,
            GC.GetTotalMemory(forceFullCollection: false),
            SampleGpuCategory(_gpuEngineAvailable, EngineCategory, EngineCounter, _engineCounters),
            gpuMemory is { } bytes ? (long)bytes : null);
    }

    /// <summary>CPU% of total machine capacity, clamped to [0, 100].</summary>
    internal static double ComputeCpuPercent(TimeSpan cpuDelta, TimeSpan wallDelta, int processorCount)
    {
        if (wallDelta <= TimeSpan.Zero || processorCount <= 0)
            return 0;

        var percent = cpuDelta.TotalMilliseconds / (wallDelta.TotalMilliseconds * processorCount) * 100;
        return Math.Clamp(percent, 0, 100);
    }

    /// <summary>
    /// Sums the counter across every instance belonging to this process — a process has
    /// one "GPU Engine" instance per engine type (3D, Copy, VideoDecode, ...) and one
    /// "GPU Process Memory" instance per adapter, all live at once. Rate counters read 0
    /// on their first NextValue() by design; the cached counter converges next tick.
    /// </summary>
    private double? SampleGpuCategory(bool available, string category, string counterName,
        Dictionary<string, PerformanceCounter> cache)
    {
        if (!available)
            return null;

        try
        {
            double sum = 0;
            var live = new HashSet<string>(StringComparer.Ordinal);

            foreach (var instance in new PerformanceCounterCategory(category).GetInstanceNames())
            {
                // Prefix match, not Contains: "pid_138_" must not match "pid_1380_...".
                if (!instance.StartsWith(_pidPrefix, StringComparison.Ordinal))
                    continue;

                live.Add(instance);
                if (!cache.TryGetValue(instance, out var counter))
                    cache[instance] = counter = new PerformanceCounter(category, counterName, instance, readOnly: true);
                sum += counter.NextValue();
            }

            // Engine contexts come and go with the game; evict dead instances or this
            // sampler leaks PDH handles over a long session — the one failure mode a
            // leak monitor cannot be allowed to have.
            foreach (var stale in cache.Keys.Where(k => !live.Contains(k)).ToList())
            {
                cache[stale].Dispose();
                cache.Remove(stale);
            }

            return sum;
        }
        catch
        {
            return null; // flaky tick — degrade, retry next tick
        }
    }

    public void Dispose()
    {
        foreach (var counter in _engineCounters.Values)
            counter.Dispose();
        foreach (var counter in _memoryCounters.Values)
            counter.Dispose();
        _engineCounters.Clear();
        _memoryCounters.Clear();
        _process.Dispose();
    }
}
