using GameCapture.Engine.Metrics;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// The monolith's MetricsFormatterTests, now the only copy: same inputs, same expected outputs, on
/// the engine's MetricsFormatter. The exact strings are the contract the status line is read
/// against, so they stay as accumulated rather than re-derived from the formatter.
/// </summary>
public class MetricsFormatterTests
{
    private const long Mb = 1024 * 1024;

    private static MetricsSnapshot Snapshot(
        double cpu = 0,
        long workingSet = 0,
        long privateMem = 0,
        long heap = 0,
        double? gpu = null,
        long? gpuMem = null)
        => new(new DateTime(2026, 8, 14, 12, 0, 0), cpu, workingSet, privateMem, heap, gpu, gpuMem);

    [Fact]
    public void Format_TypicalReadings_RendersAllFields()
    {
        var text = MetricsFormatter.Format(Snapshot(
            cpu: 4.25, workingSet: 312 * Mb, privateMem: 298 * Mb, heap: 41 * Mb,
            gpu: 7.4, gpuMem: 890 * Mb));

        Assert.Equal("CPU 4.3%  MEM 312MB ws / 298MB priv / 41MB heap  GPU 7% / 890MB", text);
    }

    [Fact]
    public void Format_ZeroEverything_RendersZeros()
    {
        var text = MetricsFormatter.Format(Snapshot(gpu: 0, gpuMem: 0));

        Assert.Equal("CPU 0.0%  MEM 0MB ws / 0MB priv / 0MB heap  GPU 0% / 0MB", text);
    }

    [Fact]
    public void Format_GpuUnavailable_SaysNa()
    {
        var text = MetricsFormatter.Format(Snapshot(cpu: 12.5, workingSet: 100 * Mb));

        Assert.EndsWith("GPU n/a", text);
        Assert.DoesNotContain("/ n/a", text);
    }

    [Fact]
    public void Format_OnlyGpuMemoryAvailable_DegradesPercentOnly()
    {
        var text = MetricsFormatter.Format(Snapshot(gpuMem: 500 * Mb));

        Assert.EndsWith("GPU n/a / 500MB", text);
    }

    [Fact]
    public void Format_OnlyGpuPercentAvailable_DegradesMemoryOnly()
    {
        var text = MetricsFormatter.Format(Snapshot(gpu: 42.0));

        Assert.EndsWith("GPU 42% / n/a", text);
    }

    [Fact]
    public void Format_GpuOver100_ClampsForDisplay()
    {
        // Summing per-engine instances can exceed 100; display clamps.
        var text = MetricsFormatter.Format(Snapshot(gpu: 173.0, gpuMem: Mb));

        Assert.Contains("GPU 100%", text);
    }

    [Fact]
    public void Format_CpuFull_ShowsHundred()
    {
        Assert.StartsWith("CPU 100.0%", MetricsFormatter.Format(Snapshot(cpu: 100.0)));
    }

    [Fact]
    public void Format_MultiGigabyteValues_StayInMb()
    {
        // Unit never switches to GB — a slow leak should read as one climbing number.
        var text = MetricsFormatter.Format(Snapshot(workingSet: 5L * 1024 * Mb));

        Assert.Contains("5120MB ws", text);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(Mb - 1, 0)]
    [InlineData(Mb, 1)]
    [InlineData(890 * Mb, 890)]
    public void ToMb_TruncatesToWholeMegabytes(long bytes, long expected)
        => Assert.Equal(expected, MetricsFormatter.ToMb(bytes));
}
