using GameCapture.Contracts;
using GameCapture.Contracts.Proto;
using Grpc.Core;
using GameCapture.Sdk;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>Protocol negotiation must travel over the production named-pipe transport.</summary>
public class ProtocolHandshakeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Capacity of a client's outbound channel is 4; overflow it to force an eviction.</summary>
    private const int OverflowFrames = 6;

    [Fact]
    public async Task Hello_v1_is_acknowledged_with_the_negotiated_version()
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
        Assert.Equal(ProtocolVersion.Min.ToString(), exception.Trailers.GetValue("gamecapture-protocol-min"));
        Assert.Equal(ProtocolVersion.Current.ToString(), exception.Trailers.GetValue("gamecapture-protocol-max"));
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

    /// <summary>
    /// The ordering hazard the ack's delivery path exists for, and the one the tests above cannot
    /// see because they never run the scan loop. In a live session the loop pushes a tick to every
    /// registered client — registration happens when the Track call opens, before the client's
    /// Hello has been read — and the outbound channel drops its OLDEST entry on overflow. An ack
    /// routed through that channel would arrive behind those ticks, and once six of them have
    /// piled up against a four-deep channel it would be evicted entirely. Both failures are live
    /// here: the frames are produced, and confirmed produced, before the Hello is sent.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task HelloAck_arrives_first_even_when_live_ticks_precede_the_Hello()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        using var sink = new ConsoleSink();

        var pipeName = $"sc-handshake-{Guid.NewGuid():N}";
        var source = new GatedFrameSource(EngineTestFixtures.ReplayDir, isReplay: false);

        // The live path sleeps a scan interval between frames; keep it at the floor so overflowing
        // the channel costs the test a fraction of a second rather than three.
        var config = new EngineConfig { ScanIntervalMs = 100 };

        await using var engine = EngineHost.Create(pipeName, config, new OcrPipeline(), source, sink, verbose: false);
        await engine.StartAsync(cts.Token);

        var scan = engine.RunScanAsync(scanCts.Token);
        try
        {
            using var channel = NamedPipeChannel.Create(pipeName);
            var grpc = new CaptureEngineService.CaptureEngineServiceClient(channel);

            // Opening the call is what registers the client. Nothing has been sent on it yet.
            using var call = grpc.Track(cancellationToken: cts.Token);

            source.Release(OverflowFrames);

            // Polled rather than slept: the ticks must genuinely be queued and overflowing before
            // the Hello goes out, or the test would pass on timing instead of on ordering.
            await WaitForFrameSeqAsync(grpc, OverflowFrames, cts.Token);

            await call.RequestStream.WriteAsync(new TrackRequest
            {
                Hello = new Hello { ClientName = "late-hello", ProtocolVersion = 1 },
            });

            Assert.True(await call.ResponseStream.MoveNext(cts.Token));
            Assert.Equal(TrackResponse.MsgOneofCase.HelloAck, call.ResponseStream.Current.MsgCase);
            Assert.Equal(1u, call.ResponseStream.Current.HelloAck.NegotiatedProtocolVersion);
            Assert.False(call.ResponseStream.Current.HelloAck.ReplayMode);

            // And the ticks still follow: the ack goes ahead of them, it does not replace them.
            Assert.True(await call.ResponseStream.MoveNext(cts.Token));
            Assert.Equal(TrackResponse.MsgOneofCase.Tick, call.ResponseStream.Current.MsgCase);
        }
        finally
        {
            // Stop the loop before the host disposes the gated source out from under it.
            scanCts.Cancel();
            try { await scan; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Waits until the engine reports having scanned at least <paramref name="minSeq"/> frames.</summary>
    private static async Task WaitForFrameSeqAsync(
        CaptureEngineService.CaptureEngineServiceClient grpc, ulong minSeq, CancellationToken ct)
    {
        while ((await grpc.GetStatusAsync(new StatusRequest(), cancellationToken: ct)).FrameSeq < minSeq)
            await Task.Delay(10, ct);
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
