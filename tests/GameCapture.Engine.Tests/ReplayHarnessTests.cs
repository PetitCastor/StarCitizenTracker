using System.Diagnostics;
using GameCapture.Sdk;
using GameCapture.Sdk.Testing;
using Xunit;
using Xunit.Abstractions;

namespace GameCapture.Engine.Tests;

/// <summary>
/// A spawned engine is a process-wide resource (a named pipe, a Windows OCR engine instance), so
/// running two of these at once would have them competing for both while claiming to measure a
/// deterministic replay. RefineryPlugin.Tests's own replay-parity suite (moved out in TASK-13)
/// defines an identically-named collection in its own assembly for the same reason — xunit
/// collections are per-assembly, so the two definitions don't collide, and equally neither
/// serializes against the other (nothing here relies on cross-assembly ordering).
/// </summary>
[CollectionDefinition("ReplayParity", DisableParallelization = true)]
public class ReplayParityCollection;

/// <summary>
/// <see cref="ReplayHarness"/> against a real, separately spawned <c>GameCapture.Engine.exe</c> — the
/// exact mechanism a plugin's own CI uses, as opposed to <see cref="PluginHostIntegrationTests"/>,
/// which hosts the engine in-proc. This is also the engine suite's one thin replay-harness smoke
/// (<c>NullPlugin</c> + a minimal corpus): proves the engine side of the harness without any
/// plugin logic, now that the full replay-parity suites live with their plugins.
/// </summary>
[Collection("ReplayParity")]
[Trait("Category", "Integration")]
public class ReplayHarnessTests(ITestOutputHelper output)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task SmokeCorpus_DispatchesEveryTickAndEndsWithReplayCompleted()
    {
        var enginePath = EngineLocator.Resolve();
        var frameCount = ReplayFrameSource.EnumerateCorpus(EngineTestFixtures.ReplayDir).Length;
        var plugin = new NullPlugin();

        var result = await ReplayHarness.RunAsync(new ReplayOptions
        {
            EnginePath = enginePath,
            CorpusDir = ReplayCorpus.Resolve(EngineTestFixtures.ReplayDir),
            Plugin = plugin,
            Timeout = TestTimeout,
        });

        output.WriteLine($"{frameCount} frame(s) replayed, {plugin.TickCount} tick(s) dispatched, " +
            $"{result.Records.Count} record(s), exit {result.ExitCode}, reason {result.Reason}");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(StreamEndReason.ReplayCompleted, result.Reason);

        // Non-empty first: an empty corpus would satisfy the equality below while proving nothing.
        Assert.NotEqual(0, frameCount);
        Assert.Equal(frameCount, plugin.TickCount);

        // NullPlugin never calls IPluginServices.Emit, so the tee has nothing to report — the point
        // of this assertion is that RecordSink was wired at all rather than throwing on the way.
        Assert.Empty(result.Records);
    }

    [Fact]
    public async Task WhenTheEngineNeverComesUp_ThrowsTimeoutExceptionNamingTheFailure()
    {
        var enginePath = EngineLocator.Resolve();
        var missingCorpus = Path.Combine(Path.GetTempPath(), $"sc-replay-missing-{Guid.NewGuid():N}");

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => ReplayHarness.RunAsync(new ReplayOptions
        {
            EnginePath = enginePath,
            CorpusDir = missingCorpus,
            Plugin = new NullPlugin(),
            Timeout = TimeSpan.FromSeconds(10),
        }));

        output.WriteLine(ex.Message);

        // The engine refuses to start at all against a corpus dir that does not exist (see
        // GameCapture.Engine/Program.cs), so its stderr — captured in the ring buffer — says exactly why,
        // and that has to survive into the exception for CI to be debuggable from the message alone.
        Assert.Contains("Replay directory not found", ex.Message);
    }

    [Fact]
    public async Task AfterATimedOutRun_NoOrphanedEngineProcessRemains()
    {
        var enginePath = EngineLocator.Resolve();
        var missingCorpus = Path.Combine(Path.GetTempPath(), $"sc-replay-missing-{Guid.NewGuid():N}");
        var before = Process.GetProcessesByName("GameCapture.Engine").Length;

        await Assert.ThrowsAsync<TimeoutException>(() => ReplayHarness.RunAsync(new ReplayOptions
        {
            EnginePath = enginePath,
            CorpusDir = missingCorpus,
            Plugin = new NullPlugin(),
            Timeout = TimeSpan.FromSeconds(10),
        }));

        // Polled rather than asserted immediately: the OS can take a moment to drop a killed process
        // from the table, and a fixed sleep would either be flaky on a loaded box or slow on an idle
        // one.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Process.GetProcessesByName("GameCapture.Engine").Length > before)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, cts.Token);
        }

        Assert.Equal(before, Process.GetProcessesByName("GameCapture.Engine").Length);
    }
}
