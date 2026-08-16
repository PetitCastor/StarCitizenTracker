using CaptureContracts;
using Common;
using Grpc.Core;
using TrackerSdk;
using Xunit;

namespace CaptureEngine.Tests;

/// <summary>
/// The SDK's half of version negotiation (MATURITY TASK-05). The engine's half has its own suite;
/// what is under test here is the client — that it announces a version, reports what came back, and
/// turns both kinds of refusal into an SDK exception rather than an <see cref="RpcException"/> the
/// plugin would have to decode.
/// </summary>
/// <remarks>
/// The mismatch cases drive a real engine over a real pipe, using the client's internal version
/// seam. A stub server would be easier and would prove less: the trailers the rejection is
/// recognised by are written by the engine, and a stub asserting against trailers a test wrote
/// itself could not tell a working translation from two copies of the same mistake.
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
