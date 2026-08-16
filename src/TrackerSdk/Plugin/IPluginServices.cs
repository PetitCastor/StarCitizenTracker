using CaptureContracts;

namespace TrackerSdk;

/// <summary>
/// Everything the host lends a plugin for the duration of a tick: where to emit, how to ask for a
/// debug frame, what the engine is, and where to log.
/// </summary>
/// <remarks>
/// An interface rather than a set of constructor callbacks because this is the seam a plugin's unit
/// tests substitute. Before it existed, a plugin's logic took an <c>Action&lt;TrackerRecord&gt;</c>,
/// a <see cref="ConsoleSink"/> and a <c>Func&lt;RoiRect?, string, Task&lt;string?&gt;&gt;</c> as
/// four separate parameters, and every test had to restate all four.
/// </remarks>
public interface IPluginServices
{
    /// <summary>
    /// Records one captured event. The host both keeps it for the end-of-run summary and prints it,
    /// which is why a plugin must not print the same thing itself.
    /// </summary>
    void Emit(TrackerRecord record);

    /// <summary>
    /// Asks the engine to save its most recent frame as a PNG and hands back the absolute path, or
    /// null if the engine has not scanned a frame yet — or if debug dumps are switched off, which is
    /// the ordinary case. A null <paramref name="roi"/> dumps the whole frame.
    /// </summary>
    /// <remarks>
    /// The frame itself never crosses the process boundary; only the path the engine wrote it to
    /// does. That is what keeps a plugin from becoming a screenshot pipeline.
    /// </remarks>
    Task<string?> DumpFrameAsync(RoiRect? roi, string prefix, CancellationToken ct);

    /// <summary>What is on the other end, as of the current connect.</summary>
    EngineInfo Engine { get; }

    /// <summary>Writes a line the user is meant to see.</summary>
    void Log(string message);

    /// <summary>
    /// Writes a line only when the run was started with <c>--verbose</c>. A no-op otherwise, so a
    /// plugin may call it on every tick.
    /// </summary>
    void LogVerbose(string message);
}
