using GameCapture.Contracts;
using GameCapture.Sdk;

namespace MyCapturePlugin;

/// <summary>
/// The regions this plugin subscribes, in reference space (2560x1440). Static for the life of the
/// process because the host reads it once per connect and sends it as the initial subscription:
/// per-tick atomicity means there is no mid-tick round-trip that could add a region later.
/// </summary>
public static class Rois
{
    /// <summary>The panel line the counter lives on. Scale is the OCR upscale factor —
    /// small text needs 2-4; 0 means "engine default". Nudge the rect and scale once you have a
    /// real corpus: see the calibration workflow in README.md.</summary>
    public static readonly RoiSubscription Counter =
        new("counter", new RoiRect(1000, 110, 420, 100), 3.0, RoiKind.Text);

    /// <summary>A field, not <c>=> [Counter]</c>: the set never changes, and an
    /// expression-bodied property would build a fresh array on every read.</summary>
    public static readonly IReadOnlyList<RoiSubscription> All = [Counter];
}
