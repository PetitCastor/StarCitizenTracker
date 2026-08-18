namespace GameCapture.Engine;

/// <summary>
/// Single point of console output: scrolling log lines plus one live status row pinned
/// at the bottom. Log writes erase the status row, print normally, then redraw the
/// status below — one lock makes the erase/write/redraw sequence atomic across the
/// tracker-scan thread and the metrics-timer thread. When output is redirected (CI,
/// piped to a file) the status bar is disabled and everything degrades to plain
/// scrolling writes.
/// </summary>
/// <remarks>
/// DELIBERATE FORK of <c>GameCapture.Sdk.ConsoleSink</c>, not an oversight, and not a candidate for
/// de-duplication. The SDK owns the plugin-facing copy because a plugin's host has to write
/// somewhere; the engine keeps this one because after the repo split (SOW-10/11) the engine is a
/// separate repository that must not take a dependency on the plugin SDK package — the dependency
/// arrow points SDK → contracts, never engine → SDK. A console status bar is UI, not protocol, so
/// there is nothing here worth a third shared package. The two copies are expected to drift.
/// <para>
/// Namespace is <c>GameCapture.Engine</c> rather than <c>GameCapture.Engine.Core</c> so every engine file and
/// the engine's test project resolve <c>ConsoleSink</c> from their own enclosing namespace, ahead
/// of any <c>using GameCapture.Sdk;</c> at the top of the file. Moving it would make the SDK's copy
/// ambiguous in the test project, which references both.
/// </para>
/// </remarks>
public sealed class ConsoleSink : IDisposable
{
    private readonly Lock _gate = new();
    private readonly bool _interactive = !Console.IsOutputRedirected;
    private string _statusText = "";
    private bool _statusDrawn;
    private bool _disposed;

    public ConsoleSink()
    {
        if (_interactive)
            try { Console.CursorVisible = false; } catch (IOException) { }
    }

    public void WriteLine(string message = "")
    {
        lock (_gate)
        {
            if (!_interactive || _disposed)
            {
                Console.WriteLine(message);
                return;
            }

            EraseStatus();
            Console.WriteLine(message);
            DrawStatus();
        }
    }

    public void UpdateStatus(string statusText)
    {
        lock (_gate)
        {
            if (_disposed || !_interactive)
                return;

            _statusText = statusText;
            EraseStatus();
            DrawStatus();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            if (_interactive)
            {
                EraseStatus();
                try { Console.CursorVisible = true; } catch (IOException) { }
            }
            _disposed = true;
        }
    }

    /// <summary>
    /// Status always occupies exactly one row: truncated to width-1 so it never wraps
    /// and never touches the last column (writing the bottom-right cell auto-scrolls
    /// the buffer). Width &lt;= 1 (some hosts report 0) yields "" — skip drawing.
    /// </summary>
    internal static string FitToWidth(string text, int windowWidth)
    {
        if (windowWidth <= 1)
            return "";

        var oneLine = text.ReplaceLineEndings(" ");
        return oneLine.Length < windowWidth ? oneLine : oneLine[..(windowWidth - 1)];
    }

    private void EraseStatus()
    {
        if (!_statusDrawn)
            return;

        try
        {
            var width = Console.WindowWidth;
            if (width > 1)
            {
                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write(new string(' ', width - 1));
                Console.SetCursorPosition(0, Console.CursorTop);
            }
        }
        catch (Exception e) when (e is IOException or ArgumentOutOfRangeException)
        {
            // Terminal resized/detached mid-operation; next redraw self-corrects.
        }
        _statusDrawn = false;
    }

    private void DrawStatus()
    {
        if (_statusText.Length == 0)
            return;

        try
        {
            var fitted = FitToWidth(_statusText, Console.WindowWidth);
            if (fitted.Length == 0)
                return;

            Console.Write(fitted); // no newline — cursor stays on the status row
            _statusDrawn = true;
        }
        catch (Exception e) when (e is IOException or ArgumentOutOfRangeException)
        {
        }
    }
}
