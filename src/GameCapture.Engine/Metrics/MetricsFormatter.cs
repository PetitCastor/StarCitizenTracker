using System.Globalization;

namespace GameCapture.Engine.Metrics;

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
