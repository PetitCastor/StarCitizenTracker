# GameCapture.Sdk.Testing

The testing companion to `GameCapture.Sdk`, in the shape of `Microsoft.AspNetCore.Mvc.Testing`: a
public surface for driving a plugin under test, so a plugin in its own repository needs no
`InternalsVisibleTo` from the SDK.

Two layers. `TickDataBuilder` and `FakePluginServices` cover unit tests — no engine, no OCR, no
game. `ReplayHarness` covers parity: it spawns a real `GameCapture.Engine.exe` replaying a PNG corpus and
drives the plugin through its real `GameCapturePluginHost` path, which is what a plugin's CI runs.

## Install

```powershell
dotnet add package GameCapture.Sdk.Testing
```

> Not on nuget.org yet (TASK-16/17). Until then, reference
> `src/GameCapture.Sdk.Testing/GameCapture.Sdk.Testing.csproj` from a clone.

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

`EngineLocator.Resolve()` honours `GAMECAPTURE_ENGINE_PATH` and otherwise finds the newest local build.
Corpus layout and capture: [`docs/REPLAY.md`](https://github.com/PetitCastor/StarCitizenTracker/blob/master/docs/REPLAY.md).
