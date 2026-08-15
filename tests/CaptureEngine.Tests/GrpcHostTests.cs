using System.IO.Pipes;
using System.Security.Principal;
using CaptureContracts.Proto;
using CaptureEngine;
using CaptureEngine.Grpc;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace CaptureEngine.Tests;

/// <summary>
/// End-to-end over the real transport: Kestrel on a named pipe, a gRPC channel dialling it,
/// and the generated client. The pieces that break in a split are the plumbing ones — pipe
/// naming, HTTP/2 without TLS, codegen wiring — and none of them show up in a unit test that
/// calls the service class directly.
/// </summary>
public class GrpcHostTests
{
    /// <summary>
    /// Same ConnectCallback shape the SDK will use in TASK-4: gRPC has no pipe transport of
    /// its own, so the channel dials "localhost" over an HTTP handler whose connections are
    /// actually named-pipe streams.
    /// </summary>
    private static GrpcChannel ConnectTo(string pipeName)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.WriteThrough | PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Anonymous);
                try
                {
                    await pipe.ConnectAsync(ct);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
            },
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
    }

    [Fact]
    public async Task GetStatus_OverNamedPipe_ReturnsEngineStatus()
    {
        // Unique per run: a leftover pipe from a crashed run would otherwise be answered by
        // the wrong process and the test would assert against a stranger.
        var pipeName = $"sc-test-{Guid.NewGuid():N}";
        var status = new EngineStatus("en-US", replayMode: false);

        var app = GrpcHost.BuildGrpcHost(pipeName, status);
        await app.StartAsync();

        try
        {
            using var channel = ConnectTo(pipeName);
            var client = new CaptureEngineService.CaptureEngineServiceClient(channel);

            var response = await client.GetStatusAsync(new StatusRequest());

            Assert.NotEmpty(response.EngineVersion);
            Assert.Equal("en-US", response.OcrLanguage);
            Assert.False(response.ReplayMode);

            // No scan loop in TASK-2, so the engine has seen no frames yet.
            Assert.Equal(0u, response.FrameWidth);
            Assert.Equal(0u, response.FrameHeight);
            Assert.Equal(0ul, response.FrameSeq);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
