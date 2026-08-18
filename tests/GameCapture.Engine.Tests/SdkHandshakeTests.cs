using GameCapture.Contracts;
using GameCapture.Contracts.Proto;
using Grpc.Core;
using GameCapture.Sdk;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// The SDK's half of version negotiation (MATURITY TASK-05). The engine's half has its own suite;
/// what is under test here is the client — that it announces a version, reports what came back, and
/// turns both kinds of refusal into an SDK exception rather than an <see cref="RpcException"/> the
/// plugin would have to decode.
/// </summary>
/// <remarks>
/// Split by what each branch actually depends on. The mismatch cases drive a real engine over a
/// real pipe, using the client's internal version seam, because the trailers a rejection is
/// recognised by are written by the engine — a stub asserting against trailers a test wrote itself
/// could not tell a working translation from two copies of the same mistake. The rest of
/// <see cref="TrackSession.ReceiveHelloAckAsync"/>'s branches depend on nothing the engine does and
/// run over a scripted stream instead; several of them (a mute peer, a stream that ends mid-connect)
/// a real engine cannot be made to produce on demand at all.
/// </remarks>
public class SdkHandshakeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a plugin is willing to wait for an engine that is already up.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Outside anything the engine speaks, and outside anything it plausibly will.</summary>
    private const uint UnsupportedVersion = 999;

    private static string NewPipeName() => $"sc-sdk-hs-{Guid.NewGuid():N}";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TrackAsync_OnASupportedVersion_ExposesTheNegotiatedProtocolAndEngineVersion()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await using var engine = await StartEngineAsync(cts.Token);

        using var client = new CaptureClient(engine.PipeName);
        var status = await client.WaitForEngineAsync(ConnectTimeout, cts.Token);

        await using var session = await client.TrackAsync("handshake",
            [EngineTestFixtures.PanelStateSubscription()], cts.Token);

        Assert.Equal(ProtocolVersion.Current, session.NegotiatedProtocol);

        // Against the engine's own report, not against a literal: the ack has to carry the running
        // engine's build, and a hard-coded string would keep passing if it carried an empty one.
        Assert.NotEmpty(session.EngineVersion);
        Assert.Equal(status.EngineVersion, session.EngineVersion);
    }

    /// <summary>
    /// The engine advertises its range on GetStatus, so an incompatible client can be turned away
    /// before it opens a stream at all. That the check runs there and not only on the Hello is the
    /// point: a session refused mid-stream is far harder for a plugin to report than a connect that
    /// refused itself.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WaitForEngineAsync_WhenTheEngineRangeExcludesTheSdk_ThrowsProtocolMismatch()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await using var engine = await StartEngineAsync(cts.Token);

        using var client = new CaptureClient(engine.PipeName) { ClientProtocolVersion = UnsupportedVersion };

        var ex = await Assert.ThrowsAsync<ProtocolMismatchException>(
            () => client.WaitForEngineAsync(ConnectTimeout, cts.Token));

        Assert.Equal(ProtocolVersion.Min, ex.EngineMin);
        Assert.Equal(ProtocolVersion.Current, ex.EngineMax);
        Assert.Equal(UnsupportedVersion, ex.SdkVersion);
    }

    /// <summary>
    /// The same refusal one step later, as a client that skipped the pre-check would meet it: the
    /// engine faults the stream with FAILED_PRECONDITION and the range in trailers, and the SDK has
    /// to recognise that as a version problem rather than as a dead session.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TrackAsync_WhenTheEngineRefusesTheVersion_ThrowsProtocolMismatch()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await using var engine = await StartEngineAsync(cts.Token);

        // Deliberately no WaitForEngineAsync: its pre-check would raise this before a Hello was
        // ever sent, and the wire path is what this test is about.
        using var client = new CaptureClient(engine.PipeName) { ClientProtocolVersion = UnsupportedVersion };

        var ex = await Assert.ThrowsAsync<ProtocolMismatchException>(() => client.TrackAsync(
            "unsupported", [EngineTestFixtures.PanelStateSubscription()], cts.Token));

        Assert.Equal(ProtocolVersion.Min, ex.EngineMin);
        Assert.Equal(ProtocolVersion.Current, ex.EngineMax);
        Assert.Equal(UnsupportedVersion, ex.SdkVersion);

        // The rejection came off the wire rather than out of a local check, which is the difference
        // between this test and the one above it.
        Assert.IsType<RpcException>(ex.InnerException);
    }

    [Theory]
    [InlineData(1u, 1u, 1u)]
    [InlineData(1u, 3u, 2u)]
    [InlineData(2u, 2u, 2u)]
    public void EnsureSupported_WhenTheRangeContainsTheSdkVersion_Passes(uint min, uint max, uint sdk)
        => Assert.Null(Record.Exception(() => ProtocolNegotiation.EnsureSupported(min, max, sdk)));

    [Theory]
    [InlineData(2u, 4u, 1u)]  // SDK older than anything the engine still speaks
    [InlineData(1u, 1u, 2u)]  // SDK newer than the engine
    public void EnsureSupported_WhenTheRangeExcludesTheSdkVersion_Throws(uint min, uint max, uint sdk)
    {
        var ex = Assert.Throws<ProtocolMismatchException>(
            () => ProtocolNegotiation.EnsureSupported(min, max, sdk));

        Assert.Equal(min, ex.EngineMin);
        Assert.Equal(max, ex.EngineMax);
        Assert.Equal(sdk, ex.SdkVersion);
        Assert.Contains($"{min}-{max}", ex.Message);
    }

    /// <summary>
    /// An engine built before TASK-04 leaves both range fields at the proto3 default. It reads as a
    /// mismatch like any other, and says so in words a user can act on — it cannot answer a Hello,
    /// so admitting it would only turn a clear message into a handshake timeout.
    /// </summary>
    [Fact]
    public void EnsureSupported_AgainstAnEngineThatReportsNoRange_ThrowsSayingItPredatesNegotiation()
    {
        var ex = Assert.Throws<ProtocolMismatchException>(
            () => ProtocolNegotiation.EnsureSupported(0, 0, ProtocolVersion.Current));

        Assert.Equal(0u, ex.EngineMax);
        Assert.Contains("predates protocol negotiation", ex.Message);
    }

    /// <summary>
    /// Exercises <see cref="ProtocolNegotiation.Translate"/>'s own table directly, not through
    /// <see cref="TrackSession.ReceiveHelloAckAsync"/>: today that call site only ever reaches
    /// <c>Translate</c> on the protocol-rejection arm (see its remarks), so the other rows here
    /// prove the table out for the plugin host that will call it more broadly (TASK-07), not
    /// anything the SDK itself does yet. The <see cref="StatusCode.Cancelled"/> case in particular
    /// is only safe to feed to <c>Translate</c> here because this theory calls it directly — a real
    /// caller must rule out cancellation first, per the remark on <c>Translate</c>, or an orderly
    /// shutdown would be reported as a faulted session.
    /// </summary>
    [Theory]
    [InlineData(StatusCode.Unavailable, typeof(EngineUnavailableException))]
    [InlineData(StatusCode.DeadlineExceeded, typeof(EngineUnavailableException))]
    [InlineData(StatusCode.Cancelled, typeof(SessionFaultedException))]
    [InlineData(StatusCode.Internal, typeof(SessionFaultedException))]
    [InlineData(StatusCode.Unimplemented, typeof(SessionFaultedException))]
    // No trailers: FAILED_PRECONDITION is a status any future handler may return for its own
    // reasons, and only the range trailers say the handshake is what was refused.
    [InlineData(StatusCode.FailedPrecondition, typeof(SessionFaultedException))]
    public void Translate_MapsStatusCodesToTheSdkSurface(StatusCode code, Type expected)
    {
        var translated = ProtocolNegotiation.Translate(Rpc(code), ProtocolVersion.Current);

        Assert.IsType(expected, translated);

        // The status that caused it stays reachable: a host that logs only the SDK message would
        // otherwise lose the one detail that says which failure this was.
        Assert.IsType<RpcException>(translated.InnerException);
    }

    [Fact]
    public void Translate_OnAProtocolRejection_CarriesTheAdvertisedRange()
    {
        var rejection = Rpc(StatusCode.FailedPrecondition, new Metadata
        {
            { ProtocolNegotiation.MinTrailer, "2" },
            { ProtocolNegotiation.MaxTrailer, "5" },
        });

        var translated = Assert.IsType<ProtocolMismatchException>(
            ProtocolNegotiation.Translate(rejection, UnsupportedVersion));

        Assert.Equal(2u, translated.EngineMin);
        Assert.Equal(5u, translated.EngineMax);
        Assert.Equal(UnsupportedVersion, translated.SdkVersion);
    }

    /// <summary>
    /// Half a range, or a range that is not a number, is not the engine's protocol rejection — and
    /// reporting it as one would tell the user to upgrade over what is really a broken peer.
    /// </summary>
    [Theory]
    [InlineData("1", null)]
    [InlineData(null, "1")]
    [InlineData("one", "1")]
    public void Translate_OnAMalformedRange_FallsBackToSessionFaulted(string? min, string? max)
    {
        var trailers = new Metadata();
        if (min is not null)
            trailers.Add(ProtocolNegotiation.MinTrailer, min);
        if (max is not null)
            trailers.Add(ProtocolNegotiation.MaxTrailer, max);

        Assert.IsType<SessionFaultedException>(ProtocolNegotiation.Translate(
            Rpc(StatusCode.FailedPrecondition, trailers), ProtocolVersion.Current));
    }

    private static RpcException Rpc(StatusCode code, Metadata? trailers = null)
        => new(new Status(code, "detail"), trailers ?? new Metadata());

    /// <summary>
    /// <see cref="TrackSession.ReceiveHelloAckAsync"/>'s own branches, driven over a scripted
    /// stream instead of a real engine. The mismatch tests above exist because the trailers a
    /// rejection is recognised by are written by the engine and worth proving against the real
    /// thing once; everything else in <see cref="ReceiveHelloAckAsync"/> — the timeout, the
    /// tick-before-ack fault, a clean stream end, and which transport failures stay untyped — has
    /// nothing to do with what the engine actually does and would only make these tests slower and
    /// flakier to route through one.
    /// </summary>
    [Fact]
    public async Task ReceiveHelloAckAsync_WhenTheEngineNeverAcknowledges_ThrowsTimeout()
    {
        // Proves the handshake's own deadline actually fires, as distinct from WaitForEngineAsync's
        // — a peer that accepted the stream but then went quiet must not hang a plugin forever.
        await using var session = new TrackSession(FakeCall(ScriptedResponses.Hanging()));

        await Assert.ThrowsAsync<TimeoutException>(() => session.ReceiveHelloAckAsync(
            TimeSpan.FromMilliseconds(50), ProtocolVersion.Current, CancellationToken.None));
    }

    [Fact]
    public async Task ReceiveHelloAckAsync_WhenATickArrivesBeforeTheAck_ThrowsSessionFaulted()
    {
        // The engine writes the ack ahead of the first tick by construction, so a tick landing
        // first means the peer does not implement the handshake at all — tolerating it would leave
        // NegotiatedProtocol reading its default for the life of a session the plugin believes was
        // negotiated.
        var tick = new TrackResponse { Tick = new TickResult() };
        await using var session = new TrackSession(FakeCall(ScriptedResponses.Replaying(tick)));

        await Assert.ThrowsAsync<SessionFaultedException>(() => session.ReceiveHelloAckAsync(
            ConnectTimeout, ProtocolVersion.Current, CancellationToken.None));
    }

    [Fact]
    public async Task ReceiveHelloAckAsync_WhenTheStreamEndsWithNoAck_ReturnsWithNegotiatedProtocolUnset()
    {
        // What an engine shutting down mid-connect looks like on the wire: the response stream
        // simply ends. Not a fault — Ticks would complete immediately afterward the same way it
        // always has, and raising here would turn an orderly engine stop into a crash on the way in.
        await using var session = new TrackSession(FakeCall(ScriptedResponses.Replaying()));

        await session.ReceiveHelloAckAsync(ConnectTimeout, ProtocolVersion.Current, CancellationToken.None);

        Assert.Equal(0u, session.NegotiatedProtocol);
    }

    [Fact]
    public async Task ReceiveHelloAckAsync_WhenTheAckNegotiatesVersionZero_ThrowsSessionFaulted()
    {
        // Zero is the proto3 default an engine that only filled engine_version would send, never a
        // genuine negotiated value; accepting it verbatim would be exactly the "negotiated session
        // that was never negotiated" a tick-before-ack is refused to avoid.
        var ack = new TrackResponse { HelloAck = new HelloAck { NegotiatedProtocolVersion = 0 } };
        await using var session = new TrackSession(FakeCall(ScriptedResponses.Replaying(ack)));

        await Assert.ThrowsAsync<SessionFaultedException>(() => session.ReceiveHelloAckAsync(
            ConnectTimeout, ProtocolVersion.Current, CancellationToken.None));
    }

    [Fact]
    public async Task ReceiveHelloAckAsync_WhenTheAckNegotiatesAVersionAboveWhatWasOffered_ThrowsSessionFaulted()
    {
        // The engine can only ever answer Min(requested, its own Current); an ack above what this
        // client announced is a peer bug, not a version this session could have meant to accept.
        var ack = new TrackResponse { HelloAck = new HelloAck { NegotiatedProtocolVersion = 2 } };
        await using var session = new TrackSession(FakeCall(ScriptedResponses.Replaying(ack)));

        await Assert.ThrowsAsync<SessionFaultedException>(
            () => session.ReceiveHelloAckAsync(ConnectTimeout, sdkVersion: 1, CancellationToken.None));
    }

    [Fact]
    public async Task ReceiveHelloAckAsync_OnAnUnavailableStatus_PropagatesTheRpcExceptionUnchanged()
    {
        // Load-bearing per the remark on ReceiveHelloAckAsync: plugin reconnect loops catch
        // RpcException directly, so re-typing an ordinary dropped-pipe failure into an SDK
        // exception here would turn a routine mid-handshake reconnect into an unhandled exception.
        var fault = Rpc(StatusCode.Unavailable);
        await using var session = new TrackSession(FakeCall(ScriptedResponses.Faulting(fault)));

        var ex = await Assert.ThrowsAsync<RpcException>(() => session.ReceiveHelloAckAsync(
            ConnectTimeout, ProtocolVersion.Current, CancellationToken.None));

        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
    }

    [Fact]
    public async Task ReceiveHelloAckAsync_OnACancelledStatus_PropagatesTheRpcExceptionUnchanged()
    {
        // CANCELLED carries no protocol trailers, so it is not a protocol rejection and falls
        // through to the same untyped path as any other transport failure — the same status
        // Translate's own theory above needs a direct call to reach safely.
        var fault = Rpc(StatusCode.Cancelled);
        await using var session = new TrackSession(FakeCall(ScriptedResponses.Faulting(fault)));

        var ex = await Assert.ThrowsAsync<RpcException>(() => session.ReceiveHelloAckAsync(
            ConnectTimeout, ProtocolVersion.Current, CancellationToken.None));

        Assert.Equal(StatusCode.Cancelled, ex.StatusCode);
    }

    [Fact]
    public async Task ReceiveHelloAckAsync_OnFailedPreconditionWithoutTrailers_PropagatesTheRpcExceptionUnchanged()
    {
        // FAILED_PRECONDITION alone is not enough to call this a version refusal: only the range
        // trailers say the handshake is what was refused, and a future handler may return this
        // status for a reason that has nothing to do with protocol negotiation.
        var fault = Rpc(StatusCode.FailedPrecondition);
        await using var session = new TrackSession(FakeCall(ScriptedResponses.Faulting(fault)));

        var ex = await Assert.ThrowsAsync<RpcException>(() => session.ReceiveHelloAckAsync(
            ConnectTimeout, ProtocolVersion.Current, CancellationToken.None));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task ReceiveHelloAckAsync_OnAProtocolRejection_ThrowsProtocolMismatchCarryingTheRange()
    {
        // The one gRPC failure the handshake re-types, reproduced here without a running engine:
        // the range trailers are exactly what the wire-path integration test above proves a real
        // engine writes, so scripting them is testing the same recognition rule, not a fiction.
        var fault = Rpc(StatusCode.FailedPrecondition, new Metadata
        {
            { ProtocolNegotiation.MinTrailer, "2" },
            { ProtocolNegotiation.MaxTrailer, "5" },
        });
        await using var session = new TrackSession(FakeCall(ScriptedResponses.Faulting(fault)));

        var ex = await Assert.ThrowsAsync<ProtocolMismatchException>(() => session.ReceiveHelloAckAsync(
            ConnectTimeout, UnsupportedVersion, CancellationToken.None));

        Assert.Equal(2u, ex.EngineMin);
        Assert.Equal(5u, ex.EngineMax);
        Assert.Equal(UnsupportedVersion, ex.SdkVersion);
    }

    [Fact]
    public async Task ReceiveHelloAckAsync_OnAValidAck_SetsNegotiatedProtocolAndEngineVersion()
    {
        // The one branch above that is not a failure: proves the scripted stream drives the
        // ordinary success path too, so the failure-mode tests above it aren't trivially true of
        // any stream that merely throws before reaching Accept.
        var ack = new TrackResponse
        {
            HelloAck = new HelloAck { NegotiatedProtocolVersion = 1, EngineVersion = "1.2.3" },
        };
        await using var session = new TrackSession(FakeCall(ScriptedResponses.Replaying(ack)));

        await session.ReceiveHelloAckAsync(ConnectTimeout, sdkVersion: 1, CancellationToken.None);

        Assert.Equal(1u, session.NegotiatedProtocol);
        Assert.Equal("1.2.3", session.EngineVersion);
    }

    private static AsyncDuplexStreamingCall<TrackRequest, TrackResponse> FakeCall(
        IAsyncStreamReader<TrackResponse> responses)
        => new(new NullRequestStream(), responses, Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => new Metadata(), () => { });

    /// <summary>A request stream nothing ever reads: <see cref="TrackSession.ReceiveHelloAckAsync"/>
    /// only reads the response stream, and SendHelloAsync is never called by these tests, so this
    /// exists purely to satisfy the call's shape.</summary>
    private sealed class NullRequestStream : IClientStreamWriter<TrackRequest>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(TrackRequest message) => Task.CompletedTask;

        public Task CompleteAsync() => Task.CompletedTask;
    }

    /// <summary>A response stream a test scripts in advance: a fixed sequence of messages then a
    /// clean end, or a fault raised in place of the next message, or a read that never completes on
    /// its own. Covers every shape <see cref="ReceiveHelloAckAsync"/> has to handle without needing
    /// a second implementation per test.</summary>
    private sealed class ScriptedResponses : IAsyncStreamReader<TrackResponse>
    {
        private readonly Queue<TrackResponse> _responses;
        private readonly RpcException? _fault;
        private readonly bool _hangs;

        private ScriptedResponses(IEnumerable<TrackResponse> responses, RpcException? fault, bool hangs)
        {
            _responses = new Queue<TrackResponse>(responses);
            _fault = fault;
            _hangs = hangs;
        }

        /// <summary>Replays the given messages in order, then ends the stream cleanly like a
        /// server that closed the call normally.</summary>
        public static ScriptedResponses Replaying(params TrackResponse[] responses)
            => new(responses, fault: null, hangs: false);

        /// <summary>Fails the very next read with <paramref name="fault"/>, as if the call died
        /// before the engine wrote anything back.</summary>
        public static ScriptedResponses Faulting(RpcException fault)
            => new([], fault, hangs: false);

        /// <summary>Never resolves on its own — models an engine that accepted the stream and then
        /// stopped answering, so only a caller-side deadline or cancellation ends the read.</summary>
        public static ScriptedResponses Hanging() => new([], fault: null, hangs: true);

        public TrackResponse Current { get; private set; } = new();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_hangs)
            {
                // Throws OperationCanceledException once the caller's own token fires; never
                // returns on its own, which is the point.
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return false;
            }

            if (_responses.Count == 0)
            {
                if (_fault is not null)
                    throw _fault;
                return false;
            }

            Current = _responses.Dequeue();
            return true;
        }
    }

    /// <summary>
    /// An engine serving the replay corpus with its scan loop stopped: every test here finishes
    /// during the handshake, so frames would only add time.
    /// </summary>
    private static async Task<StartedEngine> StartEngineAsync(CancellationToken ct)
    {
        var pipeName = NewPipeName();
        var sink = new ConsoleSink();
        var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            new ReplayFrameSource(EngineTestFixtures.ReplayDir), sink, verbose: false);

        await engine.StartAsync(ct);
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
}
