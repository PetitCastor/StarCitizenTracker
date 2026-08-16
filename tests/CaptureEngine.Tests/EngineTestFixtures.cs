using CaptureContracts;
using CaptureContracts.Proto;
using TrackerSdk;

namespace CaptureEngine.Tests;

/// <summary>
/// Shared corpus and ROI definitions for the engine tests. The ROIs are the real ones a plugin
/// will subscribe (the refinery panel-state region and a REFINE-toggle colour probe), so the
/// tests exercise the same geometry path — reference space in, frame space out — that production
/// will, rather than a convenient synthetic rectangle.
/// </summary>
internal static class EngineTestFixtures
{
    /// <summary>Three frames copied from the monolith's refinery-confirm replay corpus.</summary>
    public const string ReplayDir = "Fixtures/engine-smoke";

    /// <summary>PanelStateRoi from RefineryTracker: SETUP | PROCESSING | COMPLETED.</summary>
    public static RoiSpec PanelStateRoi(string id = "panel") => new()
    {
        Id = id,
        Rect = new Rect { X = 900, Y = 265, Width = 250, Height = 55 },
        Scale = 3.0,
        Mode = RoiMode.Text,
    };

    /// <summary>A small colour probe: PIXELS ROIs are for toggle strips, not screenshots.</summary>
    public static RoiSpec ToggleStripRoi(string id = "toggle") => new()
    {
        Id = id,
        Rect = new Rect { X = 640, Y = 700, Width = 40, Height = 40 },
        Mode = RoiMode.Pixels,
    };

    /// <summary>An ROI a plugin could only produce by mistyping a constant: nowhere near the frame.</summary>
    public static RoiSpec OffFrameRoi(string id = "offscreen") => new()
    {
        Id = id,
        Rect = new Rect { X = 9000, Y = 5000, Width = 200, Height = 60 },
        Scale = 2.0,
        Mode = RoiMode.Text,
    };

    /// <summary>
    /// The same ROIs as the SDK expresses them. Derived from the proto factories above rather than
    /// restated, so an engine test and an SDK test can never drift into asserting different
    /// geometry while both claim to use "the panel ROI".
    /// </summary>
    public static RoiSubscription PanelStateSubscription(string id = "panel")
        => ToSubscription(PanelStateRoi(id));

    public static RoiSubscription ToggleStripSubscription(string id = "toggle")
        => ToSubscription(ToggleStripRoi(id));

    private static RoiSubscription ToSubscription(RoiSpec spec) => new(
        spec.Id,
        (spec.Rect ?? new Rect()).ToRoiRect(),
        spec.Scale,
        spec.Mode switch
        {
            RoiMode.Text => RoiKind.Text,
            RoiMode.Detailed => RoiKind.Detailed,
            RoiMode.Pixels => RoiKind.Pixels,
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Mode, "Unknown ROI mode."),
        });

    public static string[] ExpectedFrameNames() => Directory
        .GetFiles(ReplayDir, "*.png")
        .OrderBy(f => f, StringComparer.Ordinal)
        .Select(Path.GetFileName)
        .ToArray()!;
}
