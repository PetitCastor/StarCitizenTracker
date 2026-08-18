using GameCapture.Contracts;
using Grpc.Core;

namespace GameCapture.Sdk;

/// <summary>
/// Runs a plugin: loads its config, parses the shared command line, connects, subscribes, feeds it
/// ticks, reconnects when the engine goes away, and prints a summary on the way out.
/// </summary>
/// <remarks>
/// This is the ~90 lines both existing plugins carried as a verbatim copy of each other, comments
/// included (<c>MissionPlugin/Program.cs</c> and <c>RefineryRunner.cs</c>). The comments came along,
/// because every one of them documents a trap that was hit for real: what an
/// <see cref="OperationCanceledException"/> means depending on who cancelled, why the engine wait
/// needs a finite budget, why a reconnect has to be paced, why one bad tick must not end a run.
/// </remarks>
public static class GameCapturePluginHost
{
    /// <summary>
    /// Runs until the tick stream ends normally (replay finished, or the engine shut down), Ctrl+C,
    /// or a failure a reconnect cannot fix.
    /// </summary>
    /// <returns>
    /// 0 for every orderly ending, including Ctrl+C — a plugin stopped on purpose did not fail. 1 for
    /// a usage error and for a protocol mismatch, which are the two endings a supervisor should not
    /// simply restart.
    /// </returns>
    public static async Task<int> RunAsync(IGameCapturePlugin plugin, string[] args,
        PluginHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(args);

        options ??= new PluginHostOptions();

        // The host owns a ConsoleSink only when nobody handed it an output. Constructed first so
        // every later write goes through it and disposal (status-bar erase, cursor restore) is
        // guaranteed on every return path — the reason both Program.cs files opened with this line.
        var ownedSink = options.Output is null ? new ConsoleSink() : null;
        var output = options.Output ?? ownedSink!;

        try
        {
            return await RunCoreAsync(plugin, args, options, output);
        }
        finally
        {
            ownedSink?.Dispose();
        }
    }

