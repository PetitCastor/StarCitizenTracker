using System.Diagnostics;
using TrackerSdk;
using TrackerSdk.Testing;
using Xunit;
using Xunit.Abstractions;

namespace CaptureEngine.Tests;

/// <summary>
/// <see cref="ReplayHarness"/> against a real, separately spawned <c>CaptureEngine.exe</c> — the
/// exact mechanism a plugin's own CI uses, as opposed to <see cref="ReplayParityTests"/> and
/// <see cref="PluginHostIntegrationTests"/>, which host the engine in-proc.
/// </summary>
/// <remarks>
/// Shares <see cref="ReplayParityCollection"/> with the other engine-hosting suites: a spawned
/// engine still binds the same kind of OS resources (a named pipe, a Windows OCR engine instance)
/// those tests compete for, just from a second process instead of a second in-proc host.
/// </remarks>
[Collection("ReplayParity")]
[Trait("Category", "Integration")]
public class ReplayHarnessTests(ITestOutputHelper output)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    private static string AbsoluteFixture(string relative) => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, relative));

    [Fact]
    public async Task SmokeCorpus_DispatchesEveryTickAndEndsWithReplayCompleted()
    {
        var enginePath = EngineLocator.Resolve();
        var frameCount = ReplayFrameSource.EnumerateCorpus(EngineTestFixtures.ReplayDir).Length;
        var plugin = new NullPlugin();

        var result = await ReplayHarness.RunAsync(new ReplayOptions
        {
            EnginePath = enginePath,
            CorpusDir = AbsoluteFixture(EngineTestFixtures.ReplayDir),
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
        // CaptureEngine/Program.cs), so its stderr — captured in the ring buffer — says exactly why,
        // and that has to survive into the exception for CI to be debuggable from the message alone.
        Assert.Contains("Replay directory not found", ex.Message);
    }

    [Fact]
    public async Task AfterATimedOutRun_NoOrphanedEngineProcessRemains()
    {
        var enginePath = EngineLocator.Resolve();
        var missingCorpus = Path.Combine(Path.GetTempPath(), $"sc-replay-missing-{Guid.NewGuid():N}");
        var before = Process.GetProcessesByName("CaptureEngine").Length;

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
        while (Process.GetProcessesByName("CaptureEngine").Length > before)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, cts.Token);
        }

        Assert.Equal(before, Process.GetProcessesByName("CaptureEngine").Length);
    }
}
