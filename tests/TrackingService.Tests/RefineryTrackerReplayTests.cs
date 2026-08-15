using TrackingService;
using TrackingService.Orders;
using TrackingService.Replay;
using TrackingService.Trackers;
using Xunit;

namespace TrackingService.Tests;

/// <summary>
/// Fixture-based regression tests driving the real RefineryTracker + OcrPipeline + OrderLedger through
/// saved frame corpora via ReplayRunner. Requires a real Windows OCR language pack, so these only run
/// locally (not on a hypothetical CI runner) — see Category trait. Each uses a throwaway temp-file
/// ledger so real data is never touched. Skipped until the in-game --save-frames corpus exists under
/// Fixtures/Replay/.
/// </summary>
[Trait("Category", "Integration")]
public class RefineryTrackerReplayTests
{
    private const string FixturesRoot = "Fixtures/Replay";

    private static OrderLedger TempLedger()
    {
        var path = Path.Combine(Path.GetTempPath(), "sc-replay-" + Guid.NewGuid().ToString("N"), "orders.jsonl");
        var ledger = new OrderLedger(path);
        ledger.Load();
        return ledger;
    }

    private static RefineryTracker NewTracker(OcrPipeline ocr, ConsoleSink sink, OrderLedger ledger)
        => new(ocr, _ => { }, sink, verbose: false, debugDir: null, ledger);

    [Fact]
    public async Task FullConfirmSequence_ProducesOneCollectedOrder()
    {
        var ocr = new OcrPipeline();
        using var sink = new ConsoleSink();
        var ledger = TempLedger();
        var tracker = NewTracker(ocr, sink, ledger);

        await ReplayRunner.RunAsync(Path.Combine(FixturesRoot, "refinery-confirm"), [tracker], sink);

        var order = Assert.Single(ledger.All);
        Assert.Equal(OrderState.Collected, order.State);
        Assert.Equal(Completeness.Complete, order.Completeness);
    }

    [Fact]
    public async Task RawToRefinedRename_MergesIntoOneOrder()
    {
        // The refinery renames the raw input to its refined product between panels
        // (SETUP "ICE (RAW)" -> PROCESSING/COMPLETED "PRESSURIZED ICE"). Quality is stable
        // across the rename, so the two panels must resolve to ONE order with ONE material,
        // not split into a yield-less SETUP order plus an orphaned COMPLETED order.
        // This corpus was hotkey-captured through COMPLETED only (the live collection landed a few
        // seconds after the last frame), so it verifies the merge and reaches Ready/Complete — the
        // Collected transition is exercised by FullConfirmSequence, which captured the modal close.
        var ocr = new OcrPipeline();
        using var sink = new ConsoleSink();
        var ledger = TempLedger();
        var tracker = NewTracker(ocr, sink, ledger);

        await ReplayRunner.RunAsync(Path.Combine(FixturesRoot, "refinery-ice-rename"), [tracker], sink);

        var order = Assert.Single(ledger.All);
        Assert.True(order.State >= OrderState.Ready, $"expected Ready or later, got {order.State}");
        Assert.Equal(Completeness.Complete, order.Completeness);
        var material = Assert.Single(order.Materials);
        Assert.Equal(714, material.Quality);
        Assert.True(material.YieldCscu > 0, "refined yield must merge onto the material");
        Assert.NotNull(order.TotalYieldCscu);
    }

    [Fact(Skip = "awaiting in-game --save-frames corpus")]
    public async Task CancelSequence_ProducesNoLedgerRecord()
    {
        var ocr = new OcrPipeline();
        using var sink = new ConsoleSink();
        var ledger = TempLedger();
        var tracker = NewTracker(ocr, sink, ledger);

        await ReplayRunner.RunAsync(Path.Combine(FixturesRoot, "refinery-cancel"), [tracker], sink);

        Assert.Empty(ledger.All); // a cancelled setup is provisional and never persisted
    }

    [Fact(Skip = "awaiting in-game --save-frames corpus")]
    public async Task OverflowSequence_MoreThanTenMaterials_MarksPartial()
    {
        var ocr = new OcrPipeline();
        using var sink = new ConsoleSink();
        var ledger = TempLedger();
        var tracker = NewTracker(ocr, sink, ledger);

        await ReplayRunner.RunAsync(Path.Combine(FixturesRoot, "refinery-overflow"), [tracker], sink);

        var order = Assert.Single(ledger.All);
        Assert.Equal(Completeness.Partial, order.Completeness);
    }

    [Fact(Skip = "awaiting in-game --save-frames corpus")]
    public async Task ColdStartOnCompleted_ProducesOneRecord()
    {
        var ocr = new OcrPipeline();
        using var sink = new ConsoleSink();
        var ledger = TempLedger();
        var tracker = NewTracker(ocr, sink, ledger);

        await ReplayRunner.RunAsync(Path.Combine(FixturesRoot, "refinery-coldstart"), [tracker], sink);

        Assert.Single(ledger.All); // captured with no prior SETUP frame
    }

    [Fact(Skip = "awaiting in-game --save-frames corpus")]
    public async Task OccludedOnlyThenGone_FlaggedNeverCrashes()
    {
        var ocr = new OcrPipeline();
        using var sink = new ConsoleSink();
        var ledger = TempLedger();
        var tracker = NewTracker(ocr, sink, ledger);

        await ReplayRunner.RunAsync(Path.Combine(FixturesRoot, "refinery-occluded"), [tracker], sink);

        var order = Assert.Single(ledger.All);
        Assert.NotEqual(Completeness.Complete, order.Completeness);
    }
}
