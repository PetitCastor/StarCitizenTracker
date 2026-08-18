using System.Diagnostics;
using System.Runtime.CompilerServices;
using GameCapture.Contracts;
using GameCapture.Contracts.Proto;
using Grpc.Core;
using Grpc.Net.Client;

namespace GameCapture.Sdk;

/// <summary>
/// A plugin's connection to the capture engine. Owns the channel and the generated stub so no
/// plugin ever names a proto type; everything it hands back is either a plain contract type or a
/// <see cref="TickData"/>.
/// </summary>
/// <remarks>
/// Deliberately no reconnect logic: a dropped pipe surfaces as an <see cref="RpcException"/> and
/// the plugin host decides what that means (usually loop back to
/// <see cref="WaitForEngineAsync"/> and re-subscribe). Hiding the drop inside the SDK would let a
/// tracker keep its state machine running across an engine restart it never learned about.
/// <para>
/// That still holds after protocol negotiation (TASK-05): transport failures keep their
/// <see cref="RpcException"/> shape everywhere, including inside the handshake. The SDK's own
/// exception types are raised only for what gRPC cannot express — a version the engine refuses,
/// or a peer that answers with something the protocol does not allow — and those are the two
/// cases a reconnect cannot fix.
/// </para>
/// </remarks>
public sealed class CaptureClient : IDisposable
{
    /// <summary>Gap between engine-availability polls. Startup, not a hot path.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long the engine has to acknowledge a Hello. The ack is written before the engine drains
    /// a single tick, so a healthy engine answers in the time one write takes; this bound exists
    /// for the engine that will never answer, not for a slow one.
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly GrpcChannel _channel;
    private readonly CaptureEngineService.CaptureEngineServiceClient _client;

    public CaptureClient(string pipeName = EngineDefaults.PipeName)
    {
        _channel = NamedPipeChannel.Create(pipeName);
        _client = new CaptureEngineService.CaptureEngineServiceClient(_channel);
    }

    /// <summary>
    /// The protocol version this client announces. Production is always
    /// <see cref="ProtocolVersion.Current"/>; the setter exists so the tests can drive a genuine
    /// mismatch through the real engine rather than through a stub that only claims to reject one.
    /// </summary>
    internal uint ClientProtocolVersion { get; init; } = ProtocolVersion.Current;

    /// <summary>Polls GetStatus until the engine answers or the timeout elapses. Lets a plugin
    /// start before the engine without a crash-loop.</summary>
    /// <exception cref="TimeoutException">
    /// The engine did not answer within <paramref name="timeout"/>. The last failed attempt is the
    /// inner exception — a plugin that logs only the message would otherwise lose the difference
    /// between "no engine on that pipe" (the attempt ran out its deadline) and "engine answered
    /// with an error".
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> fired.</exception>
    /// <exception cref="ProtocolMismatchException">
    /// The engine answered, but the range it advertises does not contain this SDK's protocol
    /// version. Raised on the first answer rather than retried: the versions of two running
    /// processes do not change, so polling on could only spin until the timeout.
    /// </exception>
    public async Task<EngineInfo> WaitForEngineAsync(TimeSpan timeout, CancellationToken ct)
        => EngineInfo.From(await WaitForStatusAsync(timeout, ct));

