using System.ComponentModel;
using System.Diagnostics;

namespace GameCapture.Sdk.Testing;

/// <summary>
/// Runs a plugin against a real, spawned <c>GameCapture.Engine.exe</c> replaying a PNG corpus — the exact
/// mechanism a plugin's own CI uses for parity: public SDK plus an engine binary, no in-proc
/// shortcuts and no <c>InternalsVisibleTo</c> reaching across the engine/plugin boundary.
/// </summary>
public static class ReplayHarness
{
    /// <summary>How long to give the engine process to exit on its own once the plugin host has
    /// returned, before this is treated as a hang the caller's <see cref="ReplayOptions.Timeout"/>
    /// did not catch and the process tree is killed anyway.</summary>
    private static readonly TimeSpan EngineExitGrace = TimeSpan.FromSeconds(10);

    public static async Task<ReplayResult> RunAsync(ReplayOptions o, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(o);

        var pipe = $"gamecapture-test-{Guid.NewGuid():N}";
        var outputTail = new OutputTail();

        using var engine = new Process
        {
            StartInfo = new ProcessStartInfo(o.EnginePath)
            {
                ArgumentList = { "--replay", o.CorpusDir, "--pipe", pipe },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        engine.OutputDataReceived += (_, e) => outputTail.Add(e.Data);
        engine.ErrorDataReceived += (_, e) => outputTail.Add(e.Data);

        try
        {
            engine.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"failed to start engine process at '{o.EnginePath}'", ex);
        }
        engine.BeginOutputReadLine();
        engine.BeginErrorReadLine();

        try
        {
            var records = new List<CaptureRecord>();
            var recorder = new EndReasonRecorder(o.Plugin);

            // Linked rather than passed to WaitAsync directly: the caller's ct and an internal
            // timeout both need to make the host shut down gracefully (StreamEndReason.Cancelled),
            // never race against WaitAsync's own OperationCanceledException/TimeoutException.
            using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var options = new PluginHostOptions
            {
                // No config.json next to a test assembly, and no Ctrl+C handler stealing the test
                // runner's own interrupt — the same two overrides the host's own integration tests use.
                ConfigFileName = null,
                HandleCancelKeyPress = false,
                ShutdownToken = shutdownCts.Token,
                RecordSink = records.Add,
            };

            var hostTask = GameCapturePluginHost.RunAsync(recorder, ["--pipe", pipe], options);

            int exit;
            try
            {
                // WaitAsync rather than folding o.Timeout into ShutdownToken: a cancelled
                // ShutdownToken makes the host return gracefully (StreamEndReason.Cancelled, exit 0),
                // which is indistinguishable from a real Ctrl+C. A hang has to surface as a thrown
                // TimeoutException instead, so the caller (and CI) can tell the two apart.
                exit = await hostTask.WaitAsync(o.Timeout, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                // Don't leave hostTask running detached: ask the host to shut down and give it a
                // bounded grace period, mirroring the wait for the engine process below. Best-effort
                // — the process kill in the finally block is what actually guarantees cleanup.
                shutdownCts.Cancel();
                try { await hostTask.WaitAsync(EngineExitGrace, CancellationToken.None); }
                catch { /* the TimeoutException below is what the caller sees */ }

                throw new TimeoutException(
                    $"ReplayHarness: no result within {o.Timeout} " +
                    $"(engine '{o.EnginePath}', corpus '{o.CorpusDir}')." + Environment.NewLine +
                    "--- last engine output ---" + Environment.NewLine +
                    outputTail.Text);
            }

            // The engine exits on its own once the corpus is exhausted (see GameCapture.Engine/Program.cs)
            // — this is a bounded wait for that, not a shutdown request.
            using var exitWait = new CancellationTokenSource(EngineExitGrace);
            try { await engine.WaitForExitAsync(exitWait.Token); }
            catch (OperationCanceledException) { /* the finally below kills the tree */ }

            var reason = recorder.EndReason ?? throw new InvalidOperationException(
                $"ReplayHarness: the plugin host returned (exit {exit}) without ever raising " +
                "SessionEvent.Ended — likely a usage error logged to the host's own Console.Error." +
                Environment.NewLine + "--- last engine output ---" + Environment.NewLine +
                outputTail.Text);

            return new ReplayResult(records, exit, reason);
        }
        finally
        {
            // No orphaned GameCapture.Engine.exe on any exit path — a failed/timed-out run must not leave
            // a process holding the OCR engine and the pipe name for the next test to trip over.
            // HasExited-then-Kill is inherently racy (the process can exit in between), so this must
            // not let a Kill failure mask an exception already propagating out of the try block.
            try
            {
                if (!engine.HasExited)
                    engine.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { /* already exited */ }
            catch (Win32Exception) { /* already exited/exiting */ }
        }
    }

    /// <summary>Forwards every <see cref="IGameCapturePlugin"/> member to <paramref name="inner"/>
    /// unchanged, except for capturing the reason a run ended — the one thing
    /// <see cref="GameCapturePluginHost"/> tells the plugin but never hands back to its caller.</summary>
    private sealed class EndReasonRecorder(IGameCapturePlugin inner) : IGameCapturePlugin
    {
        public StreamEndReason? EndReason { get; private set; }

        public string Name => inner.Name;
        public IReadOnlyList<RoiSubscription> Rois => inner.Rois;
        public RoiErrorPolicy ErrorPolicy => inner.ErrorPolicy;

        public Task OnTickAsync(TickContext ctx, CancellationToken ct) => inner.OnTickAsync(ctx, ct);
        public Task OnManualTickAsync(TickContext ctx, CancellationToken ct) => inner.OnManualTickAsync(ctx, ct);

        public void OnSessionEvent(SessionEvent evt)
        {
            if (evt is SessionEvent.Ended ended)
                EndReason = ended.Reason;
            inner.OnSessionEvent(evt);
        }

        public IEnumerable<string> SummaryLines() => inner.SummaryLines();
    }

    /// <summary>The last 50 non-null lines written to the engine's stdout/stderr, for a timeout
    /// exception's message. A ring buffer rather than an ever-growing list: a corpus-less replay
    /// that spins retrying a connection could otherwise write for the entire <see
    /// cref="ReplayOptions.Timeout"/> before this is ever read.</summary>
    private sealed class OutputTail
    {
        private const int Capacity = 50;
        private readonly Queue<string> _lines = new(Capacity);
        private readonly Lock _gate = new();

        // Process.OutputDataReceived/ErrorDataReceived fire on ThreadPool threads, potentially both
        // at once, so appends need a lock even though every reader of this type is single-threaded.
        public void Add(string? line)
        {
            if (line is null)
                return;
            lock (_gate)
            {
                if (_lines.Count == Capacity)
                    _lines.Dequeue();
                _lines.Enqueue(line);
            }
        }

        public string Text
        {
            get { lock (_gate) return string.Join(Environment.NewLine, _lines); }
        }
    }
}
