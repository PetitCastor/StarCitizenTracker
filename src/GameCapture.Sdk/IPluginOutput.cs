namespace GameCapture.Sdk;

/// <summary>
/// Where a plugin host writes. <see cref="ConsoleSink"/> is the implementation a console plugin
/// gets by default; the interface exists so the same host loop can run somewhere that is not a
/// console.
/// </summary>
/// <remarks>
/// Two callers justify it today. Tests need to read back what the host printed — the summary and
/// the reconnect lines are behaviour this task has to assert, and asserting them through the real
/// <see cref="Console"/> would mean a process-global <see cref="Console.SetOut"/> that no two tests
/// could hold at once. And the tray app (a later phase) hosts a plugin inside a GUI process, where
/// a status bar drawn with cursor moves is not merely useless but corrupting.
/// </remarks>
public interface IPluginOutput
{
    /// <summary>Appends a scrolling log line.</summary>
    void WriteLine(string message = "");

    /// <summary>
    /// Replaces the single live status row. Implementations without a status concept may ignore it;
    /// nothing load-bearing may be written here, because that is exactly what a console does with
    /// it when its output is redirected.
    /// </summary>
    void UpdateStatus(string statusText);
}
