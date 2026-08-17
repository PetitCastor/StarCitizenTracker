using CaptureContracts;
using CaptureContracts.Proto;

namespace TrackerSdk;

/// <summary>What the engine should do with a subscribed region. Mirrors the wire's RoiMode, but
/// plugins never touch generated types — the enum is restated so a plugin can be written against
/// the SDK alone.</summary>
public enum RoiKind
{
    /// <summary>Plain OCR text.</summary>
    Text,

    /// <summary>OCR with per-word geometry (needed by parsers that read columns by position).</summary>
    Detailed,

    /// <summary>Raw BGRA bytes at 1:1, no OCR — colour probes such as the refinery toggle strip.</summary>
    Pixels,
}

/// <summary>One subscribed region in reference-space (2560x1440) coordinates.</summary>
/// <remarks>
/// Reference space, never frame space: the engine owns the scaling so a plugin's ROI constants
/// stay valid on any capture resolution. <paramref name="Scale"/> is the OCR upscale factor and is
/// ignored for <see cref="RoiKind.Pixels"/>; 0 (or less) means "engine default", per
/// <see cref="WireLimits.NormalizeOcrScale"/>.
/// </remarks>
/// <param name="Id">Client-chosen, unique within this client's set; how results are looked up on a
/// <see cref="TickData"/>. A plain string still works at every call site — <see cref="RoiId"/>
/// converts implicitly — so declaring a region reads exactly as it did.</param>
public sealed record RoiSubscription(RoiId Id, RoiRect Rect, double Scale, RoiKind Kind)
{
    internal RoiSpec ToProto() => new()
    {
        Id = Id.Value,
        Rect = Rect.ToProto(),
        Scale = Scale,
        Mode = Kind switch
        {
            RoiKind.Text => RoiMode.Text,
            RoiKind.Detailed => RoiMode.Detailed,
            RoiKind.Pixels => RoiMode.Pixels,

            // Not defensive padding: an unmapped kind would otherwise serialise as ROI_MODE_TEXT
            // (proto3 enum default) and the plugin would receive OCR of a colour probe.
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, $"Unknown ROI kind for '{Id}'."),
        },
    };

    /// <summary>
    /// The inverse of <see cref="ToProto"/>, for tests that state a ROI once as a RoiSpec and need
    /// the SDK's view of the same region.
    /// </summary>
    /// <remarks>
    /// Here rather than in the test project so both directions of the mode mapping sit in one
    /// file: split across assemblies, a new <see cref="RoiKind"/> can be added to one switch and
    /// missed in the other, and the half that was missed throws only once a fixture happens to use
    /// the new kind.
    /// </remarks>
    internal static RoiSubscription FromProto(RoiSpec spec) => new(
        spec.Id,
        (spec.Rect ?? new Rect()).ToRoiRect(),
        spec.Scale,
        spec.Mode switch
        {
            RoiMode.Text => RoiKind.Text,
            RoiMode.Detailed => RoiKind.Detailed,
            RoiMode.Pixels => RoiKind.Pixels,
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Mode,
                $"Unknown ROI mode for '{spec.Id}'."),
        });
}
