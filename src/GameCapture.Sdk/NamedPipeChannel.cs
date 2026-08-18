using System.IO.Pipes;
using System.Security.Principal;
using GameCapture.Contracts;
using Grpc.Net.Client;

namespace GameCapture.Sdk;

/// <summary>Grpc channel over a Windows named pipe; gRPC needs a real HTTP/2 duplex stream and
/// SocketsHttpHandler.ConnectCallback is the documented way to supply one.</summary>
/// <remarks>
/// One implementation, referenced by the SDK client and by the engine tests alike. The pattern
/// used to be copy-pasted per call site, and a copy that drifts (a missing PipeOptions.Asynchronous,
/// a different impersonation level) fails as a hang rather than as an error.
/// <para>
/// Internal, because <see cref="Create"/> hands back a <see cref="GrpcChannel"/> and a plugin that
/// can name one is a plugin coupled to the transport — the architecture test in
/// <c>GameCapture.Sdk.Tests</c> catches exactly this. What a plugin actually wanted from here was the
/// pipe name, which is <see cref="EngineDefaults.PipeName"/>.
/// </para>
/// </remarks>
internal static class NamedPipeChannel
{
    /// <summary>Pipe the engine listens on unless its config says otherwise; the same constant as
    /// <see cref="PipeContract.DefaultPipeName"/>, not a second copy.</summary>
    public const string DefaultPipeName = PipeContract.DefaultPipeName;

    /// <summary>
    /// How long one dial of the pipe may take before it is abandoned. Generous — a listening
    /// engine answers immediately — because its job is not to be a timeout policy but to stop an
    /// absent one from being waited on forever; see the remark on <see cref="Create"/>.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Creates a channel; nothing is dialled until the first RPC, so this never fails because the
    /// engine is not running yet. See <see cref="CaptureClient.WaitForEngineAsync"/> for that.
    /// </summary>
    /// <remarks>
    /// Two options here are load-bearing rather than tuning. <see cref="SocketsHttpHandler.ConnectTimeout"/>
    /// is the ONLY bound on the pipe dial: since .NET 6 a connection attempt is detached from the
    /// request that started it, so cancelling or deadlining the RPC unblocks the caller and leaves
    /// ConnectAsync polling an absent pipe — and its default is infinite, i.e. one orphaned poll
    /// loop per attempt against an engine that is not running. ThrowOperationCanceledOnCancellation
    /// makes a cancelled call surface as OperationCanceledException instead of
    /// RpcException(Cancelled), so a plugin host's shutdown path catches its own Ctrl+C rather than
    /// logging it as an engine failure. It applies to deadlines too, which is why
    /// <see cref="CaptureClient.WaitForEngineAsync"/> treats an OCE its own token did not cause as
    /// just another failed attempt.
    /// </remarks>
    public static GrpcChannel Create(string pipeName = DefaultPipeName)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
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
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            ThrowOperationCanceledOnCancellation = true,
        });
    }
}
