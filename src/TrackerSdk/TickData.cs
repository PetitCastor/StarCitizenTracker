using CaptureContracts;
using CaptureContracts.Proto;

namespace TrackerSdk;

/// <summary>One engine scan tick; every reading comes from the same frame.</summary>
/// <remarks>
/// Per-tick atomicity is the reason this type exists at all: a plugin that needs a panel's state
/// and a toggle's colour to make one decision gets both from one object, so it cannot accidentally
/// combine a state read at t with a colour read at t+1. Lookups are by the ROI id the plugin
/// subscribed, and every accessor is total — a ROI the engine failed to read answers "nothing"
/// rather than throwing into the middle of a parser.
/// </remarks>
public sealed class TickData
{
    private readonly Dictionary<string, RoiResult> _byId;

    private TickData(TickResult proto, Dictionary<string, RoiResult> byId)
    {
        _byId = byId;
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

    /// <summary>Plain text of a TEXT/DETAILED ROI; empty string if missing or errored.</summary>
    /// <remarks>
    /// An errored ROI deliberately reads as empty rather than throwing, but "empty" is then
    /// ambiguous with a successfully read empty panel — a tracker whose state machine cares about
    /// the difference must check <see cref="Error"/> first.
    /// </remarks>
    public string Text(string roiId)
        => _byId.TryGetValue(roiId, out var r) && !r.Error ? r.Text : string.Empty;

    /// <summary>Detailed OCR of a DETAILED ROI, or null if missing/errored.</summary>
    /// <remarks>
    /// A TEXT ROI answers here too, with an empty <see cref="OcrRegionResult.Lines"/>: the wire
    /// shape is the same and only the word geometry is absent.
    /// </remarks>
    public OcrRegionResult? Ocr(string roiId)
        => _byId.TryGetValue(roiId, out var r) && r.TryToOcrRegionResult(out var ocr, out _) ? ocr : null;

    /// <summary>Pixel sampler of a PIXELS ROI, or null if missing/errored.</summary>
    /// <remarks>Each call re-materialises the buffer; plugins read a ROI once per tick.</remarks>
    public PixelPatchSampler? Pixels(string roiId)
        => _byId.TryGetValue(roiId, out var r) && r.TryToPixelSampler(out var pixels, out _) ? pixels : null;

    /// <summary>Error message for a ROI, or null.</summary>
    /// <remarks>
    /// Reports what the engine flagged. A payload the engine considered fine but that fails the
    /// boundary checks in <see cref="ProtoMapping"/> is not an engine error and surfaces instead as
    /// a null from <see cref="Ocr"/> / <see cref="Pixels"/>.
    /// </remarks>
    public string? Error(string roiId)
    {
        if (!_byId.TryGetValue(roiId, out var r) || !r.Error)
            return null;

        return r.ErrorMessage.Length > 0 ? r.ErrorMessage : "the engine reported a ROI failure.";
    }

    internal static TickData From(TickResult proto)
    {
        // Indexer, not Add: ids are the client's own and the engine echoes them back unvalidated,
        // so a plugin that subscribed the same id twice would otherwise crash the whole tick here
        // instead of merely getting one of its two readings.
        var byId = new Dictionary<string, RoiResult>(proto.Results.Count, StringComparer.Ordinal);
        foreach (var result in proto.Results)
            byId[result.RoiId] = result;

        return new TickData(proto, byId);
    }
}
