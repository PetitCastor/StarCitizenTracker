using GameCapture.Contracts;

namespace GameCapture.Sdk;

/// <summary>
/// The engine's constants, stated once for plugins that need them before they have an engine to ask.
/// </summary>
/// <remarks>
/// Every value here was a literal copied into a plugin at some point — the pipe name in a config
/// default, 2560x1440 in a ROI table, "500 ms so three ticks is 1.5 s" in a debounce comment. The
/// values themselves live in <see cref="GameCapture.Contracts"/> where they are also the wire's business;
/// this class re-exports rather than restates them, so there is still exactly one definition.
/// <para>
/// These are DEFAULTS, not readings. A running engine reports what it actually does through
/// <see cref="EngineInfo"/> — in particular <see cref="EngineInfo.ScanInterval"/>, which is
/// configurable and clamped engine-side. Prefer the reading whenever there is a session; use these
/// only where there is none.
/// </para>
/// </remarks>
public static class EngineDefaults
{
    /// <summary>The pipe a stock engine listens on, and the default a plugin's config falls back to.</summary>
    public const string PipeName = PipeContract.DefaultPipeName;

    /// <summary>
    /// Reference-space width. ROIs are declared against this, never against the capture resolution:
    /// the engine scales them per frame (see <see cref="RoiScaler"/>), so a plugin's constants stay
    /// valid on any screen.
    /// </summary>
    public const int ReferenceWidth = RoiScaler.ReferenceWidth;

    /// <summary>Reference-space height; see <see cref="ReferenceWidth"/>.</summary>
    public const int ReferenceHeight = RoiScaler.ReferenceHeight;

    /// <summary>
    /// Cadence a stock engine scans at. What a plugin counting ticks should assume only until
    /// <see cref="EngineInfo.ScanInterval"/> tells it what this engine really does.
    /// </summary>
    public static readonly TimeSpan DefaultScanInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The most BGRA a single <see cref="RoiKind.Pixels"/> region may carry on one tick. A region
    /// whose scaled area exceeds it fails with a per-ROI error rather than sinking the whole tick,
    /// so a plugin sizing a colour probe wants to stay under it by construction.
    /// </summary>
    public const int MaxPixelBytes = WireLimits.MaxPixelBytes;

    /// <summary>
    /// Channel order of that buffer: B, G, R, A — which is the order
    /// <see cref="PixelPatchSampler"/> returns its samples in, and the order a plugin's colour
    /// predicates must be written against. Stated because BGRA and RGBA differ only in results that
    /// look plausible: a red/blue swap turns "the toggle is orange" into "the toggle is blue".
    /// </summary>
    public const string PixelChannelOrder = "BGRA";
}
