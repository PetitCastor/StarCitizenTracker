using GameCapture.Contracts;

namespace GameCapture.Sdk;

/// <summary>
/// The host's own <see cref="IPluginServices"/>: emit into the run's record list, dump through the
/// live client, log to the run's output.
/// </summary>
/// <remarks>
/// One instance for the whole run, not one per connect, so a plugin may hold on to the reference it
/// is handed. <see cref="Engine"/> is therefore mutable from the host's side — the value changes on
/// every reconnect, which is the point: a plugin that cached
/// <c>ctx.Services.Engine</c> across a reconnect would be reading the version of an engine that has
/// since been replaced by a different build.
/// </remarks>
internal sealed class PluginServices : IPluginServices
{
    private readonly List<CaptureRecord> _records;
    private readonly IPluginOutput _output;
    private readonly bool _verbose;

    /// <summary>
    /// Null when debug dumps are switched off in config, which is the ordinary case. Held as a
    /// delegate rather than as the client so the whole debug path can be absent rather than
    /// conditional at every call site.
    /// </summary>
    private readonly Func<RoiRect?, string, CancellationToken, Task<string?>>? _dumpFrame;

    /// <summary>
    /// Null only in tests that never exercise the calibration read. Not gated on the debug-frames
    /// setting the way <see cref="_dumpFrame"/> is: this one writes nothing, so there is nothing to
    /// switch off.
    /// </summary>
    private readonly Func<RoiSubscription, CancellationToken, Task<OcrRegionResult?>>? _readRoi;

    /// <summary>
    /// Null in the ordinary console run, where the printed summary is the only thing that reads
    /// <see cref="_records"/>. Set by <see cref="PluginHostOptions.RecordSink"/> for an embedding
    /// host — the replay harness, primarily — that needs the records themselves rather than a tally.
    /// </summary>
    private readonly Action<CaptureRecord>? _recordSink;

    public PluginServices(List<CaptureRecord> records, IPluginOutput output, bool verbose,
        Func<RoiRect?, string, CancellationToken, Task<string?>>? dumpFrame,
        Func<RoiSubscription, CancellationToken, Task<OcrRegionResult?>>? readRoi = null,
        Action<CaptureRecord>? recordSink = null)
    {
        _records = records;
        _output = output;
        _verbose = verbose;
        _dumpFrame = dumpFrame;
        _readRoi = readRoi;
        _recordSink = recordSink;
    }

    /// <summary>
    /// What the host last connected to. Set before <see cref="SessionEvent.Connected"/> is raised,
    /// so a plugin reading it from inside that handler sees the new engine, not the old one.
    /// </summary>
    public EngineInfo Engine { get; internal set; } = new("", 0, 0, 0, ReplayMode: false,
        OcrLanguage: "", ConnectedClients: [], ScanInterval: EngineDefaults.DefaultScanInterval);

    public void Emit(CaptureRecord record)
    {
        _records.Add(record);
        _recordSink?.Invoke(record);

        // One output call per capture: each WriteLine erases/redraws the status bar, so five
        // separate calls would flicker it five times per tracker event.
        _output.WriteLine(string.Join(Environment.NewLine,
            "",
            $"===== {record.Plugin} capture ({record.Trigger}) at {record.Timestamp:HH:mm:ss.fff} =====",
            record.RawText,
            "=====================================================",
            ""));
    }

    public Task<string?> DumpFrameAsync(RoiRect? roi, string prefix, CancellationToken ct)
        => _dumpFrame?.Invoke(roi, prefix, ct) ?? Task.FromResult<string?>(null);

    public Task<OcrRegionResult?> ReadRoiAsync(RoiSubscription roi, CancellationToken ct)
        => _readRoi?.Invoke(roi, ct) ?? Task.FromResult<OcrRegionResult?>(null);

    public void Log(string message) => _output.WriteLine(message);

    public void LogVerbose(string message)
    {
        if (_verbose)
            _output.WriteLine(message);
    }
}
