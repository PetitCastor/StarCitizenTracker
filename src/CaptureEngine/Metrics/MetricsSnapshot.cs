// TRANSITIONAL DUPLICATE of src/TrackingService/Metrics/MetricsSnapshot.cs, byte-identical
// apart from the namespace. Nothing references this copy yet — the engine picks it up in
// ENGINE-SPLIT TASK-3, and TASK-8 deletes the monolith's copy in favour of it. Until then both
// are live and must be edited together. No parity test: a plain record has no behavior to drift.
namespace CaptureEngine.Metrics;

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
