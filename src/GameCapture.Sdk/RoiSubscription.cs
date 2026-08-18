using GameCapture.Contracts;
using GameCapture.Contracts.Proto;

namespace GameCapture.Sdk;

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
/// <param name="Rect">The region, in reference space. The engine maps it to the captured frame;
/// never pre-scale it to a screen resolution.</param>
/// <param name="Scale">OCR upscale factor, per the remarks above: small UI text usually wants 2-4,
/// 0 takes the engine default, and the engine clamps whatever it cannot fit.</param>
/// <param name="Kind">What the engine should return for this region — OCR text, OCR with word
/// geometry, or raw pixels.</param>
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