    /// <summary>
    /// <see cref="WaitForEngineAsync"/> without the mapping — the raw status message, for the engine's
    /// own tests and for the fields <see cref="EngineInfo"/> deliberately does not carry (the frame
    /// sequence, the supported protocol range).
    /// </summary>
    /// <remarks>
    /// Internal, not obsolete-public: <see cref="StatusResponse"/> is generated, and an exported
    /// signature naming it would put a proto type back on the surface the SDK exists to keep clean —
    /// which the architecture test in <c>GameCapture.Sdk.Tests</c> now enforces.
    /// </remarks>
    internal async Task<StatusResponse> WaitForStatusAsync(TimeSpan timeout, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        Exception? last = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException(
                    $"The capture engine did not answer within {timeout.TotalSeconds:0.##}s.", last);

            try
            {
                // Bounded by what is left of the budget, not by a fixed per-attempt deadline: a
                // pipe nobody is listening on makes ConnectAsync wait, and without a deadline the
                // very first attempt would hang past the timeout the caller asked for.
                var status = await _client.GetStatusAsync(new StatusRequest(),
                    deadline: DateTime.UtcNow.Add(remaining), cancellationToken: ct);

                // Inside the try on purpose, and safe there: ProtocolMismatchException is neither
                // an RpcException nor an OperationCanceledException, so the filter below leaves it
                // alone and it escapes instead of being folded into the retry loop.
                ProtocolNegotiation.EnsureSupported(
                    status.MinSupportedProtocol, status.MaxSupportedProtocol, ClientProtocolVersion);

                return status;
            }
            catch (Exception e) when (e is RpcException or OperationCanceledException
                                      && !ct.IsCancellationRequested)
            {
                // Every RPC failure means the same thing here — the engine is not serving yet.
                // Which one it was is kept for the TimeoutException rather than acted on.
                //
                // OperationCanceledException is in that set because the channel sets
                // ThrowOperationCanceledOnCancellation, which covers deadlines as well as tokens:
                // an attempt that burned its slice of the budget arrives here as an OCE. The
                // filter is what keeps the caller's own cancellation distinct — that one has
                // ct.IsCancellationRequested set and must propagate, not be swallowed into a
                // TimeoutException at the end of the loop.
                last = e;
            }

            var delay = timeout - elapsed.Elapsed;
            if (delay <= TimeSpan.Zero)
                continue; // let the budget check above raise the timeout

            await Task.Delay(delay < PollInterval ? delay : PollInterval, ct);
        }
    }

    /// <summary>
    /// Opens the Track stream, completes the handshake, sends the initial ROI set, returns the
    /// session. The returned session has already been told what protocol version and engine build
    /// it is talking to — the ack is awaited here rather than surfaced through
    /// <see cref="TrackSession.Ticks"/>, so a plugin never has to filter a handshake message out of
    /// its tick loop.
    /// </summary>
    /// <exception cref="ProtocolMismatchException">The engine refused the announced version.</exception>
    /// <exception cref="SessionFaultedException">
    /// The engine answered the handshake with something the protocol does not allow (a tick before
    /// the ack, or an acknowledged version never offered). A peer bug; retrying will not help.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// The engine accepted the stream but never acknowledged the Hello. Reported the same way
    /// <see cref="WaitForEngineAsync"/> reports an engine that does not answer, so a plugin retries
    /// its connect rather than dying on an engine that is merely slow.
    /// </exception>
    /// <exception cref="RpcException">
    /// Ordinary transport failure — the engine went away mid-handshake, most likely. Left untyped
    /// on purpose; see the remark on <see cref="TrackSession.ReceiveHelloAckAsync"/>.
    /// </exception>
    /// <param name="clientName">
    /// Identifies this plugin on the stream: what the engine lists in <c>GetStatus</c>, and what a
    /// user reads when two plugins share one engine.
    /// </param>
    /// <param name="rois">
    /// The complete region set for the session. Sent as the initial subscription, so it must be
    /// whole before the first tick — per-tick atomicity leaves no mid-tick round-trip to add one.
    /// </param>
    /// <param name="sessionCt">
    /// Governs the whole subscription, not just this call: it is the Track call's own token, so
    /// firing it later ends the stream and makes <see cref="TrackSession.Ticks"/> throw. Pass the
    /// plugin's long-lived token. In particular do NOT reuse a startup-scoped source shared with
    /// <see cref="WaitForEngineAsync"/> — its connect timeout would fire mid-session and read as
    /// an unexplained engine disconnect.
    /// </param>
    public async Task<TrackSession> TrackAsync(string clientName,
        IReadOnlyList<RoiSubscription> rois, CancellationToken sessionCt)
    {
        var session = new TrackSession(_client.Track(cancellationToken: sessionCt));
        try
        {
            await session.SendHelloAsync(clientName, ClientProtocolVersion);

            // Before the ROI set rather than after: an engine that refuses the version faults the
            // stream, and subscribing into a stream already on its way down would report the
            // refusal as a failed write on the ROI update instead of as the mismatch it is.
            await session.ReceiveHelloAckAsync(HandshakeTimeout, ClientProtocolVersion, sessionCt);

            await session.UpdateRoisAsync(rois);
            return session;
        }
        catch
        {
            // A half-opened stream would sit registered in the engine until the connection times
            // out, and the engine would keep queueing ticks for a client that will never read.
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Saves the engine's most recent frame as a PNG. A null <paramref name="roi"/> dumps the whole
    /// frame; otherwise the reference-space rect is cropped. Returns the absolute path, or null if
    /// the engine has not scanned a frame yet.
    /// </summary>
    /// <remarks>This is how a plugin builds a replay corpus: the raw frame itself never crosses
    /// the boundary, only the path the engine wrote it to.</remarks>
    public async Task<string?> DumpFrameAsync(RoiRect? roi, string prefix, CancellationToken ct)
    {
        var request = new DumpFrameRequest { FullFrame = roi is null, Prefix = prefix ?? string.Empty };
        if (roi is { } rect)
            request.Roi = rect.ToProto();

        var response = await _client.DumpFrameAsync(request, cancellationToken: ct);
        return response.NoFrame ? null : response.Path;
    }

    /// <summary>
    /// A raw status read, deliberately without the protocol range check
    /// <see cref="WaitForEngineAsync"/> applies: this is the call a diagnostic uses to ask an
    /// engine what it is, including an engine this SDK could not hold a session with. Use
    /// <see cref="WaitForEngineAsync"/> to connect; use this to report.
    /// </summary>
    public async Task<EngineInfo> GetStatusAsync(CancellationToken ct)
        => EngineInfo.From(await GetStatusResponseAsync(ct));

    /// <summary>The raw status message; see <see cref="WaitForStatusAsync"/> for why it is internal.</summary>
    internal async Task<StatusResponse> GetStatusResponseAsync(CancellationToken ct)
        => await _client.GetStatusAsync(new StatusRequest(), cancellationToken: ct);

    /// <summary>
    /// Reads one region against the engine's most recently scanned frame, outside any tick. Returns
    /// null when the engine has not scanned a frame yet.
    /// </summary>
    /// <remarks>
    /// A calibration aid, not a data path: it deliberately does not make the engine capture, so what
    /// comes back is exactly what the last tick saw — which is what makes it usable for checking a
    /// ROI constant against a frame a plugin has already reacted to. Anything a plugin acts on
    /// belongs in <see cref="TrackSession.Ticks"/>, where per-tick atomicity holds; two reads through
    /// here are two separate round-trips and may straddle a frame.
    /// </remarks>
    /// <exception cref="RoiResultException">
    /// The engine flagged the region as failed, or the result cannot be read as OCR — notably when
    /// <paramref name="roi"/> is a <see cref="RoiKind.Pixels"/> subscription, which has no OCR to
    /// return. Raised rather than folded into a null so a mis-declared region cannot read as "the
    /// engine has no frame".
    /// </exception>
    public async Task<OcrRegionResult?> ReadRoiAsync(RoiSubscription roi, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roi);

        var response = await _client.ReadRoiAsync(new ReadRoiRequest { Roi = roi.ToProto() },
            cancellationToken: ct);

        if (response.NoFrame)
            return null;

        // A response that is neither "no frame" nor a result is a peer bug. Named as one here
        // because the alternative — mapping an all-defaults RoiResult — reports it as an
        // effective_scale violation on a region the engine never even read.
        if (response.Result is not { } result)
            throw new RoiResultException(roi.Id.Value,
                "the engine answered a ReadRoi with neither a frame flag nor a result.",
                reportedByEngine: false);

        return result.ToOcrRegionResult();
    }

    public void Dispose() => _channel.Dispose();
}

