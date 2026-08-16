using CaptureContracts.Proto;

namespace TrackerSdk;

/// <summary>
/// What the engine on the other end is, as one SDK-owned value. The point is that no plugin
/// signature names <see cref="StatusResponse"/>: the status message is a generated proto type, and
/// a plugin that reads its fields is a plugin that has to be recompiled when the wire changes.
/// </summary>
/// <remarks>
/// TASK-08 extends this record with <c>ScanInterval</c>, which needs an additive
/// <c>scan_interval_ms</c> field on <c>StatusResponse</c> first — the engine's cadence is currently
/// a constant plugins hardcode. Everything else it specifies is already on the wire and is mapped
/// here.
/// </remarks>
/// <param name="EngineVersion">Build of the engine, as it reports itself.</param>
/// <param name="NegotiatedProtocol">
/// The version the session settled on — the lower of what the SDK announced and what the engine
/// speaks. Zero before a Track session exists, i.e. on the value built from a bare status read.
/// </param>
/// <param name="FrameWidth">Capture width in pixels, or 0 when the engine has not scanned a frame
/// yet. 0 means UNKNOWN, not a 0-pixel screen — fall back to the dimensions on the first tick.</param>
/// <param name="FrameHeight">Capture height in pixels; 0 has the same meaning as on
/// <paramref name="FrameWidth"/>.</param>
/// <param name="ReplayMode">The engine is replaying a corpus rather than capturing a live screen.
/// A plugin that writes anywhere persistent must branch on this.</param>
/// <param name="OcrLanguage">BCP-47 tag of the OCR language the engine loaded.</param>
/// <param name="ConnectedClients">Client names currently subscribed, this plugin included.</param>
public sealed record EngineInfo(
    string EngineVersion,
    uint NegotiatedProtocol,
    int FrameWidth,
    int FrameHeight,
    bool ReplayMode,
    string OcrLanguage,
    IReadOnlyList<string> ConnectedClients)
{
    /// <summary>
    /// Maps a status read, optionally folding in what a live session negotiated. The two sources are
    /// combined rather than kept apart because a plugin asking "what am I talking to" wants one
    /// answer: the status RPC knows the engine's configuration, and only the handshake knows the
    /// protocol version the session actually settled on.
    /// </summary>
    internal static EngineInfo From(StatusResponse status, TrackSession? session = null) => new(
        // The handshake's engine_version wins when there is one: it was read from the same process
        // that is now serving the stream, whereas the status could have been answered by an engine
        // that has since restarted under a new build.
        EngineVersion: session is not null && session.EngineVersion.Length > 0
            ? session.EngineVersion
            : status.EngineVersion,
        NegotiatedProtocol: session?.NegotiatedProtocol ?? 0,
        FrameWidth: (int)status.FrameWidth,
        FrameHeight: (int)status.FrameHeight,
        ReplayMode: status.ReplayMode,
        OcrLanguage: status.OcrLanguage,
        ConnectedClients: status.ConnectedClients.ToArray());
}
