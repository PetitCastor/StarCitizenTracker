using RefineryPlugin.Orders;
using TrackerSdk;
using TrackerSdk.Testing;
using Xunit;
using static RefineryPlugin.Tests.RefineryTicks;

namespace RefineryPlugin.Tests;

/// <summary>
/// The whole order lifecycle driven by fabricated ticks — SETUP stitching, the debounced submit,
/// the COMPLETED checksum, the Confirm modal and the collect. Each panel used to be a frame plus
/// four OCR calls; it is now one tick object, and the point of the walk is that the outcome the
/// monolith produced (exactly one ledger record, ending Collected, materials merged across panels)
/// is unchanged by the move.
/// </summary>
/// <remarks>
/// Ticks carry timestamps spaced by the engine's scan interval, because the SETUP-departure debounce
/// is now a wall-time window (three scans) rather than a tick count — so three consecutive non-SETUP
/// ticks confirm a departure exactly as the monolith's three-tick rule did.
/// </remarks>
public class RefineryLogicWalkTests : IDisposable
{
    // The SETUP panel lists QUALITY, QTY and YIELD; the yield panels drop QTY and rename the ore
    // form away ("TITANIUM (ORE)" -> "TITANIUM"), which is exactly the rename OrderMatcher exists
    // to see through.
    private static readonly RowSpec[] SetupRows =
    [
        new("TITANIUM (ORE)", [262, 1200, 1100], 100),
        new("QUANTANIUM", [785, 800, 750], 200),
    ];

    private static readonly RowSpec[] YieldRows =
    [
        new("TITANIUM", [262, 1100], 100),
        new("QUANTANIUM", [785, 750], 200),
    ];

    private const string Station = "STANTON GATEWAY";
    private const string Process = "Pyrometric Process";
    private const string Footer = "TOTAL COST 12,500 aUEC\r\nPROCESSING TIME 03:12:36";
    private const string YieldTotal = "YIELD 1850"; // == 1100 + 750, so the checksum passes

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("refinery-plugin-tests");
    private readonly FakePluginServices _services = new();
    private readonly OrderLedger _ledger;
    private readonly RefineryLogic _logic;

    // Three scans is the debounce window; ticks advance the clock one scan each, so the semantics
    // match the monolith's three-tick AnchorGoneThreshold.
    private static readonly TimeSpan Scan = EngineDefaults.DefaultScanInterval;
    private DateTimeOffset _now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
    private DateTimeOffset Next()
    {
        var t = _now;
        _now = _now.Add(Scan);
        return t;
    }

    public RefineryLogicWalkTests()
    {
        _ledger = new OrderLedger(Path.Combine(_dir.FullName, "orders.jsonl"));
        _ledger.Load();
        _logic = new RefineryLogic(_services, _ledger, 3 * _services.Engine.ScanInterval);
    }