    private static async Task<int> RunCoreAsync(IGameCapturePlugin plugin, string[] args,
        PluginHostOptions options, IPluginOutput output)
    {
        output.WriteLine($"=== GameCapture — {plugin.Name} ===");

        if (options.ExtraArgHandler?.Invoke(args) is { } extraError)
        {
            Console.Error.WriteLine(extraError);
            return 1;
        }

        var config = LoadConfig(options);

        var parsed = PluginArgs.Parse(args, config.PipeName, out var usageError);
        if (parsed is null)
        {
            Console.Error.WriteLine(usageError);
            return 1;
        }

        var records = new List<CaptureRecord>();

        // Linked rather than the caller's token directly: the Ctrl+C handler below has to be able to
        // cancel, and an embedding host's token must cancel this run too.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(options.ShutdownToken);
        var ct = cts.Token;

        ConsoleCancelEventHandler? cancelHandler = null;
        if (options.HandleCancelKeyPress)
        {
            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
        }

        try
        {
            using var client = new CaptureClient(parsed.PipeName);

            // Debug dumps are the engine's to write — the frame never crosses the boundary, only the
            // path it was written to. Null switches the whole debug path off inside the plugin.
            var services = new PluginServices(records, output, parsed.Verbose,
                config.SaveDebugFrames ? client.DumpFrameAsync : null,
                client.ReadRoiAsync, options.RecordSink);

            output.WriteLine($"Pipe:      {parsed.PipeName}");
            output.WriteLine($"Debug:     {(config.SaveDebugFrames
                ? "asking the engine for a PNG per capture"
                : "in-memory only, no files")}");
            output.WriteLine();

            var (reason, exitCode) = await RunSessionsAsync(plugin, client, services, output, options,
                parsed.PipeName, ct);

            plugin.OnSessionEvent(new SessionEvent.Ended(reason));

            WriteSummary(plugin, records, output);
            return exitCode;
        }
        finally
        {
            if (cancelHandler is not null)
                Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// The connect / subscribe / consume loop, with its reconnect and shutdown rules. Returns why it
    /// stopped and what the process should exit with.
    /// </summary>
    private static async Task<(StreamEndReason Reason, int ExitCode)> RunSessionsAsync(
        IGameCapturePlugin plugin, CaptureClient client, PluginServices services, IPluginOutput output,
        PluginHostOptions options, string pipeName, CancellationToken ct)
    {
        var rois = plugin.Rois;
        var dispatcher = new TickDispatcher(plugin, services, output);

        // Announced once per disconnected stretch rather than per retry: a plugin started before the
        // engine would otherwise scroll the same line every few seconds.
        var announcedWait = false;
        var reconnectAttempt = 0;
        var replayMode = false;

        while (true)
        {
            if (!announcedWait)
            {
                output.WriteLine($"waiting for engine on pipe '{pipeName}'...");
                announcedWait = true;
            }

            try
            {
                // Inner try purely to re-type transport failures: every arm below is written against
                // the SDK's own exceptions, never RpcException. gRPC status codes are a detail of the
                // current boundary, and a host that switched on them would have to be rewritten if
                // the boundary ever changed.
                try
                {
                    var engine = await client.WaitForEngineAsync(options.EngineWait, ct);
                    announcedWait = false;
                    replayMode = engine.ReplayMode;

                    await using var session = await client.TrackAsync(plugin.Name, rois, ct);

                    services.Engine = engine.WithSession(session);
                    dispatcher.OnConnected();
                    reconnectAttempt = 0;
                    plugin.OnSessionEvent(new SessionEvent.Connected(services.Engine));

                    WriteConnectedBanner(services.Engine, rois, output);

                    await foreach (var tick in session.Ticks(ct))
                        await dispatcher.DispatchAsync(tick, ct);
                }
                catch (RpcException e)
                {
                    // Deliberately unfiltered by cancellation: a call this host cancelled surfaces as
                    // an OperationCanceledException (the channel sets
                    // ThrowOperationCanceledOnCancellation), so what reaches here during shutdown is
                    // only the write that was already in flight on the request stream. Translate maps
                    // its CANCELLED to SessionFaultedException, and the cancellation-filtered arm
                    // below takes it — the same "not an engine failure" the RpcException arm used to
                    // express.
                    throw ProtocolNegotiation.Translate(e, ProtocolVersion.Current);
                }
            }
            catch (ProtocolMismatchException ex)
            {
                // The one failure the loop must NOT retry. Both sides' versions are fixed for the
                // life of their processes, so dialling again can only reproduce this forever — and
                // the useful thing to tell the user is which side to upgrade, which the message says.
                //
                // FIRST, and specifically ahead of the cancellation-filtered arms below: this derives
                // from GameCaptureException, and catch clauses match in textual order, so placing it
                // after them would let a shutdown racing the refusal report an incompatible engine as
                // a clean exit 0. Cancellation cannot manufacture this exception — it needs either the
                // engine's FAILED_PRECONDITION with range trailers or the range check in
                // WaitForEngineAsync — so whenever it is raised it is the true reason the run ended.
                output.WriteLine($"incompatible engine: {ex.Message}");
                return (StreamEndReason.Faulted, 1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // our own Ctrl+C: the channel maps a cancelled call to this, not RpcException
                return (StreamEndReason.Cancelled, 0);
            }
            catch (GameCaptureException) when (ct.IsCancellationRequested)
            {
                // Ctrl+C again: the channel's OCE mapping covers the call, but a write already in
                // flight on the request stream can still surface as CANCELLED. Not an engine failure.
                return (StreamEndReason.Cancelled, 0);
            }
            catch (TimeoutException)
            {
                continue; // engine still not serving; the line above already says we are waiting
            }
            catch (Exception ex) when (ex is GameCaptureException or OperationCanceledException)
            {
                // The engine went away mid-session. Reconnecting means a fresh subscription, and the
                // plugin's state is deliberately kept: what it already saw is still true, and the
                // first read after reconnect is a re-sighting rather than a new event.
                //
                // OperationCanceledException lands here too, and only because ct did NOT cause it:
                // the channel sets ThrowOperationCanceledOnCancellation, which maps a call the ENGINE
                // cancelled (a restart aborting the in-flight Track with CANCELLED) to the same
                // exception type as our own Ctrl+C. Caught unfiltered, that would exit the plugin 0
                // on exactly the failure this loop exists to survive.
                output.WriteLine("engine connection lost — reconnecting");
                plugin.OnSessionEvent(new SessionEvent.Reconnecting(++reconnectAttempt));

                // Paced: WaitForEngineAsync returns immediately whenever GetStatus answers, so an
                // engine that is up but cannot serve a Track stream (mid-shutdown, for one) would
                // otherwise spin this loop with no delay at all.
                try { await Task.Delay(options.ReconnectDelay, ct); }
                catch (OperationCanceledException) { return (StreamEndReason.Cancelled, 0); }

                continue;
            }

            // Stream ended normally. Which of the two endings it was is the engine's to know: a
            // replay that finished is a completed run, a live engine completing the stream is a
            // shutdown, and a plugin that persists anything usually cares about the difference.
            return (replayMode ? StreamEndReason.ReplayCompleted : StreamEndReason.EngineShutdown, 0);
        }
    }

    private static void WriteConnectedBanner(EngineInfo engine, IReadOnlyList<RoiSubscription> rois,
        IPluginOutput output)
    {
        output.WriteLine($"Engine:    {engine.EngineVersion}{(engine.ReplayMode ? " (replay)" : "")}");
        output.WriteLine($"Frame:     {(engine.FrameWidth == 0
            ? "no frame scanned yet"
            : $"{engine.FrameWidth}x{engine.FrameHeight}")}");
        output.WriteLine($"Cadence:   {engine.ScanInterval.TotalMilliseconds:0} ms per scan");
        output.WriteLine($"ROIs:      {string.Join(", ", rois.Select(r => r.Id))}");
        output.WriteLine();
        output.WriteLine("Running. Ctrl+C to quit.");
        output.WriteLine();
    }

    private static void WriteSummary(IGameCapturePlugin plugin, List<CaptureRecord> records,
        IPluginOutput output)
    {
        output.WriteLine();
        output.WriteLine($"=== Summary: {records.Count} captures ===");
        foreach (var g in records.GroupBy(r => (r.Plugin, r.Trigger)))
            output.WriteLine($"  {g.Key.Plugin} ({g.Key.Trigger}): {g.Count()}");

        // After the host's own lines, so a plugin's totals read as an elaboration of them rather
        // than as a competing summary.
        foreach (var line in plugin.SummaryLines())
            output.WriteLine(line);
    }

    /// <summary>
    /// The config the host itself needs. A plugin with settings of its own loads them with
    /// <see cref="PluginConfig.Load{T}"/> and passes the instance through
    /// <see cref="PluginHostOptions.Config"/>; the host then does not touch the file, because that
    /// load already wrote the defaults on a first run.
    /// </summary>
    private static PluginConfig LoadConfig(PluginHostOptions options)
    {
        if (options.Config is { } supplied)
            return supplied;

        if (options.ConfigFileName is not { Length: > 0 } fileName)
            return new HostPluginConfig();

        return PluginConfig.Load<HostPluginConfig>(
            Path.Combine(AppContext.BaseDirectory, fileName));
    }

    /// <summary>The base settings and nothing else, for a plugin that needs no settings of its own.</summary>
    private sealed class HostPluginConfig : PluginConfig;
}
