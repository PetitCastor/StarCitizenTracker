using TrackingService;
using TrackingService.Replay;
using TrackingService.Trackers;
using Xunit;

namespace TrackingService.Tests;

/// <summary>
/// Fixture-based regression test for MissionTracker's accept-counter increment detection,
/// driven through ReplayRunner against a saved frame corpus. Requires a real Windows OCR
/// language pack — local-only, see Category trait. Skipped until a corpus exists.
/// </summary>
[Trait("Category", "Integration")]
public class MissionTrackerReplayTests
{
    private const string FixturesRoot = "Fixtures/Replay";

    [Fact(Skip = "awaiting in-game --save-frames corpus")]
    public async Task AcceptingOneMission_EmitsExactlyOneCapture()
    {
        var ocr = new OcrPipeline();
        var records = new List<TrackerRecord>();
        var tracker = new MissionTracker(ocr, records.Add, verbose: false, debugDir: null);

        await ReplayRunner.RunAsync(Path.Combine(FixturesRoot, "mission-accept-one"), [tracker]);

        Assert.Single(records);
    }
}
