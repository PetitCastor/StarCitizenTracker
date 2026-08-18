using GameCapture.Contracts;
using GameCapture.Contracts.Proto;
using Grpc.Core;

namespace GameCapture.Engine.Grpc;

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

        // The handshake result travels beside the tick channel rather than through it, and the
        // response side writes it before it drains a single tick. Both halves of that matter:
        // the channel evicts its OLDEST entry when a live client falls behind, and the oldest
        // entry is precisely the ack; and the scan loop starts pushing ticks at a client the
        // moment it registers, which is before its Hello has even been read. Routed through the
        // channel the ack could therefore arrive after a tick, or never arrive at all. A null
        // result means the client never sent a Hello — there is nothing to acknowledge.
        var handshake = new TaskCompletionSource<HelloAck?>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var msg in requestStream.ReadAllAsync(pumpCts.Token))
                    {
                        switch (msg.MsgCase)
                        {
                            case TrackRequest.MsgOneofCase.Hello:
                                // A second Hello cannot renegotiate: the version is settled and the
                                // ack has already gone out.
                                if (handshake.Task.IsCompleted)
                                    break;

                                client.Name = msg.Hello.ClientName;
                                var version = msg.Hello.ProtocolVersion == 0 ? 1u : msg.Hello.ProtocolVersion;
                                if (version < ProtocolVersion.Min || version > ProtocolVersion.Current)
                                {
                                    throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                        $"protocol {version} unsupported; engine speaks {ProtocolVersion.Min}-{ProtocolVersion.Current}"),
                                        new Metadata
                                        {
                                            { "gamecapture-protocol-min", ProtocolVersion.Min.ToString() },
                                            { "gamecapture-protocol-max", ProtocolVersion.Current.ToString() },
                                        });
                                }

                                var status = _status.Snapshot();
                                handshake.TrySetResult(new HelloAck
                                {
                                    NegotiatedProtocolVersion = Math.Min(version, ProtocolVersion.Current),
                                    EngineVersion = status.EngineVersion,
                                    FrameWidth = status.FrameWidth,
                                    FrameHeight = status.FrameHeight,
                                    ReplayMode = status.ReplayMode,
                                });
                                break;
                            case TrackRequest.MsgOneofCase.Rois:
                                // The handshake window closes at the first non-Hello message. A
                                // client that skips Hello would otherwise leave the response side
                                // waiting for an ack that is never coming while the scan loop fills
                                // its channel behind it — a deadlock in replay, where the loop
                                // blocks on a full channel instead of dropping.
                                handshake.TrySetResult(null);
                                client.SetRois(msg.Rois);
                                break;
                        }
                    }
                }
                finally
                {
                    // Request stream ended or failed without a Hello: unblock the response side.
                    handshake.TrySetResult(null);
                }
            }, pumpCts.Token);

            // Handshake before ticks, on the wire and not merely by intent.
            if (!await TryWriteHelloAckAsync(handshake.Task, pump, responseStream))
                return;

            // Completes when the registry completes the channel: replay finished, or the engine
            // is shutting down. A client disconnecting instead surfaces as a cancellation. A
            // failed request pump must end the call immediately too: otherwise an unsupported
            // Hello would be stranded behind a response read that can never produce another item.
            while (true)
            {
                if (pump.IsFaulted && !await ObservePumpAsync(pump))
                    return;

                var read = client.Out.Reader.WaitToReadAsync(ctx.CancellationToken).AsTask();
                if (!pump.IsCompleted)
                {
                    var completed = await Task.WhenAny(read, pump);
                    if (completed == pump && !await ObservePumpAsync(pump))
                        return;
                }

                if (!await read)
                    break;

                while (client.Out.Reader.TryRead(out var response))
                    await responseStream.WriteAsync(response);
            }

            pumpCts.Cancel();
            await ObservePumpAsync(pump);
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
    {
        var response = _status.Snapshot();
        response.MinSupportedProtocol = ProtocolVersion.Min;
        response.MaxSupportedProtocol = ProtocolVersion.Current;

        // From the loop, not from the config: the loop clamps a too-small configured interval, and
        // reporting the unclamped number would have a plugin time out three ticks early forever.
        response.ScanIntervalMs = (uint)_scanLoop.ScanInterval.TotalMilliseconds;

        return Task.FromResult(response);
    }

    /// <summary>
    /// Waits for the handshake to settle and writes the ack, if there is one. Returns false when
    /// the call is already over, in which case the caller must not go on to stream ticks.
    /// </summary>
    private static async Task<bool> TryWriteHelloAckAsync(
        Task<HelloAck?> handshake, Task pump, IServerStreamWriter<TrackResponse> responseStream)
    {
        // Racing the pump matters as much as awaiting the handshake: an unsupported version faults
        // the pump without ever settling the handshake, and waiting on it alone would hang the call
        // instead of delivering the rejection the client is waiting for.
        if (await Task.WhenAny(handshake, pump) == pump && !await ObservePumpAsync(pump))
            return false;

        var ack = await handshake;
        if (ack is not null)
            await responseStream.WriteAsync(new TrackResponse { HelloAck = ack });

        return true;
    }

    /// <summary>
    /// Observes a completed pump: a genuine failure (an unsupported protocol version, trailers and
    /// all) is rethrown, while a hangup is reported as false. Returns true if the pump ended
    /// cleanly. Never let a bare <c>await pump</c> stand in for this — a client disconnect reaches
    /// the pump as an <see cref="RpcException"/> with a CANCELLED status about as often as it does
    /// an <see cref="OperationCanceledException"/>, and rethrowing that turns an ordinary
    /// disconnect into an exception escaping <see cref="Track"/>.
    /// </summary>
    private static async Task<bool> ObservePumpAsync(Task pump)
    {
        try
        {
            await pump;
            return true;
        }
        catch (Exception e) when (IsCancellation(e))
        {
            return false;
        }
    }

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
