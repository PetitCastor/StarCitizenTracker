// TRANSITIONAL DUPLICATE of src/TrackingService/Metrics/MetricsFormatter.cs, byte-identical
// apart from the namespace. Nothing references this copy yet — the engine picks it up in
// ENGINE-SPLIT TASK-3, and TASK-8 deletes the monolith's copy in favour of it. Until then both
// are live and must be edited together. tests/CaptureEngine.Tests/MetricsFormatterTests.cs
// mirrors the monolith's assertions to catch drift.
using System.Globalization;

namespace CaptureEngine.Metrics;

/// <summary>
/// Renders a snapshot as the single status-bar line. Everything stays in MB so a slow
/// leak reads as one monotonically climbing number instead of jumping units.
/// </summary>
public static class MetricsFormatter
{
    public static string Format(MetricsSnapshot s)
    {
        // The two GPU counter categories can fail independently on a given tick, so each
        // field degrades on its own. Summed per-engine utilization can legitimately
        // exceed 100; clamp for display.
        var gpu = s.GpuPercent is null && s.GpuMemoryBytes is null
            ? "GPU n/a"
            : "GPU "
              + (s.GpuPercent is { } pct ? Invariant($"{Math.Min(pct, 100):0}%") : "n/a")
              + " / "
              + (s.GpuMemoryBytes is { } mem ? Invariant($"{ToMb(mem)}MB") : "n/a");

        return Invariant(
            $"CPU {s.CpuPercent:0.0}%  MEM {ToMb(s.WorkingSetBytes)}MB ws / {ToMb(s.PrivateMemoryBytes)}MB priv / {ToMb(s.ManagedHeapBytes)}MB heap  {gpu}");
    }

    internal static long ToMb(long bytes) => bytes / (1024 * 1024);

    private static string Invariant(FormattableString s)
        => s.ToString(CultureInfo.InvariantCulture);
}
