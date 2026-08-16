using Common;
using RefineryPlugin.Orders;
using Xunit;
using static RefineryPlugin.Tests.TickFactory;

namespace RefineryPlugin.Tests;

/// <summary>
/// What an unreadable ROI must NOT do. The monolith read the panel header and the modal before it
/// touched any state, so an OCR failure there aborted the whole tick and nothing moved; over the
/// wire the failure arrives as a flag while the ROI still reads as empty text, which is exactly
/// what a closed panel and a dismissed modal look like. These pin the reconstructed abort — an
/// errored ROI is never allowed to act as an observation.
/// </summary>
public class RefineryErroredRoiTests : IDisposable
{
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
    private const string YieldTotal = "YIELD 1850";

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("refinery-plugin-error-tests");
    private readonly ConsoleSink _sink = new();
    private readonly List<TrackerRecord> _records = [];
    private readonly OrderLedger _ledger;
    private readonly RefineryLogic _logic;

    public RefineryErroredRoiTests()
    {
        _ledger = new OrderLedger(Path.Combine(_dir.FullName, "orders.jsonl"));
        _ledger.Load();
        _logic = new RefineryLogic(_records.Add, _sink, verbose: false, dumpFrame: null, _ledger);
    }

    public void Dispose()
    {
        _sink.Dispose();
        _dir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private Task SetupTick(params string[] errored) => _logic.OnTickAsync(Tick("SETUP",
        station: Station, setupRows: SetupRows, toggle: ToggleOn, erroredRois: errored));

    private Task YieldTick(string panel, string modal = "", string total = YieldTotal,
        params string[] errored)
        => _logic.OnTickAsync(Tick(panel, modal: modal, station: Station, yieldTotal: total,
            yieldRows: YieldRows, erroredRois: errored));

    /// <summary>
    /// The fabricated-delivery case. After a CANCEL the panel is still COMPLETED with the modal
    /// dismissed; if the panel ROI then errors, an empty read classifies as None and the machine
    /// sees "completed, modal was seen, panel gone" — a delivery that never happened, written to
    /// the ledger as Collected.
    /// </summary>
    [Fact]
    public async Task ErroredPanelRoiAfterACancel_DoesNotFabricateACollect()
    {
        await YieldTick("COMPLETED");
        await YieldTick("COMPLETED", modal: "CONFIRM DELIVERY");
        await YieldTick("COMPLETED");                                   // CANCEL: modal dismissed
        await YieldTick("", errored: [Rois.Panel.Id]);                  // panel unreadable, not gone

        Assert.Equal(OrderState.Ready, Assert.Single(_ledger.All).State);
    }

    /// <summary>
    /// The mirror case: the modal is still on screen but its ROI errors. An empty read is the
    /// CANCEL signal, which clears the delivery latch and downgrades the real delivery to the G2
    /// residual — the order would sit Ready forever.
    /// </summary>
    [Fact]
    public async Task ErroredModalRoiDuringADelivery_StillCollects()
    {
        await YieldTick("COMPLETED");
        await YieldTick("COMPLETED", modal: "CONFIRM DELIVERY");
        await YieldTick("COMPLETED", modal: "CONFIRM DELIVERY", errored: [Rois.Modal.Id]);
        await _logic.OnTickAsync(Tick(""));

        Assert.Equal(OrderState.Collected, Assert.Single(_ledger.All).State);
    }

    /// <summary>
    /// Three unreadable panel ticks in a row are not three ticks of "SETUP closed": that would
    /// satisfy the departure debouncer with DepartedTo == None and throw away every scroll-stitched
    /// row the user had accumulated.
    /// </summary>
    [Fact]
    public async Task ErroredPanelRoiDuringSetup_DoesNotDiscardTheAccumulator()
    {
        await SetupTick();

        for (var i = 0; i < SetupDepartureDebouncer.ConfirmTicks; i++)
            await SetupTick(Rois.Panel.Id);

        // The rows survived: the submit that follows a real departure still carries them.
        await YieldTick("PROCESSING", total: "");
        await YieldTick("PROCESSING", total: "");
        await YieldTick("PROCESSING", total: "");

        var order = Assert.Single(_ledger.All);
        Assert.Equal([1200, 800], order.Materials.Select(m => m.QtyCscu));
    }

    /// <summary>
    /// An errored YIELD total must not be read as "no total printed": the checksum would fail, a
    /// clean read would be filed Partial, and the console would tell the user to scroll a list that
    /// was never truncated.
    /// </summary>
    [Fact]
    public async Task ErroredYieldTotalRoi_DoesNotFileTheReadAsPartial()
    {
        await YieldTick("COMPLETED", errored: [Rois.YieldTotal.Id]);
        Assert.Empty(_ledger.All); // the whole observation waits for a tick that has the total

        await YieldTick("COMPLETED");

        var order = Assert.Single(_ledger.All);
        Assert.Equal(Completeness.Complete, order.Completeness);
    }

    /// <summary>
    /// A completed panel whose rows never parsed leaves the previous order as the only thing
    /// <c>_lastOrder</c> points at. Collecting on panel-state alone would then mark an order the
    /// user never delivered — while the one they did deliver goes unrecorded.
    /// </summary>
    [Fact]
    public async Task DeliveryOfAnOrderThatWasNeverRead_DoesNotCollectThePreviousOrder()
    {
        // Order A: observed, delivered, collected.
        await YieldTick("COMPLETED");
        await YieldTick("COMPLETED", modal: "CONFIRM DELIVERY");
        await _logic.OnTickAsync(Tick(""));
        var orderA = Assert.Single(_ledger.All);
        Assert.Equal(OrderState.Collected, orderA.State);

        // Order B: its rows error on every tick, so nothing about it ever reaches the ledger.
        await YieldTick("COMPLETED", errored: [Rois.YieldList.Id]);
        await YieldTick("COMPLETED", modal: "CONFIRM DELIVERY", errored: [Rois.YieldList.Id]);
        await _logic.OnTickAsync(Tick(""));

        // Still just order A, and its record is untouched — no second collect was fabricated.
        var after = Assert.Single(_ledger.All);
        Assert.Equal(orderA.Id, after.Id);
        Assert.Equal(orderA.LastSeen, after.LastSeen);
    }

    /// <summary>
    /// A skipped tick is a pause, not a stop: once the ROI reads again the panel is picked up on
    /// the next frame, with whatever was already stitched still intact.
    /// </summary>
    [Fact]
    public async Task AfterAnUnreadableRoiRecovers_ReadsResume()
    {
        await SetupTick(Rois.SetupList.Id);
        await SetupTick();

        // The hotkey forces whatever is accumulated into the ledger — it is the rows from the tick
        // that read cleanly, so nothing was lost and nothing blank was stitched over them.
        await _logic.OnTickAsync(Tick("SETUP", station: Station, setupRows: SetupRows,
            toggle: ToggleOn, manual: true));

        var order = Assert.Single(_ledger.All);
        Assert.Equal(["TITANIUM (ORE)", "QUANTANIUM"], order.Materials.Select(m => m.Name));
        Assert.All(order.Materials, m => Assert.True(m.RefineOn));
    }
}
