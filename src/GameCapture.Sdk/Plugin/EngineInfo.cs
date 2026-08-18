using GameCapture.Contracts.Proto;

namespace GameCapture.Sdk;

/// <summary>
/// What the engine on the other end is, as one SDK-owned value. The point is that no plugin
/// signature names <see cref="StatusResponse"/>: the status message is a generated proto type, and
/// a plugin that reads its fields is a plugin that has to be recompiled when the wire changes.
/// </summary>
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
/// <param name="ScanInterval">
/// How often this engine scans. A plugin that debounces in ticks — "the panel has to stay gone for
/// three of them" — needs this to state the same rule in seconds, and the number is configurable
/// engine-side: before it was reported, plugins carried comments asserting 500 ms that no longer
/// held the moment anyone edited the engine's config. Falls back to
/// <see cref="EngineDefaults.DefaultScanInterval"/> against an engine too old to report it.
/// </param>
public sealed record EngineInfo(
    string EngineVersion,
    uint NegotiatedProtocol,
    int FrameWidth,
    int FrameHeight,
    bool ReplayMode,
    string OcrLanguage,
    IReadOnlyList<string> ConnectedClients,
    TimeSpan ScanInterval)
{
    /// <summary>Maps a status read. Nothing session-scoped is known yet; see <see cref="WithSession"/>.</summary>
    internal static EngineInfo From(StatusResponse status) => new(
        EngineVersion: status.EngineVersion,
        NegotiatedProtocol: 0,
        FrameWidth: (int)status.FrameWidth,
        FrameHeight: (int)status.FrameHeight,
        ReplayMode: status.ReplayMode,
        OcrLanguage: status.OcrLanguage,
        ConnectedClients: status.ConnectedClients.ToArray(),
        ScanInterval: status.ScanIntervalMs > 0
            ? TimeSpan.FromMilliseconds(status.ScanIntervalMs)
            : EngineDefaults.DefaultScanInterval);

    /// <summary>
    /// Folds in what a live session negotiated. The two sources are combined rather than kept apart
    /// because a plugin asking "what am I talking to" wants one answer: the status RPC knows the
    /// engine's configuration, and only the handshake knows the protocol version the session
    /// actually settled on.
    /// </summary>
    /// <remarks>
    /// The handshake's engine_version wins when there is one: it was read from the same process that
    /// is now serving the stream, whereas the status could have been answered by an engine that has
    /// since restarted under a new build.
    /// </remarks>
    internal EngineInfo WithSession(TrackSession session) => this with
    {
        EngineVersion = session.EngineVersion.Length > 0 ? session.EngineVersion : EngineVersion,
        NegotiatedProtocol = session.NegotiatedProtocol,
    };
}
