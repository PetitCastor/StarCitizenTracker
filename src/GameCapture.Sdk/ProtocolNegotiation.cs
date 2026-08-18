using Grpc.Core;

namespace GameCapture.Sdk;

/// <summary>
/// The two halves of version negotiation the SDK owns: checking the range the engine advertises,
/// and turning a gRPC failure into the SDK's own vocabulary.
/// </summary>
internal static class ProtocolNegotiation
{
    /// <summary>Trailers the engine attaches to a rejected Hello; see CaptureGrpcService.Track.</summary>
    internal const string MinTrailer = "gamecapture-protocol-min";
    internal const string MaxTrailer = "gamecapture-protocol-max";

    /// <summary>
    /// Fail-fast check against the range <c>GetStatus</c> advertises, run before a stream is opened
    /// at all. The engine would reject the Hello anyway, but only after the client has committed to
    /// a session — and a rejection that arrives as a faulted stream is much harder to report than
    /// one raised out of the connect call.
    /// </summary>
    /// <remarks>
    /// An engine that predates TASK-04 leaves both fields at their proto3 default of 0, so it fails
    /// this check like any other incompatible range. That is deliberate rather than incidental: it
    /// cannot answer a Hello with an ack, and <see cref="CaptureClient.TrackAsync"/> now waits for
    /// one, so letting it through here would only trade a clear message for a handshake timeout.
    /// </remarks>
    internal static void EnsureSupported(uint engineMin, uint engineMax, uint sdkVersion)
    {
        if (sdkVersion < engineMin || sdkVersion > engineMax)
            throw new ProtocolMismatchException(engineMin, engineMax, sdkVersion);
    }

    /// <summary>
    /// True when a failure is the engine refusing the announced protocol version. Keyed on the
    /// trailers and not on the status alone: FAILED_PRECONDITION is a status any future handler
    /// could return for its own reasons, and only the range trailers say the handshake is what was
    /// refused.
    /// </summary>
    /// <remarks>
    /// This is the one gRPC failure the SDK re-types today. Everything else still reaches plugins
    /// as an <see cref="RpcException"/>, because that is what their reconnect loops catch — see the
    /// remark on <see cref="TrackSession.ReceiveHelloAckAsync"/>.
    /// </remarks>
    internal static bool IsProtocolRejection(RpcException ex)
        => ex.StatusCode == StatusCode.FailedPrecondition && TryReadRange(ex, out _, out _);

    /// <summary>
    /// Maps a gRPC failure to the SDK's exception surface. The protocol arm is checked first and by
    /// trailers rather than by status alone, for the reason given on
    /// <see cref="IsProtocolRejection"/>.
    /// </summary>
    /// <remarks>
    /// Only the protocol arm is wired to a transport path today; the rest exists for the plugin
    /// host (SOW-3 / TASK-07), which is the first caller that will own a reconnect policy of its
    /// own. That caller must decide whether the call was cancelled BEFORE calling this: a
    /// cancellation reaches gRPC as <see cref="StatusCode.Cancelled"/> and would fall to the
    /// default arm, reporting an orderly shutdown as a faulted session.
    /// </remarks>
    internal static GameCaptureException Translate(RpcException ex, uint sdkVersion) => ex.StatusCode switch
    {
        StatusCode.FailedPrecondition when TryReadRange(ex, out var min, out var max)
            => new ProtocolMismatchException(min, max, sdkVersion, ex),
        StatusCode.Unavailable or StatusCode.DeadlineExceeded
            => new EngineUnavailableException($"The capture engine is not reachable: {ex.Status.Detail}", ex),
        _ => new SessionFaultedException($"The capture session failed ({ex.StatusCode}): {ex.Status.Detail}", ex),
    };

    /// <summary>
    /// Reads the supported range out of a rejection's trailers. Both must be present and parse, or
    /// this is not the engine's protocol rejection and the caller must not report it as one.
    /// </summary>
    private static bool TryReadRange(RpcException ex, out uint min, out uint max)
    {
        min = 0;
        max = 0;

        // Trailers are absent rather than empty on some failure paths (a call torn down before the
        // server wrote them, for one), so this cannot assume the collection exists.
        var trailers = ex.Trailers;
        if (trailers is null)
            return false;

        return uint.TryParse(trailers.GetValue(MinTrailer), out min)
            && uint.TryParse(trailers.GetValue(MaxTrailer), out max);
    }
}