/// <summary>
/// An open Track subscription. The engine pushes one tick per scanned frame for as long as this
/// lives; the plugin's ROI set can be replaced at any time without reopening the stream.
/// </summary>
public sealed class TrackSession : IAsyncDisposable
{
    private readonly AsyncDuplexStreamingCall<TrackRequest, TrackResponse> _call;

    // Two request-stream writers exist in practice — the initial subscribe and any later
    // UpdateRoisAsync from a tracker thread — and gRPC forbids concurrent writes on one stream.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private bool _requestStreamClosed;
    private int _disposed;

    internal TrackSession(AsyncDuplexStreamingCall<TrackRequest, TrackResponse> call) => _call = call;

    /// <summary>
    /// Protocol version this session settled on — the lower of what the SDK announced and what the
    /// engine speaks. A plugin reads it to decide whether a newer field is worth looking at.
    /// </summary>
    public uint NegotiatedProtocol { get; private set; }

    /// <summary>Build of the engine on the other end, as it reported itself in the handshake.</summary>
    public string EngineVersion { get; private set; } = string.Empty;

    /// <summary>Ticks as they arrive. Completes normally when the server ends the stream
    /// (replay finished / engine shutdown); throws RpcException(Unavailable) if the pipe drops,
    /// and OperationCanceledException — not RpcException(Cancelled) — when either
    /// <paramref name="ct"/> or the session's own token fires.</summary>
    public async IAsyncEnumerable<TickData> Ticks([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var response in _call.ResponseStream.ReadAllAsync(ct))
        {
            // Forward compatibility: a future engine may add response kinds, and an older plugin
            // must ignore them rather than treat an unset oneof as an empty tick.
            if (response.MsgCase != TrackResponse.MsgOneofCase.Tick)
                continue;

            yield return TickData.From(response.Tick);
        }
    }

