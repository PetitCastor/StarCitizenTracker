using GameCapture.Contracts.Proto;
using Xunit;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// The status message becoming the SDK's own value. Worth its own tests because two of the fields
/// are not copies: the cadence has a fallback for an engine too old to report it, and the version
/// has a rule about which of two sources wins.
/// </summary>
public class EngineInfoTests
{
    [Fact]
    public void From_CopiesWhatTheEngineReported()
    {
        var status = new StatusResponse
        {
            EngineVersion = "1.4.2",
            FrameWidth = 3840,
            FrameHeight = 2160,
            ReplayMode = true,
            OcrLanguage = "en-US",
            ScanIntervalMs = 250,
        };
        status.ConnectedClients.Add("refinery");
        status.ConnectedClients.Add("mission");

        var info = EngineInfo.From(status);

        Assert.Equal("1.4.2", info.EngineVersion);
        Assert.Equal(3840, info.FrameWidth);
        Assert.Equal(2160, info.FrameHeight);
        Assert.True(info.ReplayMode);
        Assert.Equal("en-US", info.OcrLanguage);
        Assert.Equal(["refinery", "mission"], info.ConnectedClients);
        Assert.Equal(TimeSpan.FromMilliseconds(250), info.ScanInterval);
    }

    /// <summary>
    /// Nothing session-scoped is known from a status read alone, and claiming a negotiated version
    /// that was never negotiated is exactly what the handshake refuses elsewhere.
    /// </summary>
    [Fact]
    public void From_WithoutASession_ReportsNoNegotiatedProtocol()
        => Assert.Equal(0u, EngineInfo.From(new StatusResponse()).NegotiatedProtocol);

    /// <summary>
    /// An engine older than <c>scan_interval_ms</c> sends the proto3 default, and 0 ms as a cadence
    /// would have a plugin conclude every debounce had already elapsed.
    /// </summary>
    [Fact]
    public void From_AgainstAnEngineThatDoesNotReportItsCadence_FallsBackToTheDefault()
        => Assert.Equal(EngineDefaults.DefaultScanInterval,
            EngineInfo.From(new StatusResponse()).ScanInterval);

    [Fact]
    public void ConnectedClients_IsACopy_NotTheProtoCollection()
    {
        var status = new StatusResponse();
        status.ConnectedClients.Add("refinery");

        var info = EngineInfo.From(status);
        status.ConnectedClients.Add("mission");

        // A plugin holding an EngineInfo must not see a later mutation of the message it came from.
        Assert.Equal(["refinery"], info.ConnectedClients);
    }
}