    public void Dispose()
    {
        _dir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private Task SetupTick() => _logic.OnTickAsync(Tick(Next(), "SETUP",
        station: Station, process: Process, footer: Footer,
        setupRows: SetupRows, toggle: ToggleOn));

    private Task YieldTick(string panel, string modal = "", string total = YieldTotal)
        => _logic.OnTickAsync(Tick(Next(), panel, modal: modal,
            station: Station, process: Process, footer: Footer,
            yieldTotal: total, yieldRows: YieldRows));

    [Fact]
    public async Task FullOrderWalk_EndsWithExactlyOneCollectedOrder()
    {
        // SETUP: three ticks of the same list — the scroll-stitch accumulator must collapse them.
        for (var i = 0; i < 3; i++)
            await SetupTick();

        Assert.Empty(_ledger.All); // nothing is written until the panel leaves SETUP

        // PROCESSING: the yield panel is observed straight away, and the SETUP departure is only
        // trusted (and submitted) after three non-SETUP ticks have spanned the debounce window.
        await YieldTick("PROCESSING", total: "");
        var afterFirstProcessing = Assert.Single(_ledger.All);
        Assert.Equal(OrderState.Processing, afterFirstProcessing.State);

        await YieldTick("PROCESSING", total: "");
        await YieldTick("PROCESSING", total: "");

        // The submit merged into the same record rather than opening a second one, and brought the
        // SETUP-only columns (QTY, the refine choice, cost/ETA) with it.
        var afterSubmit = Assert.Single(_ledger.All);
        Assert.Equal([1200, 800], afterSubmit.Materials.Select(m => m.QtyCscu));
        Assert.All(afterSubmit.Materials, m => Assert.True(m.RefineOn));
        Assert.Equal("12,500 aUEC", afterSubmit.Cost);
        Assert.Equal("03:12:36", afterSubmit.Eta);
        Assert.Equal(["PROCESSING", "SETUP"], afterSubmit.Sources);

        // COMPLETED: the printed YIELD total matches the row sum on a non-occluded read, so the
        // record is Ready and trusted Complete.
        await YieldTick("COMPLETED");

        var ready = Assert.Single(_ledger.All);
        Assert.Equal(OrderState.Ready, ready.State);
        Assert.Equal(Completeness.Complete, ready.Completeness);
        Assert.Equal(1850, ready.TotalYieldCscu);
        Assert.Equal([1100, 750], ready.Materials.Select(m => m.YieldCscu));

        // Confirm-Delivery modal: the read is occluded, so it files as Unknown — and Unknown must
        // never pull a record back down from Complete (H3).
        await YieldTick("COMPLETED", modal: "CONFIRM DELIVERY");
        Assert.Equal(Completeness.Complete, Assert.Single(_ledger.All).Completeness);

        // Panel gone with the modal cleared: the delivery is recognised.
        await _logic.OnTickAsync(Tick(Next(), ""));

        var collected = Assert.Single(_ledger.All);
        Assert.Equal(OrderState.Collected, collected.State);
        Assert.Equal(Completeness.Complete, collected.Completeness);
        Assert.Equal(Station, collected.Station);
        Assert.Equal(Process, collected.Process);

        // Three console captures: the SETUP submit, the order turning Ready, and the collect.
        Assert.Equal(3, _services.Emitted.Count);
        Assert.All(_services.Emitted, r => Assert.Equal(TriggerKind.Auto, r.Trigger));
        Assert.All(_services.Emitted, r => Assert.Equal("refinery", r.Tracker));
    }

    /// <summary>
    /// CANCEL on the confirm modal leaves the COMPLETED panel showing, and the panel closing later
    /// must not fabricate a delivery — the order stays Ready. Same rule the monolith's state
    /// machine tests pin down, here through the whole tick path.
    /// </summary>
    [Fact]
    public async Task CancelledDelivery_LeavesTheOrderReady()
    {
        for (var i = 0; i < 3; i++)
            await SetupTick();

        await YieldTick("COMPLETED");
        await YieldTick("COMPLETED", modal: "CONFIRM DELIVERY");
        await YieldTick("COMPLETED");                          // CANCEL: modal dismissed, panel still up
        await _logic.OnTickAsync(Tick(Next(), ""));            // panel finally closes

        Assert.Equal(OrderState.Ready, Assert.Single(_ledger.All).State);
    }

    /// <summary>
    /// A ROI the engine flagged as failed must skip the tick rather than stitch a blank list over
    /// the rows already accumulated, and the order has to come through the rest of the walk
    /// unharmed. The header fields take the same treatment implicitly: last-good-wins.
    /// </summary>
    [Fact]
    public async Task SetupTickWithAnErroredListRoi_KeepsTheAccumulatedRows()
    {
        await SetupTick();
        await _logic.OnTickAsync(Tick(Next(), "SETUP", station: Station, process: Process, footer: Footer,
            setupRows: SetupRows, toggle: ToggleOn, erroredRois: [Rois.SetupList.Id]));
        await SetupTick();

        await YieldTick("PROCESSING", total: "");
        await YieldTick("PROCESSING", total: "");
        await YieldTick("PROCESSING", total: "");

        var order = Assert.Single(_ledger.All);
        Assert.Equal([1200, 800], order.Materials.Select(m => m.QtyCscu));
    }

    /// <summary>
    /// The hotkey escape hatch: with rows accumulated but no panel transition seen, a manual tick
    /// forces the SETUP order into the ledger as a Manual capture.
    /// </summary>
    [Fact]
    public async Task ManualTick_ForcesTheAccumulatedSetupOrderIntoTheLedger()
    {
        await SetupTick();

        await _logic.OnTickAsync(Tick(Next(), "SETUP", station: Station, process: Process, footer: Footer,
            setupRows: SetupRows, toggle: ToggleOn, manual: true));

        var order = Assert.Single(_ledger.All);
        Assert.Equal(OrderState.Pending, order.State);
        Assert.Equal(["SETUP"], order.Sources);
        Assert.Equal(TriggerKind.Manual, Assert.Single(_services.Emitted).Trigger);
    }

    /// <summary>
    /// Manual with nothing accumulated is the calibration aid instead: the raw list and footer text
    /// of the tick, so a ROI can be re-aimed from what the engine actually read. A DETAILED
    /// subscription still carries plain text, which is why the list ROI answers here at all.
    /// </summary>
    [Fact]
    public async Task ManualTickWithNothingAccumulated_EmitsTheRawRoiText()
    {
        await _logic.OnTickAsync(Tick(Next(), "", footer: Footer, setupRows: SetupRows, manual: true));

        var record = Assert.Single(_services.Emitted);
        Assert.Equal(TriggerKind.Manual, record.Trigger);
        Assert.Contains("[raw list ROI]", record.RawText);
        Assert.Contains("TITANIUM (ORE) 262 1200 1100", record.RawText);
        Assert.Contains("[raw footer ROI]", record.RawText);
        Assert.Contains("12,500 aUEC", record.RawText);
        Assert.Empty(_ledger.All);
    }
}
