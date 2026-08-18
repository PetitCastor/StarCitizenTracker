using System.Diagnostics.CodeAnalysis;
using GameCapture.Contracts;
using GameCapture.Contracts.Proto;

namespace GameCapture.Sdk;

/// <summary>How one subscribed region fared on one tick.</summary>
/// <remarks>
/// Three states rather than a bool because the two failure modes need different fixes: a
/// <see cref="Failed"/> region was read and the read did not work (the region fell outside the
/// frame, the pixel payload blew the wire budget), while a <see cref="NotSubscribed"/> region was
/// never in the tick at all — a typo'd id, or a lookup against a subscription this plugin does not
/// hold. Reporting both as "no reading" is how a mistyped constant survives a whole session
/// silently.
/// </remarks>
public enum RoiStatus
{
    /// <summary>The engine read the region and the payload is usable.</summary>
    Ok,

    /// <summary>The engine flagged the region as failed on this tick. Nothing on it is readable.</summary>
    Failed,

    /// <summary>The tick carried no result under this id at all.</summary>
    NotSubscribed,
}

/// <summary>One engine scan tick; every reading comes from the same frame.</summary>
/// <remarks>
/// Per-tick atomicity is the reason this type exists at all: a plugin that needs a panel's state
/// and a toggle's colour to make one decision gets both from one object, so it cannot accidentally
/// combine a state read at t with a colour read at t+1. Lookups are by the ROI id the plugin
/// subscribed.
/// <para>
/// The Try-shaped accessors are the whole point of the surface: a region that failed and a region
/// that was genuinely empty both answer <c>""</c>, and the difference decides whether a state
/// machine should advance — the refinery's panel header reading empty means "the panel closed",
/// which files an order that never completed. A <c>false</c> return cannot be mistaken for a
/// reading the way an empty string can.
/// </para>
/// </remarks>
public sealed class TickData
{
    private readonly Dictionary<RoiId, RoiResult> _byId;

    private TickData(TickResult proto, Dictionary<RoiId, RoiResult> byId, IReadOnlyList<RoiId> errored)
    {
        _byId = byId;
        ErroredRois = errored;
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(proto.TimestampMs).LocalDateTime;
        FrameSeq = proto.FrameSeq;
        FrameWidth = (int)proto.FrameWidth;
        FrameHeight = (int)proto.FrameHeight;
        Manual = proto.Manual;
    }

    /// <summary>When the engine scanned the frame, in local time (the wire carries UTC millis).</summary>
    public DateTime Timestamp { get; }

    /// <summary>Monotonic per scanned frame; how a plugin tells a fresh decision from a repeat.</summary>
    public ulong FrameSeq { get; }

    public int FrameWidth { get; }
    public int FrameHeight { get; }

    /// <summary>The hotkey fired since the previous tick. Same value for every client on this tick.</summary>
    public bool Manual { get; }

    /// <summary>Regions the engine flagged as failed on this tick, in wire order, one entry per
    /// distinct id.</summary>
    /// <remarks>
    /// Every failed result in the tick, not only the ones a given plugin subscribed: the engine
    /// echoes ids back unvalidated, so a host filtering on behalf of one plugin still intersects
    /// this with that plugin's own set.
    /// </remarks>
    public IReadOnlyList<RoiId> ErroredRois { get; }

    /// <summary>At least one region failed this tick.</summary>
    public bool HasErrors => ErroredRois.Count > 0;

    /// <summary>How the region fared, or <see cref="RoiStatus.NotSubscribed"/> if it is not in this tick.</summary>
    public RoiStatus Status(RoiId roiId)
    {
        if (!_byId.TryGetValue(roiId, out var r))
            return RoiStatus.NotSubscribed;

        return r.Error ? RoiStatus.Failed : RoiStatus.Ok;
    }

    /// <summary>
    /// Plain text of a TEXT/DETAILED region. False — with <paramref name="text"/> empty — when the
    /// region failed, is not in this tick, or answers a PIXELS subscription; true with an empty
    /// string when the panel really was blank.
    /// </summary>
    /// <remarks>
    /// A PIXELS result is refused rather than answered with its (always empty) text field for the
    /// reason <see cref="ProtoMapping"/> gives: the unfilled fields of the other mode are proto3
    /// defaults, and an empty string is indistinguishable from a successfully read empty panel.
    /// </remarks>
    public bool TryGetText(RoiId roiId, out string text)
    {
        text = string.Empty;
        if (!_byId.TryGetValue(roiId, out var r) || r.Error || r.Kind == RoiResultKind.Pixels)
            return false;

        text = r.Text;
        return true;
    }

