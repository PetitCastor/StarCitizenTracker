using TrackingService;
using TrackingService.Replay;
using TrackingService.Trackers;
using Xunit;

namespace TrackingService.Tests;

/// <summary>
/// Fixture-based regression tests driving the real RefineryTracker + OcrPipeline through
/// saved frame corpora via ReplayRunner. Requires a real Windows OCR language pack, so these
/// only run locally (not on a hypothetical CI runner) — see Category trait.
/// Skipped until the in-game --save-frames corpus (setup open/scroll/toggle/CONFIRM/CANCEL)
/// exists under Fixtures/Replay/.
/// </summary>
[Trait("Category", "Integration")]
public class RefineryTrackerReplayTests
{
    private const string FixturesRoot = "Fixtures/Replay";

    [Fact(Skip = "awaiting in-game --save-frames corpus")]
    public async Task FullConfirmSequence_CommitsExactlyOnce()
    {
        var ocr = new OcrPipeline();
        var records = new List<TrackerRecord>();
        var tracker = new RefineryTracker(ocr, records.Add, verbose: false, debugDir: null);

        await ReplayRunner.RunAsync(Path.Combine(FixturesRoot, "refinery-confirm"), [tracker]);

        Assert.Single(records);
    }

    [Fact(Skip = "awaiting in-game --save-frames corpus")]
    public async Task CancelSequence_DoesNotCommit()
    {
        var ocr = new OcrPipeline();
        var records = new List<TrackerRecord>();
        var tracker = new RefineryTracker(ocr, records.Add, verbose: false, debugDir: null);

        await ReplayRunner.RunAsync(Path.Combine(FixturesRoot, "refinery-cancel"), [tracker]);

        Assert.Empty(records);
    }
}
