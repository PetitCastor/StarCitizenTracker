using CaptureContracts;
using CaptureContracts.Proto;
using Common;
using Grpc.Core;
using TrackerSdk;
using Xunit;

namespace CaptureEngine.Tests;

/// <summary>Protocol negotiation must travel over the production named-pipe transport.</summary>
public class ProtocolHandshakeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Hello_v1_returns_HelloAck_before_any_tick()
    {
        await using var engine = await StartEngineAsync(replayMode: true);
        using var cts = new CancellationTokenSource(TestTimeout);
        using var channel = NamedPipeChannel.Create(engine.PipeName);
        var client = new CaptureEngineService.CaptureEngineServiceClient(channel);
        using var call = client.Track(cancellationToken: cts.Token);

        await call.RequestStream.WriteAsync(new TrackRequest
        {
            Hello = new Hello { ClientName = "handshake-v1", ProtocolVersion = 1 },
        });

        Assert.True(await call.ResponseStream.MoveNext(cts.Token));
        var response = call.ResponseStream.Current;
        Assert.Equal(TrackResponse.MsgOneofCase.HelloAck, response.MsgCase);
        Assert.Equal(1u, response.HelloAck.NegotiatedProtocolVersion);
        Assert.NotEmpty(response.HelloAck.EngineVersion);
        Assert.True(response.HelloAck.ReplayMode);
    }

    [Fact]
    public async Task Hello_v999_faults_with_supported_protocol_range()
    {
        await using var engine = await StartEngineAsync(replayMode: false);
        using var cts = new CancellationTokenSource(TestTimeout);
        using var channel = NamedPipeChannel.Create(engine.PipeName);
        var client = new CaptureEngineService.CaptureEngineServiceClient(channel);
        using var call = client.Track(cancellationToken: cts.Token);

        await call.RequestStream.WriteAsync(new TrackRequest
        {
            Hello = new Hello { ClientName = "unsupported", ProtocolVersion = 999 },
        });

        var exception = await Assert.ThrowsAsync<RpcException>(
            () => call.ResponseStream.MoveNext(cts.Token));
        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
        Assert.Equal(ProtocolVersion.Min.ToString(), exception.Trailers.GetValue("sctracker-protocol-min"));
        Assert.Equal(ProtocolVersion.Current.ToString(), exception.Trailers.GetValue("sctracker-protocol-max"));
    }

    [Fact]
    public async Task Hello_v0_is_accepted_as_legacy_protocol_v1()
    {
        await using var engine = await StartEngineAsync(replayMode: false);
        using var cts = new CancellationTokenSource(TestTimeout);
        using var channel = NamedPipeChannel.Create(engine.PipeName);
        var client = new CaptureEngineService.CaptureEngineServiceClient(channel);
        using var call = client.Track(cancellationToken: cts.Token);

        await call.RequestStream.WriteAsync(new TrackRequest
        {
            Hello = new Hello { ClientName = "legacy", ProtocolVersion = 0 },
        });

        Assert.True(await call.ResponseStream.MoveNext(cts.Token));
        Assert.Equal(TrackResponse.MsgOneofCase.HelloAck, call.ResponseStream.Current.MsgCase);
        Assert.Equal(1u, call.ResponseStream.Current.HelloAck.NegotiatedProtocolVersion);
    }

    [Fact]
    public async Task GetStatus_reports_current_protocol_range()
    {
        await using var engine = await StartEngineAsync(replayMode: false);
        using var cts = new CancellationTokenSource(TestTimeout);
        using var channel = NamedPipeChannel.Create(engine.PipeName);
        var client = new CaptureEngineService.CaptureEngineServiceClient(channel);

        var status = await client.GetStatusAsync(new StatusRequest(), cancellationToken: cts.Token);

        Assert.Equal(1u, status.MinSupportedProtocol);
        Assert.Equal(1u, status.MaxSupportedProtocol);
    }

    private static async Task<StartedEngine> StartEngineAsync(bool replayMode)
    {
        var pipeName = $"sc-handshake-{Guid.NewGuid():N}";
        IFrameSource source = replayMode
            ? new ReplayFrameSource(EngineTestFixtures.ReplayDir)
            : new NoFramesSource();
        var sink = new ConsoleSink();
        var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(), source, sink, verbose: false);
        await engine.StartAsync();
        return new StartedEngine(pipeName, engine, sink);
    }

    private sealed class StartedEngine(string pipeName, EngineHost engine, ConsoleSink sink) : IAsyncDisposable
    {
        public string PipeName { get; } = pipeName;

        public async ValueTask DisposeAsync()
        {
            await engine.DisposeAsync();
            sink.Dispose();
        }
    }

    private sealed class NoFramesSource : IFrameSource
    {
        public bool IsReplay => false;

        public Task<Windows.Graphics.Imaging.SoftwareBitmap?> NextFrameAsync(CancellationToken ct)
            => Task.FromResult<Windows.Graphics.Imaging.SoftwareBitmap?>(null);

        public void Dispose() { }
    }
}
