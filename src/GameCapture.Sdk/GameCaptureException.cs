namespace GameCapture.Sdk;

/// <summary>
/// Base of everything the SDK raises on its own behalf. A plugin catches this rather than
/// <see cref="Grpc.Core.RpcException"/>: gRPC status codes are a transport detail of the current boundary,
/// and a plugin that switches on them is a plugin that has to be rewritten if the boundary ever
/// changes.
/// </summary>
/// <remarks>
/// The SDK does not yet translate everywhere — <see cref="TrackSession.Ticks"/> still surfaces the
/// raw <see cref="Grpc.Core.RpcException"/> so the existing plugin loops keep reconnecting on it. Full
/// translation lands with the plugin host (SOW-3), which is why
/// <see cref="ProtocolNegotiation.Translate"/> exists ahead of every call site that will use it.
/// </remarks>
public class GameCaptureException : Exception
{
    public GameCaptureException(string message) : base(message) { }

    public GameCaptureException(string message, Exception? innerException)
        : base(message, innerException) { }
}
