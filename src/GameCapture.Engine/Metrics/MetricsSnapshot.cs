namespace GameCapture.Engine.Metrics;

/// <summary>
/// One point-in-time reading of process health. GPU fields are null when the GPU
/// performance counters are unavailable on this machine (RDP, driver quirks) —
/// distinct from a genuine 0% / 0 bytes reading.
/// </summary>
public sealed record MetricsSnapshot(
    DateTime Timestamp,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    double? GpuPercent,
    long? GpuMemoryBytes);
