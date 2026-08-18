using GameCapture.Contracts.Proto;
using GameCapture.Sdk;

namespace GameCapture.Engine.Tests;

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

    /// <summary>The refinery panel-state ROI: SETUP | PROCESSING | COMPLETED.</summary>
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
        => RoiSubscription.FromProto(PanelStateRoi(id));

    public static RoiSubscription ToggleStripSubscription(string id = "toggle")
        => RoiSubscription.FromProto(ToggleStripRoi(id));

    /// <summary>Corpus frames in the order the scan loop will hand them out.</summary>
    public static string[] ExpectedFrameNames() => ReplayFrameSource
        .EnumerateCorpus(ReplayDir)
        .Select(Path.GetFileName)
        .ToArray()!;
}
