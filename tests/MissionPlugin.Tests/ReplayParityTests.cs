using GameCapture.Sdk;
using GameCapture.Sdk.Testing;
using Xunit;
using Xunit.Abstractions;

namespace MissionPlugin.Tests;

/// <summary>
/// A spawned engine is a process-wide resource (a named pipe, a Windows OCR engine instance), so
/// running two of these at once would have them competing for both while claiming to measure a
/// deterministic replay. Mirrors RefineryPlugin.Tests's own collection of the same name — xunit
/// collections are per-assembly, so the two definitions don't collide.
/// </summary>
[CollectionDefinition("ReplayParity", DisableParallelization = true)]
public class ReplayParityCollection;

/// <summary>
/// The acceptance gate RefineryPlugin already has (see <c>RefineryPlugin.Tests.ReplayParityTests</c>):
/// the engine replaying a real corpus through the plugin's own <see cref="GameCapturePluginHost"/> path,
/// asserted against what a human capture is known to produce. MissionPlugin never had one — the
/// monolith shipped mission tracking without a corpus to pin it against, so this is new rather than
/// ported.
/// </summary>
[Collection("ReplayParity")]
[Trait("Category", "Integration")]
public class ReplayParityTests(ITestOutputHelper output)
{
    /// <summary>The corpora linked into this assembly's output by the csproj — currently just the
    /// awaited "mission-accept" capture (see the skip reason on the fact below).</summary>
    private const string FixturesRoot = "Fixtures/Replay";

    [Fact(Skip = "awaiting mission corpus — capture via --save-frames, accept one mission, ~5-8 frames")]
    public async Task MissionAccept_corpus_emitsExactlyOneAutoRecord()
    {
        var corpusDir = ReplayCorpus.Resolve(Path.Combine(FixturesRoot, "mission-accept"));
        Assert.True(Directory.Exists(corpusDir), $"corpus not copied to the test output: {corpusDir}");

        // Timeout left at ReplayOptions' own default (5 min) — a handful of frames through real OCR
        // measures in seconds, so anything near it means something is stuck rather than slow.
        var result = await ReplayHarness.RunAsync(new ReplayOptions
        {
            EnginePath = EngineLocator.Resolve(),
            CorpusDir = corpusDir,
            Plugin = new MissionPlugin(),
        });

        output.WriteLine($"exit {result.ExitCode}, reason {result.Reason}, {result.Records.Count} record(s)");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(StreamEndReason.ReplayCompleted, result.Reason);

        // The one mission accepted mid-corpus must produce exactly one Auto record — a manual
        // hotkey press is a separate trigger this corpus does not exercise.
        var record = Assert.Single(result.Records);
        Assert.Equal("missions", record.Plugin);
        Assert.Equal(TriggerKind.Auto, record.Trigger);
        Assert.NotEmpty(record.RawText);
    }
}
