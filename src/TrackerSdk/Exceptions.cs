using CaptureContracts;
using Grpc.Core;

namespace TrackerSdk;

/// <summary>
/// Base of everything the SDK raises on its own behalf. A plugin catches this rather than
/// <see cref="RpcException"/>: gRPC status codes are a transport detail of the current boundary,
/// and a plugin that switches on them is a plugin that has to be rewritten if the boundary ever
/// changes.
/// </summary>
/// <remarks>
/// The SDK does not yet translate everywhere — <see cref="TrackSession.Ticks"/> still surfaces the
/// raw <see cref="RpcException"/> so the existing plugin loops keep reconnecting on it. Full
/// translation lands with the plugin host (SOW-3), which is why
/// <see cref="ProtocolNegotiation.Translate"/> exists ahead of every call site that will use it.
/// </remarks>
public class TrackerSdkException : Exception
{
    public TrackerSdkException(string message) : base(message) { }

    public TrackerSdkException(string message, Exception? innerException)
        : base(message, innerException) { }
}

/// <summary>
/// The engine could not be reached, or stopped answering: no pipe on the other end, a dial that
/// ran out its deadline, a handshake nobody replied to. Retryable — this is the exception a host's
/// reconnect loop is for.
/// </summary>
public sealed class EngineUnavailableException : TrackerSdkException
{
    public EngineUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

/// <summary>
/// The engine and this SDK do not speak a common protocol version. Not retryable: the versions are
/// fixed for the life of both processes, so a host that loops on this loops forever. The supported
/// range is carried as data because the useful thing to tell the user is which side to upgrade.
/// </summary>
public sealed class ProtocolMismatchException : TrackerSdkException
{
    public ProtocolMismatchException(uint engineMin, uint engineMax, uint sdkVersion,
        Exception? innerException = null)
        : base(Describe(engineMin, engineMax, sdkVersion), innerException)
    {
        EngineMin = engineMin;
        EngineMax = engineMax;
        SdkVersion = sdkVersion;
    }

    /// <summary>Oldest protocol version the engine still speaks.</summary>
    public uint EngineMin { get; }

    /// <summary>Newest protocol version the engine speaks. Zero means the engine predates
    /// negotiation entirely and reported no range at all.</summary>
    public uint EngineMax { get; }

    /// <summary>What this SDK announced — <see cref="ProtocolVersion.Current"/> in production.</summary>
    public uint SdkVersion { get; }

    private static string Describe(uint engineMin, uint engineMax, uint sdkVersion)
        => engineMax == 0
            ? $"The engine predates protocol negotiation (it reports no supported range); this SDK speaks protocol {sdkVersion}."
            : $"The engine speaks protocol {engineMin}-{engineMax}; this SDK speaks {sdkVersion}.";
}

/// <summary>
/// The session died in a way that is neither "no engine" nor "wrong version": the stream faulted,
/// or the engine broke its own handshake contract. Distinct from
/// <see cref="EngineUnavailableException"/> because a reconnect is a guess here, not a remedy.
/// </summary>
public sealed class SessionFaultedException : TrackerSdkException
{
    public SessionFaultedException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

/// <summary>
/// The two halves of version negotiation the SDK owns: checking the range the engine advertises,
/// and turning a gRPC failure into the SDK's own vocabulary.
/// </summary>
internal static class ProtocolNegotiation
{
    /// <summary>Trailers the engine attaches to a rejected Hello; see CaptureGrpcService.Track.</summary>
    internal const string MinTrailer = "sctracker-protocol-min";
    internal const string MaxTrailer = "sctracker-protocol-max";

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
    internal static TrackerSdkException Translate(RpcException ex, uint sdkVersion) => ex.StatusCode switch
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
