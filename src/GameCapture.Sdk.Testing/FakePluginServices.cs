using GameCapture.Contracts;

namespace GameCapture.Sdk.Testing;

/// <summary>
/// An in-memory <see cref="IPluginServices"/> for a plugin's own unit tests: <see cref="Emit"/>
/// records rather than prints, <see cref="DumpFrameAsync"/> stubs a path under the OS temp
/// directory rather than touching a real engine, and <see cref="Log"/>/<see cref="LogVerbose"/>
/// capture lines a test can assert on instead of writing to a console no two parallel tests could
/// share.
/// </summary>
public sealed class FakePluginServices : IPluginServices
{
    private readonly List<string> _logs = [];
    private readonly List<string> _verboseLogs = [];

    /// <summary>Every record a plugin under test handed to <see cref="Emit"/>, in order.</summary>
    public List<CaptureRecord> Emitted { get; } = [];

    /// <summary>Every line written through <see cref="Log"/>.</summary>
    public IReadOnlyList<string> Logs => _logs;

    /// <summary>Every line written through <see cref="LogVerbose"/> — captured regardless of a
    /// verbose flag, since there is no run configuration here to gate it on.</summary>
    public IReadOnlyList<string> VerboseLogs => _verboseLogs;

    /// <summary>What the plugin under test is told the engine is. Settable: a test asserting
    /// engine-version or scan-interval branches needs to shape this before running the plugin.</summary>
    public EngineInfo Engine { get; set; } = new(
        EngineVersion: "test-engine",
        NegotiatedProtocol: 1,
        FrameWidth: EngineDefaults.ReferenceWidth,
        FrameHeight: EngineDefaults.ReferenceHeight,
        ReplayMode: false,
        OcrLanguage: "en",
        ConnectedClients: [],
        ScanInterval: EngineDefaults.DefaultScanInterval);

    /// <summary>Overrides the stubbed <see cref="DumpFrameAsync"/> response. Null (the default)
    /// hands back a fabricated temp path without touching disk, matching debug dumps being switched
    /// off — the ordinary case for a unit test.</summary>
    public Func<RoiRect?, string, CancellationToken, Task<string?>>? DumpFrameHandler { get; set; }

    /// <summary>Overrides the stubbed <see cref="ReadRoiAsync"/> response. Null (the default)
    /// answers every calibration read with null, matching services built without a live client.</summary>
    public Func<RoiSubscription, CancellationToken, Task<OcrRegionResult?>>? ReadRoiHandler { get; set; }

    public void Emit(CaptureRecord record) => Emitted.Add(record);

    public Task<string?> DumpFrameAsync(RoiRect? roi, string prefix, CancellationToken ct)
    {
        if (DumpFrameHandler is not null)
            return DumpFrameHandler(roi, prefix, ct);

        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.png");
        return Task.FromResult<string?>(path);
    }

    public Task<OcrRegionResult?> ReadRoiAsync(RoiSubscription roi, CancellationToken ct)
        => ReadRoiHandler?.Invoke(roi, ct) ?? Task.FromResult<OcrRegionResult?>(null);

    public void Log(string message) => _logs.Add(message);

    public void LogVerbose(string message) => _verboseLogs.Add(message);
}