    /// <summary>Full-replacement update of the subscribed set.</summary>
    public async Task UpdateRoisAsync(IReadOnlyList<RoiSubscription> rois)
    {
        var update = new RoiSetUpdate();
        foreach (var roi in rois)
            update.Rois.Add(roi.ToProto());

        await SendAsync(new TrackRequest { Rois = update });
    }

    internal Task SendHelloAsync(string clientName, uint protocolVersion)
        => SendAsync(new TrackRequest
        {
            Hello = new Hello { ClientName = clientName, ProtocolVersion = protocolVersion },
        });

    /// <summary>
    /// Reads the stream up to and including the HelloAck, and records what it said. Everything the
    /// engine sends afterwards belongs to <see cref="Ticks"/>; nothing is consumed past the ack.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tick arriving before the ack is treated as a broken engine rather than tolerated. The
    /// engine writes the ack ahead of the first tick by construction (it travels beside the tick
    /// channel, not through it), so a tick here means the peer does not implement the handshake —
    /// and silently continuing would leave <see cref="NegotiatedProtocol"/> reading a default for
    /// the life of a session the plugin believes was negotiated. Response kinds this SDK does not
    /// recognise are skipped instead, which is the forward-compatible half of the same rule.
    /// </para>
    /// <para>
    /// What this method does NOT do is re-type ordinary transport failures. It is the first READ on
    /// the stream, and a read is where a dropped pipe actually surfaces — so before the handshake
    /// existed those failures reached the plugin from <see cref="Ticks"/> instead, as an
    /// <see cref="RpcException"/> its reconnect loop knows. Translating them here would move a
    /// routine mid-connect engine restart from "reconnect" to "unhandled exception", because
    /// nothing catches a <see cref="GameCaptureException"/> until the plugin host lands (TASK-07).
    /// Only the version rejection — which cannot occur against a matching engine and must not be
    /// retried when it does — becomes an SDK type here.
    /// </para>
    /// </remarks>
    internal async Task ReceiveHelloAckAsync(TimeSpan timeout, uint sdkVersion, CancellationToken sessionCt)
    {
        using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
        ackCts.CancelAfter(timeout);

        try
        {
            while (await _call.ResponseStream.MoveNext(ackCts.Token))
            {
                var response = _call.ResponseStream.Current;
                switch (response.MsgCase)
                {
                    case TrackResponse.MsgOneofCase.HelloAck:
                        Accept(response.HelloAck, sdkVersion);
                        return;

                    case TrackResponse.MsgOneofCase.Tick:
                        throw new SessionFaultedException(
                            "The engine sent a tick before acknowledging the handshake; it does not " +
                            "implement protocol negotiation.");
                }
            }

            // The stream ended before anything was acknowledged, which is what an engine shutting
            // down mid-connect looks like. Not a fault: the session is simply over, Ticks will
            // complete immediately, and a plugin reads that as the clean stream end it always did.
            // Faulting here would turn an orderly engine stop into a crash on the way in.
        }
        catch (RpcException e) when (ProtocolNegotiation.IsProtocolRejection(e))
        {
            throw ProtocolNegotiation.Translate(e, sdkVersion);
        }
        catch (OperationCanceledException e) when (!sessionCt.IsCancellationRequested)
        {
            // Our own deadline, not the caller's token: the channel maps both to an OCE, and only
            // the filter tells them apart. The caller's cancellation propagates as-is — a plugin
            // shutting down must not read as an engine failure.
            //
            // TimeoutException rather than EngineUnavailableException deliberately: this is the
            // same "the engine did not answer" that WaitForEngineAsync already reports that way,
            // and both plugin loops retry the whole connect on it. An SDK type would escape every
            // catch clause they have and kill the plugin over an engine that is merely slow.
            throw new TimeoutException(
                $"The engine did not acknowledge the handshake within {timeout.TotalSeconds:0.##}s.", e);
        }
    }

