using Xunit;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// The host's behaviour that needs no engine: what it does with a bad command line, and what it does
/// when it is told to stop. Everything that requires a real session lives in
/// <c>GameCapture.Engine.Tests.PluginHostIntegrationTests</c>, the only project that can own both ends of
/// the pipe.
/// </summary>
public class GameCapturePluginHostTests
{
    /// <summary>
    /// A hang bound, not a budget. Every test here either fails a command line (instant) or cancels
    /// a wait that would otherwise last a day.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Config loading off and the console left alone: these tests run inside a test host, where
    /// writing a config.json next to the test assembly is litter and grabbing Ctrl+C would take the
    /// runner's own interrupt.
    /// </summary>
    private static PluginHostOptions Options(RecordingOutput output, CancellationToken shutdown = default)
        => new()
        {
            Output = output,
            ConfigFileName = null,
            HandleCancelKeyPress = false,
            ShutdownToken = shutdown,
            ReconnectDelay = TimeSpan.FromMilliseconds(10),
        };

    [Fact]
    public async Task PipeFlagWithoutAValue_ExitsOneWithoutConnecting()
    {
        var output = new RecordingOutput();
        var plugin = new StubPlugin();

        var exit = await GameCapturePluginHost.RunAsync(plugin, ["--pipe"], Options(output));

        Assert.Equal(1, exit);

        // Nothing past the banner: a usage error must not reach the point of dialling a pipe, and
        // must not print a summary for a run that never happened.
        Assert.DoesNotContain("waiting for engine", output.Text);
        Assert.DoesNotContain("Summary", output.Text);
        Assert.Empty(plugin.Events);
    }

    [Fact]
    public async Task BlankPipeName_ExitsOne()
    {
        var output = new RecordingOutput();

        var exit = await GameCapturePluginHost.RunAsync(new StubPlugin(), ["--pipe", "   "],
            Options(output));

        Assert.Equal(1, exit);
    }

    /// <summary>
    /// The plugin's own flags are validated before the host commits to anything — the point of the
    /// hook being handed the whole command line rather than one token at a time.
    /// </summary>
    [Fact]
    public async Task ExtraArgHandlerReturningAnError_ExitsOne()
    {
        var output = new RecordingOutput();
        var options = new PluginHostOptions
        {
            Output = output,
            ConfigFileName = null,
            HandleCancelKeyPress = false,
            ExtraArgHandler = args => args.Contains("--ledger") ? null : "--ledger is required.",
        };

        var exit = await GameCapturePluginHost.RunAsync(new StubPlugin(), [], options);

        Assert.Equal(1, exit);
        Assert.DoesNotContain("waiting for engine", output.Text);
    }

    [Fact]
    public async Task ExtraArgHandlerReturningNull_LetsTheRunProceed()
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var output = new RecordingOutput();

        var seen = new List<string>();
        var options = new PluginHostOptions
        {
            Output = output,
            ConfigFileName = null,
            HandleCancelKeyPress = false,
            ShutdownToken = shutdown.Token,
            ExtraArgHandler = args => { seen.AddRange(args); return null; },
        };

        var exit = await GameCapturePluginHost.RunAsync(new StubPlugin(), ["--pipe", DeadPipe()], options)
            .WaitAsync(TestTimeout);