    /// <summary>
    /// Detailed OCR of a DETAILED region — word geometry included — or false when the region
    /// failed, is not in this tick, or carries a payload the boundary checks reject.
    /// </summary>
    /// <remarks>
    /// A TEXT region answers here too, with an empty <see cref="OcrRegionResult.Lines"/>: the wire
    /// shape is the same and only the word geometry is absent.
    /// </remarks>
    public bool TryGetOcr(RoiId roiId, [NotNullWhen(true)] out OcrRegionResult? ocr)
    {
        ocr = null;
        return _byId.TryGetValue(roiId, out var r) && r.TryToOcrRegionResult(out ocr, out _);
    }

    /// <summary>
    /// Pixel sampler of a PIXELS region, or false when it failed, is not in this tick, or carries a
    /// buffer that does not match its declared geometry.
    /// </summary>
    /// <remarks>Each call re-materialises the buffer; plugins read a region once per tick.</remarks>
    public bool TryGetPixels(RoiId roiId, [NotNullWhen(true)] out PixelPatchSampler? pixels)
    {
        pixels = null;
        return _byId.TryGetValue(roiId, out var r) && r.TryToPixelSampler(out pixels, out _);
    }

    /// <summary>
    /// What the engine said about a failed region, or null when it did not fail (including when it
    /// is not in this tick — <see cref="Status"/> is what tells those apart).
    /// </summary>
    /// <remarks>
    /// Reports what the engine flagged. A payload the engine considered fine but that fails the
    /// boundary checks in <see cref="ProtoMapping"/> is not an engine error and surfaces instead as
    /// a false from <see cref="TryGetOcr"/> / <see cref="TryGetPixels"/>.
    /// </remarks>
    public string? ErrorMessage(RoiId roiId)
    {
        if (!_byId.TryGetValue(roiId, out var r) || !r.Error)
            return null;

        return r.ErrorMessage.Length > 0 ? r.ErrorMessage : "the engine reported a ROI failure.";
    }

    /// <summary>Plain text of a TEXT/DETAILED ROI; empty string if missing or errored.</summary>
    [Obsolete("Ambiguous on failure: use TryGetText/Status.")]
    public string Text(RoiId roiId)
        => TryGetText(roiId, out var text) ? text : string.Empty;

    /// <summary>Detailed OCR of a DETAILED ROI, or null if missing/errored.</summary>
    [Obsolete("Use TryGetOcr/Status.")]
    public OcrRegionResult? Ocr(RoiId roiId)
        => TryGetOcr(roiId, out var ocr) ? ocr : null;

    /// <summary>Pixel sampler of a PIXELS ROI, or null if missing/errored.</summary>
    [Obsolete("Use TryGetPixels/Status.")]
    public PixelPatchSampler? Pixels(RoiId roiId)
        => TryGetPixels(roiId, out var pixels) ? pixels : null;

    /// <summary>Error message for a ROI, or null.</summary>
    [Obsolete("Renamed: use ErrorMessage, or Status for the failed/absent distinction.")]
    public string? Error(RoiId roiId) => ErrorMessage(roiId);

    internal static TickData From(TickResult proto)
    {
        // Indexer, not Add: ids are the client's own and the engine echoes them back unvalidated,
        // so a plugin that subscribed the same id twice would otherwise crash the whole tick here
        // instead of merely getting one of its two readings.
        var byId = new Dictionary<RoiId, RoiResult>(proto.Results.Count);
        foreach (var result in proto.Results)
            byId[result.RoiId] = result;

        // Walked in wire order, but answered from the deduplicated map: the same id sent twice must
        // not be reported as two failures, and the entry that is listed has to be the one a lookup
        // would actually return (the indexer above kept the last). Enumerating the dictionary
        // instead would give both of those for free but leave the order an implementation detail of
        // Dictionary, and this list is public API a plugin may print. Built once here because the
        // host consults it on every tick.
        List<RoiId>? errored = null;
        HashSet<RoiId>? seen = null;
        foreach (var result in proto.Results)
        {
            RoiId id = result.RoiId;
            if (!byId[id].Error || !(seen ??= []).Add(id))
                continue;

            (errored ??= []).Add(id);
        }

        return new TickData(proto, byId, errored ?? (IReadOnlyList<RoiId>)[]);
    }
}
