# Writing a tracker plugin

A plugin is a console process that says what regions of the screen it cares about and what to do
when a tick carrying them arrives. It never captures, never runs OCR, and never speaks gRPC:
`GameCapturePluginHost` owns connecting, subscribing, reconnecting, cancellation, and the end-of-run
summary, and hands the plugin one `TickContext` at a time.

This is the golden path, in order. Every code block below is taken from a project that was built
and tested outside this repository against the real SDK — see [§8](#8-cold-start-checklist).

- [1. Prerequisites](#1-prerequisites)
- [2. Creating the project](#2-creating-the-project)
- [3. Anatomy of a plugin](#3-anatomy-of-a-plugin)
- [4. ROIs: space, scale, calibration](#4-rois-space-scale-calibration)
- [5. Error handling](#5-error-handling)
- [6. Session events](#6-session-events)
- [7. Testing](#7-testing)
- [8. Cold-start checklist](#8-cold-start-checklist)
- [9. Config, CLI, and compatibility](#9-config-cli-and-compatibility)
- [Appendix: manual project setup](#appendix-manual-project-setup)

## 1. Prerequisites

- **.NET 10 SDK.** Every project in the system targets `net10.0`; only the engine adds the Windows
  flavor (`net10.0-windows10.0.22621.0`), and a plugin must never need it — see the TFM constraint
  in [`ARCHITECTURE.md`](ARCHITECTURE.md#frozen-constraints).
- **An engine to talk to.** Either a release zip
  ([Releases](https://github.com/PetitCastor/StarCitizenTracker/releases) ships
  `GameCapture.Engine-vX.Y.Z-win-x64.zip`, a self-contained exe) or a clone built with
  `dotnet build GameCapture.slnx`. Running the engine live needs Windows 10/11 with an OCR
  language pack installed; replaying a corpus needs the same, but no game. Note where the exe lands
  — parity tests need `GAMECAPTURE_ENGINE_PATH` pointed at it ([§7](#7-testing)).
- **A clone of this repository, or a local pack of it** until the SDK is on nuget.org (TASK-21/22).
  The template ([§2](#2-creating-the-project)) references `GameCapture.Sdk`, `GameCapture.Contracts`,
  and `GameCapture.Sdk.Testing` by package ID already — a local feed (or, later, nuget.org) supplies
  them either way.

Writing and unit-testing a plugin needs none of the above beyond the SDK — no Windows OCR, no game,
no engine process. That is the point of the plain-`net10.0` boundary.

## 2. Creating the project

```powershell
# once GameCapture.Plugin.Template has a stable release on nuget.org (TASK-21/22):
dotnet new install GameCapture.Plugin.Template
dotnet new gamecapture-plugin -n MyPlugin
```

This scaffolds the whole project: `MyPlugin.csproj`, `Program.cs`, `Rois.cs`, `MyPlugin.cs` (the
class to rename and fill in — [§3](#3-anatomy-of-a-plugin) picks up from here), `config.json`, a
`tests/` project wired against `GameCapture.Sdk.Testing` ([§7](#7-testing)), and a CI workflow stub.
`dotnet new gamecapture-plugin -h` lists every symbol, including `--SdkVersion` for pinning a
specific `GameCapture.Sdk`/`.Contracts`/`.Sdk.Testing` version.

Until then, install and instantiate from a local feed instead — pack the four projects, add the feed
as a source scoped to the new project (not the machine-wide config `dotnet nuget add source` mutates
without `--configfile`), and pin the instantiated project at that exact prerelease with
`--SdkVersion`:

```powershell
dotnet pack src/GameCapture.Contracts -c Release -o feed
dotnet pack src/GameCapture.Sdk -c Release -o feed
dotnet pack src/GameCapture.Sdk.Testing -c Release -o feed
dotnet pack templates/GameCapture.Plugin.Template.csproj -c Release -o feed

dotnet new install feed/GameCapture.Plugin.Template.*.nupkg
dotnet new gamecapture-plugin -n MyPlugin --SdkVersion <version from feed/GameCapture.Sdk.*.nupkg>

dotnet new nugetconfig -o MyPlugin
dotnet nuget add source <full path to feed> --name local --configfile MyPlugin/nuget.config
```

`.github/workflows/ci.yml`'s `template-guard` job runs this same recipe on every PR (the anti-rot
guard for the template itself: instantiate, build, test); read it for the working detail, including
the `GameCapture.Sdk.Testing` name collision to filter out of the `GameCapture.Sdk.*.nupkg` glob.

Setting the project up by hand — no template package available — is
[Appendix: manual project setup](#appendix-manual-project-setup).

## 3. Anatomy of a plugin

`IGameCapturePlugin` (`src/GameCapture.Sdk/Plugin/IGameCapturePlugin.cs`) has three required members — `Name`,
`Rois`, `OnTickAsync` — and four with defaults. The example below reads one text region, remembers
the last value, and emits a record whenever it changes.

The ROI set first, in its own file. It is static for the life of the process because the host reads
it once per connect and sends it as the initial subscription: per-tick atomicity means there is no
mid-tick round-trip that could add a region later.

```csharp
using GameCapture.Contracts;
using GameCapture.Sdk;

namespace MyPlugin;

/// <summary>
/// The regions this plugin subscribes, in reference space (2560x1440).
/// </summary>
public static class Rois
{
    /// <summary>The panel line the counter lives on. Scale is the OCR upscale factor —
    /// small text needs 2-4; 0 means "engine default".</summary>
    public static readonly RoiSubscription Counter =
        new("counter", new RoiRect(1000, 110, 420, 100), 3.0, RoiKind.Text);

    /// <summary>A field, not <c>=> [Counter]</c>: the set never changes, and an
    /// expression-bodied property would build a fresh array on every read.</summary>
    public static readonly IReadOnlyList<RoiSubscription> All = [Counter];
}
```

The plugin itself:

```csharp
using GameCapture.Sdk;

namespace MyPlugin;

/// <summary>
/// Watches one region for a counter and emits a record every time the value changes.
/// </summary>
public sealed class CounterPlugin : IGameCapturePlugin
{
    private string? _last;

    /// <summary>The client name on the Track stream and the tag on every record emitted.</summary>
    public string Name => "counter";

    public IReadOnlyList<RoiSubscription> Rois => MyPlugin.Rois.All;

    /// <summary>Default. The host skips any tick in which a subscribed region failed, so
    /// nothing below ever reads a degraded value.</summary>
    public RoiErrorPolicy ErrorPolicy => RoiErrorPolicy.AbortTick;

    public Task OnTickAsync(TickContext ctx, CancellationToken ct)
    {
        // TryGetText, not Text: a failed region and a genuinely blank panel both answer "",
        // and only the bool tells them apart.
        if (!ctx.Tick.TryGetText(MyPlugin.Rois.Counter.Id, out var text))
            return Task.CompletedTask;

        var value = text.Trim();
        if (value.Length == 0 || value == _last)
            return Task.CompletedTask;

        _last = value;

        // The tick's own timestamp, not DateTime.Now: the engine buffers a few ticks per
        // client, so processing time can trail the frame it describes.
        ctx.Services.Emit(new CaptureRecord(ctx.Tick.Timestamp, Name, TriggerKind.Auto, value));
        return Task.CompletedTask;
    }

    /// <summary>The hotkey means "capture what is on screen right now" here, so the current
    /// reading is emitted whether or not it changed.</summary>
    public Task OnManualTickAsync(TickContext ctx, CancellationToken ct)
    {
        if (!ctx.Tick.TryGetText(MyPlugin.Rois.Counter.Id, out var text))
            return Task.CompletedTask;

        var value = text.Trim();
        if (value.Length == 0)
            return Task.CompletedTask;

        // Advance the same state the auto path keeps. Without this, a press on a value that
        // has not been seen yet emits it as Manual and the very next tick emits it again as
        // Auto — one screen, two records.
        _last = value;

        ctx.Services.Emit(new CaptureRecord(ctx.Tick.Timestamp, Name, TriggerKind.Manual, value));
        return Task.CompletedTask;
    }

    /// <summary>Frames this plugin never saw. A tracker watching for an edge can miss it
    /// across a gap, so the next reading is re-reported as a fresh sighting rather than
    /// assumed to be the successor of the last one. A reconnect is NOT in here: the host
    /// deliberately keeps plugin state across one — see §6.</summary>
    public void OnSessionEvent(SessionEvent evt)
    {
        if (evt is SessionEvent.TicksDropped)
            _last = null;
    }

    public IEnumerable<string> SummaryLines() => [$"  last counter: {_last ?? "none"}"];
}
```

Members, and what each one is for:

| Member | Required | Notes |
| --- | --- | --- |
| `Name` | yes | Client name on the Track stream (what the engine lists in `GetStatus`, and what a user sees when two plugins share an engine) *and* the `CaptureRecord.Plugin` tag on everything emitted. |
| `Rois` | yes | Complete before the first tick; see [§4](#4-rois-space-scale-calibration). |
| `OnTickAsync` | yes | One scanned frame. Called sequentially — the host never overlaps two ticks — so plugin state needs no locking. Throwing does **not** end the run: the host logs it and delivers the next tick, because one unparseable frame out of thousands is normal. |
| `ErrorPolicy` | no (`AbortTick`) | See [§5](#5-error-handling). |
| `OnManualTickAsync` | no (→ `OnTickAsync`) | The tick on which the engine hotkey fired. Override when the hotkey means something other than "the normal capture, forced". |
| `OnSessionEvent` | no (no-op) | Connected / Reconnecting / TicksDropped / Ended. Runs on the host's loop — must not block. See [§6](#6-session-events). |
| `SummaryLines` | no (empty) | Extra lines printed under the host's own end-of-run summary. |

What the plugin gets back, through `ctx.Services` (`IPluginServices`):

- `Emit(CaptureRecord)` — records one captured event. The host both keeps it for the summary and
  prints it, so a plugin must not print the same thing itself.
- `Log` / `LogVerbose` — user-visible lines; `LogVerbose` is a no-op unless the run was started with
  `--verbose`, so it is safe to call on every tick.
- `DumpFrameAsync(roi, prefix, ct)` / `ReadRoiAsync(roi, ct)` — calibration aids, [§4](#4-rois-space-scale-calibration).
- `Engine` — an `EngineInfo`: version, negotiated protocol, frame size, OCR language, connected
  clients, `ScanInterval`, and `ReplayMode`. **A plugin that writes anywhere persistent must branch
  on `ReplayMode`** — a corpus run must not append to a real ledger.

## 4. ROIs: space, scale, calibration

**Reference space is 2560x1440, always.** A ROI is declared against that grid and the *engine*
scales it to whatever resolution is actually being captured (`RoiScaler.ToFrame`), echoing the
frame-space rect it really read back on the result. A plugin never rescales a rect the engine
reports. This is what keeps a ROI table valid across monitors, and it is a frozen constraint, not a
convenience ([`ARCHITECTURE.md`](ARCHITECTURE.md#frozen-constraints)).

A region that cannot touch the frame at all — origin past the frame edge, or zero width/height — is
rejected as a per-ROI error rather than clamped to a meaningless sliver.

**Choosing `Kind`** (`RoiKind`, and the fuller table in
[`ENGINE-SERVICES.md`](ENGINE-SERVICES.md#roi-kinds)):

| Kind | Read it with | Use for |
| --- | --- | --- |
| `Text` | `TryGetText` | Panels where only the text matters — status lines, single-value readouts. |
| `Detailed` | `TryGetOcr` (per-word boxes) | Table-shaped UI where column position decides which value belongs to which row. |
| `Pixels` | `TryGetPixels` → `PixelPatchSampler` | Small colour probes — a toggle's state by its fill colour. Never for text. |

**Choosing `Scale`.** It is the OCR upscale factor and applies to `Text`/`Detailed` only; `0` (or
less) means "engine default", `1.0`. Small UI text usually needs 2-4 — the shipped plugins use 3.0
for a tab counter and 2.0 for a large pane. The engine clamps the request so the upscaled crop stays
under the Windows OCR maximum dimension and reports what it actually applied on
`RoiResult.effective_scale`, so asking for 8.0 on a large region silently gets less; size the region
down instead of the scale up.

**Sizing a `Pixels` region.** Its BGRA payload must stay under `EngineDefaults.MaxPixelBytes` or the
region fails with a per-ROI error — a colour probe should be a strip a few pixels tall, not a panel.
The budget is checked on the **frame-space** rect, after the engine has scaled the reference rect to
the actual capture resolution, so a probe that fits comfortably at 2560x1440 can still blow the cap
on a 4K screen. Size it against the largest resolution the plugin should support, not the reference.
Channel order is `EngineDefaults.PixelChannelOrder` (`BGRA`), and a red/blue swap produces perfectly
plausible wrong answers, so write colour predicates against B, G, R in that order.

**Calibrating.** Both aids read the engine's *most recently scanned* frame, not a fresh capture:

- `ReadRoiAsync(roi, ct)` runs the same read path a live tick uses against a candidate rectangle, so
  a calibration read behaves exactly as a subscribed ROI would have on that frame. Returns `null`
  when the engine has not scanned anything yet. **Nothing a plugin acts on may come from here** — it
  is a second round-trip and may land on a different frame than the tick in hand, which is the
  cross-frame mixing `TickData` exists to prevent. It throws `RoiResultException` if the engine
  flagged the region, or if it is a `Pixels` subscription (no OCR to return).
- `DumpFrameAsync(roi, prefix, ct)` asks the engine to write a PNG — full frame when `roi` is null,
  otherwise a crop through the same scaling path — and hands back the absolute path *on the engine's
  machine*. The frame itself never crosses the boundary. It returns `null` when the engine has not
  scanned yet or when `saveDebugFrames` is false, which is the ordinary case, so treat a dump as a
  debugging aid that is allowed to fail: emit the record first, then dump inside a `try`.

The practical loop is: run the engine, run the plugin with `--verbose` and `saveDebugFrames: true`,
compare the dumped PNG against what `LogVerbose` printed, nudge the rect, repeat. For a region that
is not subscribed at all, `ReadRoiAsync` will probe it without touching the subscription.

## 5. Error handling

Two independent things, and mixing them up is the classic way to corrupt a state machine.

**Per-region status.** A tick states what happened to each region:

```csharp
ctx.Tick.Status(id)          // Ok | Failed | NotSubscribed
ctx.Tick.HasErrors           // any region failed this tick
ctx.Tick.ErroredRois         // which ones, in wire order
ctx.Tick.ErrorMessage(id)    // what the engine said, or null if it did not fail
```

`Failed` and `NotSubscribed` are separate on purpose: a failed region was read and the read did not
work, while `NotSubscribed` means the id was never in the tick — a typo'd constant, which would
otherwise survive an entire session as "no reading".

Always prefer the `Try` accessors over the obsolete `Text`/`Ocr`/`Pixels`/`Error` ones. A region that
failed and a region that was genuinely blank *both* answer `""`, and for a state machine the
difference is everything: "the panel header read empty" can mean the panel closed, which files an
order that never completed.

**Whole-tick policy.** `ErrorPolicy` decides whether a degraded tick reaches the parser at all — one
decision made once, instead of a check every reader has to remember:

| Policy | Behaviour | When |
| --- | --- | --- |
| `AbortTick` (default) | The host skips the tick entirely. Nothing the plugin sees is ever degraded. | Almost always — especially any plugin holding state across ticks. |
| `SkipErrored` | The tick is delivered. The host notes which regions failed — through `LogVerbose`, so only under `--verbose`, and once per failure stretch rather than per tick — and the plugin is expected to check before trusting a reading. | Regions that are genuinely independent, where one failing should not blind the others. |
| `PassThrough` | Delivered with no host-side filtering or logging. | Rare; you are taking over the whole decision. |

Under `AbortTick` the host never calls `OnTickAsync` while a subscribed region is failed, so the
in-plugin guards above only matter under the other two policies and when tests drive the plugin
directly. Write them anyway — they are what makes the plugin correct under either.

Throwing out of `OnTickAsync` is survivable and logged; the run continues with the next tick. A
plugin that dies on one bad frame loses every bit of state it accumulated, which is why the host
refuses to let it.

## 6. Session events

`OnSessionEvent` is where the assumption "I have seen every frame since I started" stops holding. It
is deliberately void and not cancellable — these are notifications, not work — and it runs on the
host's loop, so anything slow belongs on the next tick.

| Event | Means | Typical response |
| --- | --- | --- |
| `Connected(EngineInfo)` | Subscribed and receiving. Raised **once per connect**, so a reconnect raises it again. | Read `Engine.ReplayMode` / `ScanInterval`; re-arm anything per-session. |
| `Reconnecting(Attempt)` | The session dropped; the host is about to dial again. `Attempt` counts from 1 within the current disconnected stretch and resets on the next `Connected`. | Escalate logging. Not a decision point — whether to keep going is the host's call. |
| `TicksDropped(Gap)` | Frames were scanned that this plugin never saw, proven by a jump in `TickData.FrameSeq`. | Treat the next tick as a fresh observation rather than the successor of the last one. |
| `Ended(StreamEndReason)` | The run is over; the summary is about to print. The last chance to persist. | Flush. Branch on the reason. |

Dropped ticks are a **normal live-mode event**, not a transport failure: the engine's per-client
channel holds 4 ticks and drops the oldest rather than stalling the scan loop for every other
plugin. A replay session never drops (the engine blocks instead), which is what makes corpus runs
deterministic. A tracker watching for an *edge* — a counter incrementing, a panel appearing — can
miss the edge entirely across a gap, so the honest response is to reset the "what did I last see"
state rather than to compare across the hole.

`StreamEndReason` distinguishes four endings: `ReplayCompleted` (the corpus ran out),
`EngineShutdown` (a live engine completed the stream), `Cancelled` (Ctrl+C, or the embedding host's
token), and `Faulted` (unrecoverable — a protocol the engine refuses). The host exits 0 for every
orderly ending including Ctrl+C, and 1 for a usage error and for `Faulted`, which are the two
endings a supervisor should not simply restart.

Reconnects deliberately **keep** plugin state: what the plugin already saw is still true, and the
first read after reconnect is a re-sighting rather than a new event.

## 7. Testing

Two layers, both in `GameCapture.Sdk.Testing` — a real package, not `InternalsVisibleTo`, so it works
from a plugin's own repository.

The template ([§2](#2-creating-the-project)) scaffolds `tests/MyPlugin.Tests.csproj` already wired
with `Microsoft.NET.Test.Sdk`, `xunit`, and a `PackageReference` on `GameCapture.Sdk.Testing` —
nothing to set up by hand. Building a test project from scratch (no template available) is in
[Appendix: manual project setup](#appendix-manual-project-setup).

**Unit: `TickDataBuilder` + `FakePluginServices`.** The builder produces a `TickData` the way the
engine would have sent it (through the SDK's own wire mapping), so a tick that could never arrive on
the wire cannot pass a test. `FakePluginServices` records emissions and logs instead of printing.

```csharp
using GameCapture.Sdk;
using GameCapture.Sdk.Testing;
using Xunit;

namespace MyPlugin.Tests;

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
```

The builder covers every shape a tick can carry: `.Text(id, text)`, `.Detailed(id, lines...)` (an
`OcrLineSpec` converts implicitly from a string, or is built from `OcrWordSpec`s when the parser
reads word geometry), `.Pixels(id, b, g, r, w, h)`, `.Errored(id, message)`, plus `.Manual()`,
`.FrameSeq(n)` for gap tests, and `.At(instant)` to separate frame time from processing time.
`FakePluginServices` exposes `Emitted`, `Logs`, `VerboseLogs`, a settable `Engine`, and
`DumpFrameHandler` / `ReadRoiHandler` for the calibration paths.

**Parity: `ReplayHarness`.** This spawns a real `GameCapture.Engine.exe` replaying a PNG corpus and drives
the plugin through its real `GameCapturePluginHost` path — public SDK plus an engine binary, no in-proc
shortcuts. It is what a plugin's CI runs.

```csharp
/// A spawned engine owns a named pipe and a Windows OCR instance, so two of these must never
/// run at once. The CollectionDefinition is what actually serializes them — the [Collection]
/// attribute alone only groups; without DisableParallelization the group still runs beside
/// every other collection in the assembly.
[CollectionDefinition("ReplayParity", DisableParallelization = true)]
public class ReplayParityCollection;

[Collection("ReplayParity")]
[Trait("Category", "Integration")]
public class ReplayParityTests
{
    [Fact]
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
        Assert.Equal("counter", record.Plugin);
        Assert.Equal(TriggerKind.Auto, record.Trigger);
    }
}
```

**`GAMECAPTURE_ENGINE_PATH` is effectively required for a plugin outside this repository.**
`EngineLocator.Resolve()` uses that env var when set, and otherwise falls back to walking up from the
test assembly's output looking for `src/GameCapture.Engine/bin` — a path that exists only inside a clone
of the engine repo. From a plugin's own repo the fallback finds nothing and `Resolve()` throws
`InvalidOperationException`, so point the variable at the engine you unpacked or built in
[§1](#1-prerequisites):

```powershell
$env:GAMECAPTURE_ENGINE_PATH = "C:\tools\sctracker\GameCapture.Engine.exe"
```

CI does the same thing, pinning it to the exact artifact it downloaded or built.
`ReplayOptions.Timeout` (default 5 minutes) is a hang bound, not a performance budget — a handful of
frames measures in seconds, so a fired timeout means something is stuck.

**Capturing the corpus** is a live, in-game step: run the engine with `--save-frames`, press the
configured hotkey (`engine-config.json`'s `hotkey`, default `Ctrl+Shift+F12`, logged at startup) at
each stage worth a frame. Each press writes one full-frame PNG into the engine's own output
directory — `engine-config.json`'s `outputDir`, `captures/` by default, resolved relative to the
config file and printed as `Dumps:` on startup. Copy those PNGs into `Fixtures/Replay/<name>/` and
copy them to the test output:

```xml
<None Include="Fixtures\Replay\my-corpus\**\*.png" CopyToOutputDirectory="PreserveNewest" />
```

(A corpus shared between several test projects lives outside any one of them and needs
`Link=` to land at the same relative path in each output — that is the pattern this repository's
own `tests/fixtures/corpus/` uses.)

Frames are full captures — the engine applies ROI geometry itself — and play back in ordinal
filename order, which the engine's timestamped names already satisfy. Full details, including why
replay never drops a tick, are in [`REPLAY.md`](REPLAY.md).

## 8. Cold-start checklist

Verified against a project built outside this repository:

```powershell
dotnet new gamecapture-plugin -n MyPlugin   # §2 — then rename/fill in MyPlugin.cs per §3
dotnet build MyPlugin -c Release            # SDK + contracts restore from the (local or nuget.org) feed

# Unit tests only. The parity test from §7 spawns a real engine, so it is filtered out
# here — run it once GAMECAPTURE_ENGINE_PATH and a corpus are in place.
dotnet test MyPlugin\tests\MyPlugin.Tests.csproj -c Release --filter "Category!=Integration"
```

Then, with an engine running:

```powershell
dotnet run --project <clone>\src\GameCapture.Engine     # terminal 1
dotnet run --project MyPlugin -- --verbose         # terminal 2
```

The plugin prints its banner, `waiting for engine on pipe '<name>'...`, and then — once connected —
the engine version, frame size, cadence, and its own ROI ids. If it waits forever, the pipe names
disagree: check `config.json` against `engine-config.json`, or pass `--pipe <name>` to both.

## 9. Config, CLI, and compatibility

**Config.** `PluginConfig` carries the two settings every plugin has — `pipeName` (must match the
engine's) and `saveDebugFrames`. Derive to add your own; `PluginConfig.Load<T>(path)` writes a
defaults file on first run so settings are discoverable without documentation, and `AfterLoad` is
the hook for anything that must resolve relative to the config file's own location (a ledger path,
typically). Hand the loaded instance to the host so it does not re-read the file:

```csharp
// MyConfig.cs
public sealed class MyConfig : PluginConfig
{
    public string LedgerPath { get; set; } = "ledger.csv";

    // Runs on both the first-run and the read-back path. A bare relative path would otherwise
    // resolve against the working directory, which is whatever shell launched the plugin.
    protected override void AfterLoad(string configPath)
        => LedgerPath = Path.GetFullPath(LedgerPath, Path.GetDirectoryName(configPath)!);
}

// Program.cs, for a plugin that takes its settings as a constructor argument
var config = PluginConfig.Load<MyConfig>(Path.Combine(AppContext.BaseDirectory, "config.json"));
return await GameCapturePluginHost.RunAsync(new MyPlugin.LedgerPlugin(config), args,
    new PluginHostOptions { Config = config });
```

The host cannot load a derived config itself — `Load<T>` needs the concrete type, and the plugin is
the only party that knows it and needs the typed instance back anyway.

Everything about *how* the screen is read — monitor, hotkey, OCR language, scan cadence — belongs to
the engine's config. A plugin that grew those knobs would be describing a capture stack it no longer
owns.

**CLI.** The host parses `--pipe <name>` (overriding the config) and `--verbose` for every plugin.
For flags of your own, use `PluginHostOptions.ExtraArgHandler`: it is handed the whole argument list
before anything else happens and returns an error message to abort with exit code 1, or null to
proceed. The host never consumes what it does not recognise, so a handler may read flags the host
also reads.

**Other host options.** `ConfigFileName` (null skips loading), `Output` (an `IPluginOutput` for
hosting outside a console), `ShutdownToken` and `HandleCancelKeyPress` (for an embedding process that
owns the lifetime), `RecordSink` (tee every emitted record — how the replay harness collects them),
`ReconnectDelay`, and `EngineWait`.

**Compatibility.** The `Track` stream opens with a handshake carrying an integer protocol version,
negotiated down to what both sides speak; an engine that refuses the version ends the run with
`StreamEndReason.Faulted` and exit 1 rather than retrying, because dialling again cannot fix it. That
version is distinct from any package or release version. The rules — what may change without a
protocol bump, and what may not — are in [`PROTOCOL.md`](PROTOCOL.md#version-policy) and, once
TASK-20 lands, `COMPATIBILITY.md`. A plugin that stays on the SDK's own types (never a generated
proto type, never `Grpc.*`) is the one that survives a wire change; CI greps for exactly that
(`.github/workflows/ci.yml`, "Plugin boundary grep gate").

## Appendix: manual project setup

Only needed when `GameCapture.Plugin.Template` cannot be installed. Five files — three here, two in
[§3](#3-anatomy-of-a-plugin) — plus a test project.

A plugin is an ordinary console exe:

```powershell
dotnet new console -n MyPlugin
```

`MyPlugin.csproj` — plain `net10.0`, plus references to the SDK and the contracts:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- Plain net10.0. A plugin parses text; it never touches the capture stack, so the Windows
       TFM the engine needs must not appear here. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>MyPlugin</RootNamespace>
  </PropertyGroup>

  <!-- Until the SDK is on nuget.org (TASK-21/22), reference it out of a clone of the engine
       repo, or a local pack — see §2. $(GameCaptureRepo) is the clone root; set it here or
       pass -p:GameCaptureRepo=... -->
  <PropertyGroup>
    <GameCaptureRepo Condition="'$(GameCaptureRepo)' == ''">C:\src\StarCitizenTracker</GameCaptureRepo>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="$(GameCaptureRepo)\src\GameCapture.Sdk\GameCapture.Sdk.csproj" />
    <ProjectReference Include="$(GameCaptureRepo)\src\GameCapture.Contracts\GameCapture.Contracts.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="config.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

`config.json` — the two settings every plugin has (see [§9](#9-config-cli-and-compatibility)); the
pipe name must match the engine's:

```json
{
  "pipeName": "GameCapture.Engine",
  "saveDebugFrames": false
}
```

`Program.cs` — the whole entry point. Everything else the two shipped plugins used to carry here
(argument parsing, the connect/reconnect loop, Ctrl+C, the summary) lives in the host now:

```csharp
using GameCapture.Sdk;

return await GameCapturePluginHost.RunAsync(new MyPlugin.CounterPlugin(), args);
```

`CounterPlugin.cs` and `Rois.cs` are [§3](#3-anatomy-of-a-plugin).

The test project — `dotnet new xunit -n MyPlugin.Tests`, then:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <PropertyGroup>
    <GameCaptureRepo Condition="'$(GameCaptureRepo)' == ''">C:\src\StarCitizenTracker</GameCaptureRepo>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\MyPlugin\MyPlugin.csproj" />
    <!-- TickDataBuilder, FakePluginServices, ReplayHarness. Becomes a PackageReference on
         GameCapture.Sdk.Testing once the packages ship (TASK-21/22). -->
    <ProjectReference Include="$(GameCaptureRepo)\src\GameCapture.Sdk.Testing\GameCapture.Sdk.Testing.csproj" />
  </ItemGroup>

</Project>
```

## See also

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — processes, projects, and the frozen constraints.
- [`ENGINE-SERVICES.md`](ENGINE-SERVICES.md) — every RPC, budget, and constant the engine offers.
- [`PROTOCOL.md`](PROTOCOL.md) — transport, handshake, version policy, coordinate spaces.
- [`REPLAY.md`](REPLAY.md) — corpus layout, capture, and replay.
