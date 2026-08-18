using GameCapture.Engine.Metrics;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// The monolith's MetricsSamplerCpuPercentTests, now the only copy: same inputs, same expected
/// outputs, on the engine's MetricsSampler.ComputeCpuPercent. The clamping cases came from real
/// timer jitter, so they stay as accumulated rather than re-derived from the arithmetic.
/// </summary>
public class MetricsSamplerCpuPercentTests
{
    [Theory]
    // half a core busy for 1s on 4 cores = 12.5% of the machine
    [InlineData(500, 1000, 4, 12.5)]
    // one core fully busy on 4 cores = 25%
    [InlineData(1000, 1000, 4, 25.0)]
    // all 4 cores fully busy = 100%
    [InlineData(4000, 1000, 4, 100.0)]
    // single-core machine, fully busy
    [InlineData(1000, 1000, 1, 100.0)]
    // idle
    [InlineData(0, 1000, 4, 0.0)]
    public void ComputeCpuPercent_TypicalReadings(double cpuMs, double wallMs, int cores, double expected)
        => Assert.Equal(expected,
            MetricsSampler.ComputeCpuPercent(
                TimeSpan.FromMilliseconds(cpuMs), TimeSpan.FromMilliseconds(wallMs), cores),
            precision: 10);

    [Fact]
    public void ComputeCpuPercent_OverHundred_ClampsToHundred()
    {
        // Timer jitter can make cpuDelta exceed cores*wallDelta slightly.
        var pct = MetricsSampler.ComputeCpuPercent(
            TimeSpan.FromMilliseconds(8000), TimeSpan.FromMilliseconds(1000), 4);

        Assert.Equal(100.0, pct);
    }

    [Fact]
    public void ComputeCpuPercent_NegativeCpuDelta_ClampsToZero()
        => Assert.Equal(0.0, MetricsSampler.ComputeCpuPercent(
            TimeSpan.FromMilliseconds(-50), TimeSpan.FromMilliseconds(1000), 4));

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void ComputeCpuPercent_ZeroOrNegativeWallDelta_ReturnsZero(double wallMs)
        => Assert.Equal(0.0, MetricsSampler.ComputeCpuPercent(
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(wallMs), 4));

    [Fact]
    public void ComputeCpuPercent_ZeroProcessorCount_ReturnsZero()
        => Assert.Equal(0.0, MetricsSampler.ComputeCpuPercent(
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000), 0));
}
