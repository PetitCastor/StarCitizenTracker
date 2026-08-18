using GameCapture.Contracts;
using GameCapture.Sdk;

namespace RefineryPlugin;

/// <summary>
/// The regions this plugin subscribes, in reference space. Static for the life of the process:
/// per-tick atomicity means every decision is made from one tick, so the set a tick can answer
/// must be complete before the tick arrives — there is no mid-tick round-trip to add a ROI.
/// </summary>
/// <remarks>
/// Deliberate design change against the monolith: RefineryTracker gated its reads to save in-process
/// OCR — the Confirm-Delivery modal was only read when a panel was live (<c>needModal</c>), and
/// station/process/footer only on every 4th tick. Engine-side those reads cost about 1.3 ms each at
/// 2 Hz, which is not worth a gate, so the plugin subscribes the full set on every tick and the
/// header cadence is gone. Per-tick semantics are unchanged: everything still comes from one frame.
/// </remarks>
internal static class Rois
{
    private const double HeaderScale = 3.0;
    private const double ListScale = 2.5;
    private const double FooterScale = 3.0;

    // Calibrated against the 2560x1440 corpus (Fixtures/Replay/refinery-confirm) in reference
    // coordinates; the engine maps them to the actual frame size at scan time.
    public static readonly RoiSubscription Panel =                                    // SETUP | PROCESSING | COMPLETED
        new("panel", new RoiRect(900, 265, 250, 55), HeaderScale, RoiKind.Text);
    public static readonly RoiSubscription Station =                                  // "STANTON GATEWAY"
        new("station", new RoiRect(320, 190, 340, 55), HeaderScale, RoiKind.Text);
    public static readonly RoiSubscription Process =                                  // "Pyrometric Chromalysis"
        new("process", new RoiRect(650, 515, 440, 48), HeaderScale, RoiKind.Text);
    public static readonly RoiSubscription SetupList =                                // SETUP list: NAME QUALITY QTY YIELD
        new("setupList", new RoiRect(650, 640, 400, 270), ListScale, RoiKind.Detailed);
    public static readonly RoiSubscription Footer =                                   // TOTAL COST / PROCESSING TIME
        new("footer", new RoiRect(650, 950, 440, 120), FooterScale, RoiKind.Text);
    public static readonly RoiSubscription Toggles =                                  // SETUP refine toggles
        new("toggles", new RoiRect(1055, 645, 40, 250), 1.0, RoiKind.Pixels);
    public static readonly RoiSubscription YieldList =                                // PROCESSING/COMPLETED: NAME QUALITY YIELD ...
        new("yieldList", new RoiRect(650, 395, 470, 210), ListScale, RoiKind.Detailed);
    public static readonly RoiSubscription YieldTotal =                               // "YIELD 303 cSCU" checksum line
        new("yieldTotal", new RoiRect(650, 805, 480, 48), HeaderScale, RoiKind.Text);
    public static readonly RoiSubscription Modal =                                    // Confirm Delivery modal
        new("modal", new RoiRect(1052, 582, 625, 225), HeaderScale, RoiKind.Text);

    /// <summary>Reference-space sample column inside the toggle pill.</summary>
    public const int ToggleColumnX = 1073;

    // A field, not an expression-bodied property: the set never changes, and `=> [Panel, ...]`
    // would build a fresh array on every read.
    public static readonly IReadOnlyList<RoiSubscription> All =
        [Panel, Station, Process, SetupList, Footer, Toggles, YieldList, YieldTotal, Modal];
}
