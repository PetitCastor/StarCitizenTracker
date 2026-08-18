namespace GameCapture.Contracts;

/// <summary>
/// Numeric parts of the wire contract that the engine and the plugins must agree on but that
/// proto3 cannot express: the default for a field whose "unset" is indistinguishable from 0,
/// and the payload cap that keeps one ROI from sinking a whole tick.
/// </summary>
public static class WireLimits
{
    /// <summary>OCR upscale applied when a RoiSpec leaves <c>scale</c> at its proto3 default.</summary>
    public const double DefaultOcrScale = 1.0;

    /// <summary>
    /// Cap on a single ROI_MODE_PIXELS payload (256 KiB, i.e. a 256x256 BGRA patch). gRPC's
    /// default 4 MiB receive limit applies to the entire TickResult, so an unbounded PIXELS ROI
    /// would fail the whole tick — every other ROI included — which is exactly the per-tick
    /// atomicity the contract promises. Engines reject oversized ROIs with a per-ROI error.
    /// PIXELS ROIs are meant for small colour probes (toggle strips), not screenshots.
    /// </summary>
    public const int MaxPixelBytes = 256 * 1024;

    /// <summary>
    /// Resolves a requested OCR scale to the one the engine will actually apply, before its own
    /// clamp to the OCR max dimension. Anything at or below zero (including the proto3 default
    /// and NaN) means "engine default".
    /// </summary>
    public static double NormalizeOcrScale(double requestedScale)
        => requestedScale > 0 ? requestedScale : DefaultOcrScale;

    /// <summary>True if a width x height BGRA patch fits under <see cref="MaxPixelBytes"/>.</summary>
    /// <remarks>
    /// Compares pixel counts rather than byte counts: multiplying two uints by 4 first can
    /// overflow even a signed 64-bit accumulator, and an overflowed size would compare as
    /// "fits".
    /// </remarks>
    public static bool FitsPixelBudget(uint width, uint height)
        => (ulong)width * height <= (ulong)(MaxPixelBytes / 4);
}
