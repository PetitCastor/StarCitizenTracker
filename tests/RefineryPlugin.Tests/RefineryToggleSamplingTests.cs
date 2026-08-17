using RefineryPlugin.Orders;
using TrackerSdk;
using TrackerSdk.Testing;
using Xunit;
using static RefineryPlugin.Tests.RefineryTicks;

namespace RefineryPlugin.Tests;

/// <summary>
/// The REFINE toggle is the one reading that is not text: the engine ships the pill strip as raw
/// BGRA and the plugin samples a column of it per row. These pin both halves — the threshold itself
/// and the projection from a crop-space row centre to the frame pixel that gets sampled.
/// </summary>
public class RefineryToggleSamplingTests
{
    private static readonly RowSpec[] Rows =
    [
        new("TITANIUM (ORE)", [262, 1200, 1100], 100),
        new("QUANTANIUM", [785, 800, 750], 200),
    ];

    private static readonly TimeSpan Scan = EngineDefaults.DefaultScanInterval;
    private static readonly DateTimeOffset T0 = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(200, 50, true)]   // clearly orange/red (ON)
    [InlineData(80, 80, false)]   // clearly neutral gray
    [InlineData(141, 78, true)]   // just over both thresholds
    [InlineData(141, 79, false)]  // R > 140 but R <= B*1.8
    [InlineData(140, 50, false)]  // R not strictly > 140
    [InlineData(251, 244, false)] // white knob (OFF) — R high but R <= B*1.8
    public void IsRefineOn_AppliesColorThreshold(byte r, byte b, bool expected)
        => Assert.Equal(expected, RefineryLogic.IsRefineOn((b, 0, r)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToggleColour_DecidesTheRefineFlagOnEveryAccumulatedRow(bool on)
    {
        var dir = Directory.CreateTempSubdirectory("refinery-plugin-toggle-tests");
        try
        {
            var services = new FakePluginServices();
            var ledger = new OrderLedger(Path.Combine(dir.FullName, "orders.jsonl"));
            var logic = new RefineryLogic(services, ledger, 3 * services.Engine.ScanInterval);

            // Tick 1 stitches the rows; the hotkey on tick 2 forces the accumulator into the ledger
            // (manual runs before the scan), which is the only place the flags are observable.
            await logic.OnTickAsync(Tick(T0, "SETUP", station: "STANTON GATEWAY",
                setupRows: Rows, toggle: on ? ToggleOn : ToggleOff));
            await logic.OnTickAsync(Tick(T0.Add(Scan), "SETUP", station: "STANTON GATEWAY",
                setupRows: Rows, toggle: on ? ToggleOn : ToggleOff, manual: true));

            var order = Assert.Single(ledger.All);
            Assert.Equal(2, order.Materials.Count);
            Assert.All(order.Materials, m => Assert.Equal(on, m.RefineOn));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// An errored pixel strip skips the whole SETUP read: sampling a strip that never arrived would
    /// clamp to black and file every row as "not refined", which is a silent data error rather than
    /// a missing one.
    /// </summary>
    [Fact]
    public async Task WithAnErroredToggleStrip_NothingIsAccumulated()
    {
        var dir = Directory.CreateTempSubdirectory("refinery-plugin-toggle-tests");
        try
        {
            var services = new FakePluginServices();
            var ledger = new OrderLedger(Path.Combine(dir.FullName, "orders.jsonl"));
            var logic = new RefineryLogic(services, ledger, 3 * services.Engine.ScanInterval);

            await logic.OnTickAsync(Tick(T0, "SETUP", station: "STANTON GATEWAY", setupRows: Rows,
                toggle: ToggleOn, erroredRois: [Rois.Toggles.Id]));
            await logic.OnTickAsync(Tick(T0.Add(Scan), "SETUP", station: "STANTON GATEWAY", setupRows: Rows,
                toggle: ToggleOn, erroredRois: [Rois.Toggles.Id], manual: true));

            Assert.Empty(ledger.All);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