    /// <summary>
    /// Records an ack after checking it says something possible. The engine answers
    /// <c>Min(requested, its own Current)</c>, so a zero (the proto3 default an engine that filled
    /// only <c>engine_version</c> would send) or anything above what this client announced is a
    /// peer bug — and accepting it verbatim is exactly the "negotiated session that was never
    /// negotiated" this class refuses a tick-before-ack to avoid.
    /// </summary>
    private void Accept(HelloAck ack, uint sdkVersion)
    {
        if (ack.NegotiatedProtocolVersion == 0 || ack.NegotiatedProtocolVersion > sdkVersion)
            throw new SessionFaultedException(
                $"The engine acknowledged protocol {ack.NegotiatedProtocolVersion}, which this SDK " +
                $"(speaking {sdkVersion}) never offered.");

        NegotiatedProtocol = ack.NegotiatedProtocolVersion;
        EngineVersion = ack.EngineVersion;
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent: `await using` around the session plus an explicit cleanup on a failure path
        // is an ordinary shape (TrackAsync itself does it), and the second call must not be the
        // thing that throws.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _writeGate.WaitAsync();
        try
        {
            if (!_requestStreamClosed)
            {
                _requestStreamClosed = true;

                // Half-close so the engine's request pump can finish; if the call is already dead
                // that is exactly the state we were trying to reach.
                try { await _call.RequestStream.CompleteAsync(); }
                catch (RpcException) { }
                catch (InvalidOperationException) { }

                // And an OCE, which is the same "the call is already dead" in the shape the
                // channel's ThrowOperationCanceledOnCancellation produces. This path is no longer
                // rare: TrackAsync disposes the session on every failed handshake, so a cleanup
                // throwing here would replace the ProtocolMismatchException the caller needed
                // with an exception about the cleanup.
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            _writeGate.Release();
        }

        // The gate is deliberately NOT disposed. A tracker thread may be parked in SendAsync's
        // WaitAsync right now — the concurrency this class exists to serialise — and the Release
        // above hands it the gate; disposing it here would make its finally-block Release throw
        // from semaphore internals instead of letting SendAsync raise the ObjectDisposedException
        // it means to. A SemaphoreSlim whose AvailableWaitHandle was never touched holds nothing
        // but managed memory, so there is no leak to trade against that.
        _call.Dispose();
    }

    private async Task SendAsync(TrackRequest request)
    {
        await _writeGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_requestStreamClosed, this);
            await _call.RequestStream.WriteAsync(request);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
