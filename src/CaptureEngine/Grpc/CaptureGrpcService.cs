using CaptureContracts;
using CaptureContracts.Proto;
using Grpc.Core;

namespace CaptureEngine.Grpc;

/// <summary>
/// The engine's whole public surface. It holds no state of its own: everything it reports comes
/// from <see cref="EngineStatus"/>, the <see cref="SubscriptionRegistry"/> and the
/// <see cref="ScanLoop"/>, so the scan loop and the RPC layer can never disagree about what the
/// engine is doing. In particular no OCR runs here — the unary reads borrow the loop's retained
/// frame under its gate.
/// </summary>
/// <remarks>
/// Public because Grpc.AspNetCore binds service methods through compiled delegates, which cannot
/// reach a non-public type; the constructor stays internal because its dependencies are.
/// <see cref="GrpcHost"/> registers the instance in DI, so gRPC's activator resolves it rather
/// than trying to construct it reflectively.
/// </remarks>
public sealed class CaptureGrpcService : CaptureEngineService.CaptureEngineServiceBase
{
    /// <summary>Fallback dump prefix when a client sends none.</summary>
    private const string DefaultDumpPrefix = "dump";

    private readonly EngineStatus _status;
    private readonly SubscriptionRegistry _registry;
    private readonly ScanLoop _scanLoop;
    private readonly OcrPipeline _ocr;
    private readonly EngineConfig _config;

    internal CaptureGrpcService(
        EngineStatus status,
        SubscriptionRegistry registry,
        ScanLoop scanLoop,
        OcrPipeline ocr,
        EngineConfig config)
    {
        _status = status;
        _registry = registry;
        _scanLoop = scanLoop;
        _ocr = ocr;
        _config = config;
    }

    /// <summary>
    /// One subscription for the life of the connection: the request pump keeps the client's ROI
    /// set current while the response pump drains the ticks the scan loop queued for it. The two
    /// run independently on purpose — a client that stops reading must not be able to block the
    /// thread that is applying its next RoiSetUpdate.
    /// </summary>
    public override async Task Track(
        IAsyncStreamReader<TrackRequest> requestStream,
        IServerStreamWriter<TrackResponse> responseStream,
        ServerCallContext ctx)
    {
        var client = _registry.Register(_status.ReplayMode);

        // The pump outlives its usefulness the moment the response side is done, and a plugin
        // that keeps its request stream open (to send later ROI updates) would otherwise leave it
        // blocked on a read forever — with this call unable to return and the stream unable to
        // close. Its own token lets the response side end it.
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);

        try
        {
            var pump = Task.Run(async () =>
            {
                await foreach (var msg in requestStream.ReadAllAsync(pumpCts.Token))
                {
                    switch (msg.MsgCase)
                    {
                        case TrackRequest.MsgOneofCase.Hello:
                            client.Name = msg.Hello.ClientName;
                            break;
                        case TrackRequest.MsgOneofCase.Rois:
                            client.SetRois(msg.Rois);
                            break;
                    }
                }
            }, pumpCts.Token);

            // Completes when the registry completes the channel: replay finished, or the engine
            // is shutting down. A client disconnecting instead surfaces as a cancellation.
            await foreach (var response in client.Out.Reader.ReadAllAsync(ctx.CancellationToken))
                await responseStream.WriteAsync(response);

            pumpCts.Cancel();
            try
            {
                await pump;
            }
            catch (Exception e) when (IsCancellation(e))
            {
                // Expected: either we just cancelled the pump, or the client hung up first.
            }
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
        {
            // Same for the response side.
        }
        finally
        {
            _registry.Unregister(client);
        }
    }

    /// <summary>
    /// One-shot read against the retained frame — a calibration aid, not a data path: it deliberately
    /// does NOT capture a fresh frame, so what it returns is exactly what the last tick saw.
    /// </summary>
    public override async Task<ReadRoiResponse> ReadRoi(ReadRoiRequest request, ServerCallContext ctx)
    {
        await _scanLoop.FrameGate.WaitAsync(ctx.CancellationToken);
        try
        {
            var bitmap = _scanLoop.RetainedFrame;
            if (bitmap is null)
                return new ReadRoiResponse { NoFrame = true };

            return new ReadRoiResponse
            {
                NoFrame = false,
                Result = await _scanLoop.ReadOneAsync(bitmap, request.Roi ?? new RoiSpec()),
                FrameWidth = (uint)bitmap.PixelWidth,
                FrameHeight = (uint)bitmap.PixelHeight,
            };
        }
        finally
        {
            _scanLoop.FrameGate.Release();
        }
    }

    /// <summary>
    /// Writes the retained frame (or a crop of it) to the engine's output dir. This is how a
    /// plugin builds a replay corpus without raw frames ever crossing the boundary.
    /// </summary>
    public override async Task<DumpFrameResponse> DumpFrame(DumpFrameRequest request, ServerCallContext ctx)
    {
        await _scanLoop.FrameGate.WaitAsync(ctx.CancellationToken);
        try
        {
            var bitmap = _scanLoop.RetainedFrame;
            if (bitmap is null)
                return new DumpFrameResponse { NoFrame = true };

            var prefix = SanitizePrefix(request.Prefix);

            string path;
            if (request.FullFrame)
            {
                path = await FrameSaver.SavePngAsync(bitmap, _config.OutputDir, prefix);
            }
            else
            {
                var reference = (request.Roi ?? new Rect()).ToRoiRect();
                ScanLoop.EnsureRoiInFrame(reference, bitmap.PixelWidth, bitmap.PixelHeight);

                var frameRect = RoiScaler.ToFrame(reference, bitmap.PixelWidth, bitmap.PixelHeight);
                var bounds = OcrPipeline.ClampToBitmap(frameRect.ToBounds(), bitmap.PixelWidth, bitmap.PixelHeight);

                using var crop = await _ocr.CropAndScaleAsync(bitmap, bounds, 1.0);
                path = await FrameSaver.SavePngAsync(crop, _config.OutputDir, prefix);
            }

            return new DumpFrameResponse { NoFrame = false, Path = path };
        }
        finally
        {
            _scanLoop.FrameGate.Release();
        }
    }

    public override Task<StatusResponse> GetStatus(StatusRequest request, ServerCallContext ctx)
        => Task.FromResult(_status.Snapshot());

    /// <summary>
    /// Cancellation reaches us either as the token's own exception or, when gRPC has already
    /// mapped it, as a CANCELLED status — both mean "the call is over", not "the engine failed".
    /// </summary>
    private static bool IsCancellation(Exception e)
        => e is OperationCanceledException
        || (e is RpcException rpc && rpc.StatusCode == StatusCode.Cancelled);

    /// <summary>
    /// The prefix becomes part of a file name, and it arrives from another process: strip any path
    /// it carries so a plugin cannot steer a write out of the configured output dir.
    /// </summary>
    private static string SanitizePrefix(string prefix)
    {
        var name = Path.GetFileName(prefix.Trim());
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return name.Length == 0 ? DefaultDumpPrefix : name;
    }
}
