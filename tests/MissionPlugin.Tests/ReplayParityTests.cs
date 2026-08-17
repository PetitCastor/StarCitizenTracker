using TrackerSdk;
using TrackerSdk.Testing;
using Xunit;
using Xunit.Abstractions;

namespace MissionPlugin.Tests;

/// <summary>
/// The acceptance gate RefineryPlugin already has (see <c>RefineryPlugin.Tests.ReplayParityTests</c>):
/// the engine replaying a real corpus through the plugin's own <see cref="TrackerPluginHost"/> path,
/// asserted against what a human capture is known to produce. MissionPlugin never had one — the
/// monolith shipped mission tracking without a corpus to pin it against, so this is new rather than
/// ported.
/// </summary>
[Trait("Category", "Integration")]
public class ReplayParityTests(ITestOutputHelper output)
{
    /// <summary>Not captured yet — see the skip reason on the fact below.</summary>
    private const string FixturesRoot = "Fixtures/Replay";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    private static string AbsoluteFixture(string relative) => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, relative));

    [Fact(Skip = "awaiting mission corpus — capture via --save-frames, accept one mission, ~5-8 frames")]
    public async Task MissionAccept_corpus_emitsExactlyOneAutoRecord()
    {
        var corpusDir = AbsoluteFixture(Path.Combine(FixturesRoot, "mission-accept"));
        Assert.True(Directory.Exists(corpusDir), $"corpus not copied to the test output: {corpusDir}");

        var result = await ReplayHarness.RunAsync(new ReplayOptions
        {
            EnginePath = EngineLocator.Resolve(),
            CorpusDir = corpusDir,
            Plugin = new MissionPlugin(),
            Timeout = TestTimeout,
        });

        output.WriteLine($"exit {result.ExitCode}, reason {result.Reason}, {result.Records.Count} record(s)");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(StreamEndReason.ReplayCompleted, result.Reason);

        // The one mission accepted mid-corpus must produce exactly one Auto record — a manual
        // hotkey press is a separate trigger this corpus does not exercise.
        var record = Assert.Single(result.Records);
        Assert.Equal("missions", record.Tracker);
        Assert.Equal(TriggerKind.Auto, record.Trigger);
        Assert.NotEmpty(record.RawText);
    }
}
