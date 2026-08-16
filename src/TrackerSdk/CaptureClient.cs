using System.Diagnostics;
using System.Runtime.CompilerServices;
using CaptureContracts;
using CaptureContracts.Proto;
using Grpc.Core;
using Grpc.Net.Client;

namespace TrackerSdk;

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
/// </remarks>
public sealed class CaptureClient : IDisposable
{
    /// <summary>Gap between engine-availability polls. Startup, not a hot path.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly GrpcChannel _channel;
    private readonly CaptureEngineService.CaptureEngineServiceClient _client;

    public CaptureClient(string pipeName = NamedPipeChannel.DefaultPipeName)
    {
        _channel = NamedPipeChannel.Create(pipeName);
        _client = new CaptureEngineService.CaptureEngineServiceClient(_channel);
    }

    /// <summary>Polls GetStatus until the engine answers or the timeout elapses. Lets a plugin
    /// start before the engine without a crash-loop.</summary>
    /// <exception cref="TimeoutException">
    /// The engine did not answer within <paramref name="timeout"/>. The last failed attempt is the
    /// inner exception — a plugin that logs only the message would otherwise lose the difference
    /// between "no engine on that pipe" (the attempt ran out its deadline) and "engine answered
    /// with an error".
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> fired.</exception>
    public async Task<StatusResponse> WaitForEngineAsync(TimeSpan timeout, CancellationToken ct)
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
                return await _client.GetStatusAsync(new StatusRequest(),
                    deadline: DateTime.UtcNow.Add(remaining), cancellationToken: ct);
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

    /// <summary>Opens the Track stream, sends Hello + the initial ROI set, returns the session.</summary>
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
            await session.SendHelloAsync(clientName);
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

    public async Task<StatusResponse> GetStatusAsync(CancellationToken ct)
        => await _client.GetStatusAsync(new StatusRequest(), cancellationToken: ct);

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

    internal Task SendHelloAsync(string clientName)
        => SendAsync(new TrackRequest { Hello = new Hello { ClientName = clientName } });

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
