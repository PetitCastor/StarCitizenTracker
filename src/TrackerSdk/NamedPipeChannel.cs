using System.IO.Pipes;
using System.Security.Principal;
using Grpc.Net.Client;

namespace TrackerSdk;

/// <summary>Grpc channel over a Windows named pipe; gRPC needs a real HTTP/2 duplex stream and
/// SocketsHttpHandler.ConnectCallback is the documented way to supply one.</summary>
/// <remarks>
/// One implementation, referenced by the SDK client and by the engine tests alike. The pattern
/// used to be copy-pasted per call site, and a copy that drifts (a missing PipeOptions.Asynchronous,
/// a different impersonation level) fails as a hang rather than as an error.
/// </remarks>
public static class NamedPipeChannel
{
    /// <summary>Pipe the engine listens on unless its config says otherwise.</summary>
    public const string DefaultPipeName = "StarCitizenTracker.CaptureEngine";

    /// <summary>
    /// Creates a channel; nothing is dialled until the first RPC, so this never fails because the
    /// engine is not running yet. See <see cref="CaptureClient.WaitForEngineAsync"/> for that.
    /// </summary>
    public static GrpcChannel Create(string pipeName = DefaultPipeName)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.WriteThrough | PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Anonymous);
                try { await pipe.ConnectAsync(ct); return pipe; }
                catch { await pipe.DisposeAsync(); throw; }
            },
        };

        // The address is a formality: the handler above decides what is actually connected to.
        // Plain http because a pipe carries no TLS to negotiate HTTP/2 with — the engine's Kestrel
        // endpoint forces Http2 for the same reason.
        return GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = handler });
    }
}