        Assert.Equal(0, exit);
        Assert.Equal(["--pipe"], seen[..1]);
        Assert.Contains("waiting for engine", output.Text);
    }

    /// <summary>
    /// Ctrl+C, as the tests can raise it: the handler the host installs does nothing but cancel this
    /// same token, so cancelling it directly drives the identical path without the test process
    /// interrupting itself.
    /// </summary>
    [Fact]
    public async Task Cancellation_ExitsZeroAndStillPrintsTheSummary()
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var output = new RecordingOutput();
        var plugin = new StubPlugin();
        plugin.Summary.Add("Ledger: 0 orders");

        var exit = await GameCapturePluginHost
            .RunAsync(plugin, ["--pipe", DeadPipe()], Options(output, shutdown.Token))
            .WaitAsync(TestTimeout);

        // Zero: a plugin stopped on purpose did not fail.
        Assert.Equal(0, exit);
        Assert.Contains("=== Summary: 0 captures ===", output.Lines);

        // The plugin's own lines land under the host's, not instead of them.
        Assert.Contains("Ledger: 0 orders", output.Lines);

        var ended = Assert.IsType<SessionEvent.Ended>(Assert.Single(plugin.Events));
        Assert.Equal(StreamEndReason.Cancelled, ended.Reason);
    }

    /// <summary>
    /// Cancelled before the first dial. The summary still has to be printed: a plugin's records are
    /// in memory, and skipping the summary on a fast shutdown would lose them for good.
    /// </summary>
    [Fact]
    public async Task CancellationBeforeTheRunStarts_StillExitsZeroWithASummary()
    {
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();

        var output = new RecordingOutput();

        var exit = await GameCapturePluginHost
            .RunAsync(new StubPlugin(), ["--pipe", DeadPipe()], Options(output, shutdown.Token))
            .WaitAsync(TestTimeout);

        Assert.Equal(0, exit);
        Assert.Contains("=== Summary: 0 captures ===", output.Lines);
    }

    /// <summary>
    /// Announced once per disconnected stretch rather than per retry: a plugin started before the
    /// engine would otherwise scroll the same line every few seconds.
    /// </summary>
    [Fact]
    public async Task WaitingForAnAbsentEngine_AnnouncesItselfOnlyOnce()
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var output = new RecordingOutput();

        await GameCapturePluginHost
            .RunAsync(new StubPlugin(), ["--pipe", DeadPipe()], Options(output, shutdown.Token))
            .WaitAsync(TestTimeout);

        Assert.Single(output.Lines, l => l.StartsWith("waiting for engine"));
    }

    [Fact]
    public async Task Banner_NamesThePluginAndThePipe()
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var output = new RecordingOutput();
        var pipe = DeadPipe();

        await GameCapturePluginHost
            .RunAsync(new StubPlugin { Name = "refinery" }, ["--pipe", pipe],
                Options(output, shutdown.Token))
            .WaitAsync(TestTimeout);

        Assert.Contains("=== GameCapture — refinery ===", output.Lines);
        Assert.Contains($"Pipe:      {pipe}", output.Lines);
        Assert.Contains("in-memory only, no files", output.Text);
    }

    /// <summary>
    /// The host's own config wiring, which every other test here switches off. Proves the default
    /// path actually resolves a file next to the plugin and runs the loader through it — a
    /// <c>LoadConfig</c> that looked in the wrong directory, or skipped the write, would otherwise
    /// only be caught once a real plugin shipped without a discoverable config.json.
    /// </summary>
    [Fact]
    public async Task WithNoConfigFile_TheHostWritesOneWithTheDefaults()
    {
        // A unique name in the test assembly's own directory: AppContext.BaseDirectory is where the
        // host looks, and it is not a knob.
        var fileName = $"host-cfg-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, fileName);

        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var output = new RecordingOutput();

            var exit = await GameCapturePluginHost.RunAsync(new StubPlugin(), ["--pipe", DeadPipe()],
                new PluginHostOptions
                {
                    Output = output,
                    ConfigFileName = fileName,
                    HandleCancelKeyPress = false,
                    ShutdownToken = shutdown.Token,
                }).WaitAsync(TestTimeout);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(path), $"the host did not write {path}");

            var written = File.ReadAllText(path);
            Assert.Contains("\"pipeName\"", written);
            Assert.Contains("\"saveDebugFrames\"", written);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// A plugin whose settings extend <see cref="PluginConfig"/> loads them itself and hands the
    /// instance over; the host must then read ITS values and leave the file alone, because that load
    /// already wrote the defaults on a first run.
    /// </summary>
    [Fact]
    public async Task WithASuppliedConfig_TheHostUsesItAndWritesNothing()
    {
        var fileName = $"host-cfg-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, fileName);

        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var output = new RecordingOutput();
            var pipe = DeadPipe();

            var exit = await GameCapturePluginHost.RunAsync(new StubPlugin(), [],
                new PluginHostOptions
                {
                    Output = output,
                    // Both set: the supplied config must win, and the file name must be ignored.
                    Config = new SuppliedConfig { PipeName = pipe, SaveDebugFrames = true },
                    ConfigFileName = fileName,
                    HandleCancelKeyPress = false,
                    ShutdownToken = shutdown.Token,
                }).WaitAsync(TestTimeout);

            Assert.Equal(0, exit);
            Assert.False(File.Exists(path), "the host wrote a config file it was handed one for");

            // Its PipeName reached the connect, and its SaveDebugFrames reached the banner.
            Assert.Contains($"waiting for engine on pipe '{pipe}'...", output.Lines);
            Assert.Contains("asking the engine for a PNG per capture", output.Text);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private sealed class SuppliedConfig : PluginConfig;

    [Fact]
    public async Task NullOptions_AreAccepted()
    {
        // Exercises the default-construction path, which production takes: a usage error is the one
        // ending reachable without an engine or a config file.
        Assert.Equal(1, await GameCapturePluginHost.RunAsync(new StubPlugin(), ["--pipe"]));
    }

    [Fact]
    public async Task NullPlugin_Throws()
        => await Assert.ThrowsAsync<ArgumentNullException>(
            () => GameCapturePluginHost.RunAsync(null!, []));

    /// <summary>A pipe name nothing is listening on, unique per test so two never collide.</summary>
    private static string DeadPipe() => $"sc-host-{Guid.NewGuid():N}";
}
