using GameCapture.Contracts.Proto;
using GameCapture.Engine.Grpc;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using GameCapture.Sdk;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// End-to-end over the real transport: Kestrel on a named pipe, a gRPC channel dialling it,
/// and the generated client. The pieces that break in a split are the plumbing ones — pipe
/// naming, HTTP/2 without TLS, codegen wiring — and none of them show up in a unit test that
/// calls the service class directly.
/// </summary>
public class GrpcHostTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Generous: the round-trip test OCRs the whole fixture corpus over the pipe.</summary>
    private static readonly TimeSpan TrackTimeout = TimeSpan.FromMinutes(2);

    // Unique per run: a leftover pipe from a crashed run would otherwise be answered by the
    // wrong process and the test would assert against a stranger.
    private static string NewPipeName() => $"sc-test-{Guid.NewGuid():N}";

    [Fact]
    public async Task GetStatus_OverNamedPipe_ReturnsEngineStatus()
    {
        var pipeName = NewPipeName();
        var status = new EngineStatus("en-US", replayMode: false);
        var registry = new SubscriptionRegistry(status);
        var config = new EngineConfig();

        // The scan loop is built but never run: this test is about the transport, and a stopped
        // engine is exactly the state in which "no frames yet" must still answer correctly.
        using var sink = new ConsoleSink();
        using var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var ocr = new OcrPipeline();
        using var scanLoop = new ScanLoop(source, ocr, registry, status, sink, config, verbose: false);

        var app = GrpcHost.BuildGrpcHost(pipeName, status, registry, scanLoop, ocr, config);
        await app.StartAsync();

        try
        {
            // If the server ever fails to bind the pipe (the regression this test exists to
            // catch), the connect/RPC must fail fast instead of hanging until the runner
            // timeout swallows the red result.
            using var cts = new CancellationTokenSource(TestTimeout);
            using var channel = NamedPipeChannel.Create(pipeName);
            var client = new CaptureEngineService.CaptureEngineServiceClient(channel);

            var response = await client.GetStatusAsync(new StatusRequest(),
                deadline: DateTime.UtcNow.Add(TestTimeout), cancellationToken: cts.Token);

            Assert.NotEmpty(response.EngineVersion);
            Assert.Equal("en-US", response.OcrLanguage);
            Assert.False(response.ReplayMode);

            // The loop never ran, so the engine has seen no frames.
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

    /// <summary>
    /// The whole engine as a plugin will meet it: subscribe over Track, receive every tick of a
    /// replay corpus, then use the two unary calls against the frame the loop retained.
    /// Needs a real Windows OCR language pack.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Track_OverNamedPipe_StreamsCorpusThenServesUnaryReads()
    {
        using var cts = new CancellationTokenSource(TrackTimeout);
        using var sink = new ConsoleSink();

        var outputDir = Path.Combine(Path.GetTempPath(), $"sc-engine-dump-{Guid.NewGuid():N}");
        var config = new EngineConfig { OutputDir = outputDir };
        var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var pipeName = NewPipeName();

        await using var engine = EngineHost.Create(pipeName, config, new OcrPipeline(), source, sink, verbose: false);
        await engine.StartAsync(cts.Token);

        // Started before anyone connects: the loop must hold the corpus until a client has
        // actually subscribed, or the frames are consumed into the void.
        var scan = engine.RunScanAsync(cts.Token);

        try
        {
            using var channel = NamedPipeChannel.Create(pipeName);
            var client = new CaptureEngineService.CaptureEngineServiceClient(channel);

            using var call = client.Track(cancellationToken: cts.Token);
            await call.RequestStream.WriteAsync(new TrackRequest { Hello = new Hello { ClientName = "smoke" } });
            await call.RequestStream.WriteAsync(new TrackRequest
            {
                Rois = new RoiSetUpdate
                {
                    Rois = { EngineTestFixtures.PanelStateRoi(), EngineTestFixtures.ToggleStripRoi() },
                },
            });

            // Nothing more to send: half-closing is how a subscribe-once plugin says so.
            await call.RequestStream.CompleteAsync();

            // Raw generated client, so the oneof arms arrive unfiltered: the handshake ack comes
            // first and every tick after it. Skipping the non-tick arms is what TrackSession.Ticks
            // does for plugins; doing it here keeps this test about the corpus, not the handshake
            // (ProtocolHandshakeTests owns that).
            var ticks = new List<TickResult>();
            await foreach (var response in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                if (response.MsgCase == TrackResponse.MsgOneofCase.Tick)
                    ticks.Add(response.Tick);
            }

            await scan;

            Assert.NotEmpty(ticks);
            Assert.Equal(source.FrameCount, ticks.Count);

            for (var i = 0; i < ticks.Count; i++)
            {
                var tick = ticks[i];
                Assert.Equal((ulong)(i + 1), tick.FrameSeq);
                Assert.Equal(["panel", "toggle"], tick.Results.Select(r => r.RoiId));

                var text = tick.Results[0];
                Assert.False(text.Error, text.ErrorMessage);
                Assert.True(text.EffectiveScale > 0);

                var pixels = tick.Results[1];
                Assert.False(pixels.Error, pixels.ErrorMessage);
                Assert.True(pixels.PixelsStride >= pixels.PixelsWidth * 4);
                Assert.True(pixels.PixelsBgra.Length >= pixels.PixelsWidth * pixels.PixelsHeight * 4);
            }

            // The loop retains its last frame, so the calibration RPCs still answer after the
            // corpus is finished.
            var read = await client.ReadRoiAsync(
                new ReadRoiRequest { Roi = EngineTestFixtures.PanelStateRoi("probe") },
                cancellationToken: cts.Token);

            Assert.False(read.NoFrame);
            Assert.Equal("probe", read.Result.RoiId);
            Assert.False(read.Result.Error, read.Result.ErrorMessage);
            Assert.True(read.FrameWidth > 0 && read.FrameHeight > 0);

            var dump = await client.DumpFrameAsync(
                new DumpFrameRequest { FullFrame = true, Prefix = "smoke" },
                cancellationToken: cts.Token);

            Assert.False(dump.NoFrame);
            Assert.True(File.Exists(dump.Path));
            Assert.Equal(outputDir, Path.GetDirectoryName(dump.Path));
            Assert.StartsWith("smoke_", Path.GetFileName(dump.Path));

            // The crop path used to reimplement RoiScaler.ToFrame directly and skip the off-frame
            // guard ReadOneAsync enforces, so a bad crop rect silently saved a meaningless 1-pixel
            // sliver instead of failing — exactly the "wrong but plausible" outcome the guard exists
            // to prevent. It must reject the same way DumpFrame(full_frame) never has to.
            var ex = await Assert.ThrowsAsync<RpcException>(() => client.DumpFrameAsync(
                new DumpFrameRequest { FullFrame = false, Roi = EngineTestFixtures.OffFrameRoi().Rect, Prefix = "bad-crop" },
                cancellationToken: cts.Token).ResponseAsync);
            Assert.Equal(StatusCode.Unknown, ex.StatusCode);
        }
        finally
        {
            cts.Cancel();
            try { await scan; } catch (OperationCanceledException) { }
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }
}
