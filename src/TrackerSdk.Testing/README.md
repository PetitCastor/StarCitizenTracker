# SCTracker.Sdk.Testing

The testing companion to `SCTracker.Sdk`, in the shape of `Microsoft.AspNetCore.Mvc.Testing`: a
public surface for driving a plugin under test, so a plugin in its own repository needs no
`InternalsVisibleTo` from the SDK.

Two layers. `TickDataBuilder` and `FakePluginServices` cover unit tests — no engine, no OCR, no
game. `ReplayHarness` covers parity: it spawns a real `CaptureEngine.exe` replaying a PNG corpus and
drives the plugin through its real `TrackerPluginHost` path, which is what a plugin's CI runs.

## Install

```powershell
dotnet add package SCTracker.Sdk.Testing
```

## Unit test

```csharp
var plugin = new CounterPlugin();
var services = new FakePluginServices();
var tick = new TickDataBuilder().Text("counter", "4/8").Build();

await plugin.OnTickAsync(TickContext.ForTesting(tick, services), default);

Assert.Equal("4/8", Assert.Single(services.Emitted).RawText);
```

The builder produces a tick the way the engine would have sent it — through the SDK's own wire
mapping — so a tick that could never arrive on the wire cannot pass a test. It covers `.Text`,
`.Detailed`, `.Pixels`, `.Errored`, plus `.Manual()`, `.FrameSeq(n)`, and `.At(instant)`.

## Parity test

```csharp
var result = await ReplayHarness.RunAsync(new ReplayOptions
{
    EnginePath = EngineLocator.Resolve(),
    CorpusDir = ReplayCorpus.Resolve("Fixtures/Replay/my-corpus"),
    Plugin = new CounterPlugin(),
});

Assert.Equal(StreamEndReason.ReplayCompleted, result.Reason);
```

`EngineLocator.Resolve()` honours `SCTRACKER_ENGINE_PATH` and otherwise finds the newest local build.
Corpus layout and capture: [`docs/REPLAY.md`](https://github.com/PetitCastor/StarCitizenTracker/blob/master/docs/REPLAY.md).
