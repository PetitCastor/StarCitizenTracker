namespace GameCapture.Sdk;

/// <summary>
/// The knobs on <see cref="GameCapturePluginHost.RunAsync"/>. Every one has a default that is right for
/// a console plugin, so passing none is the normal case.
/// </summary>
public sealed class PluginHostOptions
{
    /// <summary>
    /// Breathing room between a lost session and the next dial. Paced on purpose:
    /// <see cref="CaptureClient.WaitForEngineAsync"/> returns immediately whenever GetStatus
    /// answers, so an engine that is up but cannot serve a Track stream (mid-shutdown, for one)
    /// would otherwise spin the reconnect loop with no delay at all.
    /// </summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Config file to load from the plugin's own directory, or null to skip loading entirely and run
    /// on defaults plus command line. Ignored when <see cref="Config"/> is supplied.
    /// </summary>
    public string? ConfigFileName { get; init; } = "config.json";

    /// <summary>
    /// An already-loaded config, for a plugin whose settings extend
    /// <see cref="PluginConfig"/>. Null — the ordinary case — means the host loads
    /// <see cref="ConfigFileName"/> itself and reads only the base settings from it.
    /// </summary>
    /// <remarks>
    /// The host cannot load a derived config on the plugin's behalf: <see cref="PluginConfig.Load{T}"/>
    /// needs the concrete type, and the plugin is the only party that knows it — and needs the typed
    /// instance back anyway, to read the settings the host does not care about. So the plugin loads,
    /// keeps its instance, and hands the same object here for the host's own two fields. The host
    /// then does not touch the file, because that load already wrote the defaults on a first run.
    /// </remarks>
    public PluginConfig? Config { get; init; }

    /// <summary>
    /// Handed the full command line before the host acts on anything. Return an error message to
    /// abort the run with exit code 1 and that message on stderr, or null to proceed.
    /// </summary>
    /// <remarks>
    /// The task sketch typed this <c>Func&lt;string, string&gt;</c>, one argument at a time. It is
    /// the whole list here because the case it names — Refinery's <c>--ledger &lt;path&gt;</c> — is a
    /// flag plus a value plus three distinct usage errors, one of which ("needs a file path after
    /// it") can only be detected by looking past the end of the list. A per-token callback could
    /// only express it by keeping state between calls, which is a parser with extra steps.
    /// <para>
    /// The host never consumes what it does not recognise, so a handler is free to read flags the
    /// host also reads. It runs before the config is loaded, so what it captures is available by the
    /// time the plugin is constructed.
    /// </para>
    /// </remarks>
    public Func<IReadOnlyList<string>, string?>? ExtraArgHandler { get; init; }

    /// <summary>
    /// Where the host writes. Null means it owns a <see cref="ConsoleSink"/> for the duration of the
    /// run — the ordinary case, and the one that guarantees the status bar is erased and the cursor
    /// restored on every return path. Pass one to host a plugin somewhere that is not a console; the
    /// host then does not dispose it, because it did not create it.
    /// </summary>
    public IPluginOutput? Output { get; init; }

    /// <summary>
    /// An outer stop signal, folded together with the host's own Ctrl+C handling. Cancelling it ends
    /// the run exactly as Ctrl+C does: summary printed, exit code 0.
    /// </summary>
    /// <remarks>
    /// For an embedding process that owns the lifetime — the tray app, and the tests, which cannot
    /// raise a real console interrupt against themselves.
    /// </remarks>
    public CancellationToken ShutdownToken { get; init; } = CancellationToken.None;

    /// <summary>
    /// Install the <see cref="Console.CancelKeyPress"/> handler. True for a console process; false
    /// when something else owns the console and this plugin is merely running inside it, where
    /// grabbing Ctrl+C would stop more than the plugin.
    /// </summary>
    public bool HandleCancelKeyPress { get; init; } = true;

    /// <summary>
    /// How long to wait for an engine that is not up yet. <see cref="CaptureClient.WaitForEngineAsync"/>
    /// needs a finite budget: <see cref="Timeout.InfiniteTimeSpan"/> is negative and would go
    /// straight to its timeout branch, and <see cref="TimeSpan.MaxValue"/> overflows the RPC
    /// deadline. A day is "forever" for a plugin left running — the loop retries anyway, and
    /// cancellation, not this, is what ends the wait.
    /// </summary>
    public TimeSpan EngineWait { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Tees every emitted <see cref="CaptureRecord"/> here, in addition to the run's own summary.
    /// Null — the ordinary case — means only the printed summary counts them.
    /// </summary>
    /// <remarks>
    /// For an embedding host that needs the records themselves rather than a printed tally — the
    /// replay harness, primarily, which hands them back as part of its result to a test that asserts
    /// on what was captured rather than on the summary line.
    /// </remarks>
    public Action<CaptureRecord>? RecordSink { get; init; }
}
