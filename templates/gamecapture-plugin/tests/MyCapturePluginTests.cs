using GameCapture.Sdk;
using GameCapture.Sdk.Testing;
using Xunit;

namespace MyCapturePlugin.Tests;

// Named independently of the project, like CounterPlugin itself: sourceName substitution would
// otherwise splice a dotted project name (`-n Acme.MyPlugin`) straight into this declaration.
public class CounterPluginTests
{
    private static TickContext Tick(TickData tick, FakePluginServices services)
        => TickContext.ForTesting(tick, services);

    [Fact]
    public async Task Emits_once_per_change()
    {
        var plugin = new CounterPlugin();
        var services = new FakePluginServices();

        await plugin.OnTickAsync(Tick(new TickDataBuilder().Text("counter", "3/8").Build(), services), default);
        await plugin.OnTickAsync(Tick(new TickDataBuilder().Text("counter", "3/8").Build(), services), default);
        await plugin.OnTickAsync(Tick(new TickDataBuilder().Text("counter", "4/8").Build(), services), default);

        Assert.Equal(["3/8", "4/8"], services.Emitted.Select(r => r.RawText));
    }

    [Fact]
    public async Task Failed_roi_emits_nothing()
    {
        var plugin = new CounterPlugin();
        var services = new FakePluginServices();
        var tick = new TickDataBuilder().Errored("counter", "region outside frame").Build();

        await plugin.OnTickAsync(Tick(tick, services), default);

        Assert.Empty(services.Emitted);
        Assert.Equal(RoiStatus.Failed, tick.Status("counter"));
    }
}

/// <summary>
/// A spawned engine owns a named pipe and a Windows OCR instance, so two of these must never run
/// at once. This is what actually serializes them — the <c>[Collection]</c> attribute on
/// <see cref="ReplayParityTests"/> alone only groups; without <c>DisableParallelization</c> the
/// group still runs beside every other collection in the assembly. Keep this even with only one
/// test below: it is the thing that stops the second test you add here from racing the first one
/// for the same pipe.
/// </summary>
[CollectionDefinition("ReplayParity", DisableParallelization = true)]
public class ReplayParityCollection;

[Collection("ReplayParity")]
public class ReplayParityTests
{
    /// <summary>
    /// Parity smoke test: spawns a real GameCapture.Engine.exe replaying a PNG corpus and drives
    /// this plugin through its real GameCapturePluginHost path — public SDK plus an engine binary,
    /// no in-proc shortcuts. Skipped until you have both a corpus and an engine to point at; see
    /// the calibration workflow in README.md and docs/REPLAY.md for how to capture one, then:
    ///   1. Copy the captured PNGs into Fixtures/Replay/my-corpus/ and add them to this csproj:
    ///      &lt;None Include="Fixtures\Replay\my-corpus\**\*.png" CopyToOutputDirectory="PreserveNewest" /&gt;
    ///   2. Point GAMECAPTURE_ENGINE_PATH at the engine you built or unpacked.
    ///   3. Remove the Skip.
    /// </summary>
    [Fact(Skip = "needs corpus + GAMECAPTURE_ENGINE_PATH")]
    [Trait("Category", "Integration")]
    public async Task Corpus_emits_one_record()
    {
        var corpusDir = ReplayCorpus.Resolve("Fixtures/Replay/my-corpus");
        Assert.True(Directory.Exists(corpusDir), $"corpus not copied to the test output: {corpusDir}");

        var result = await ReplayHarness.RunAsync(new ReplayOptions
        {
            EnginePath = EngineLocator.Resolve(),
            CorpusDir = corpusDir,
            Plugin = new CounterPlugin(),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(StreamEndReason.ReplayCompleted, result.Reason);

        var record = Assert.Single(result.Records);
        Assert.Equal("MyCapturePlugin", record.Plugin);
        Assert.Equal(TriggerKind.Auto, record.Trigger);
    }
}
