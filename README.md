# GameCapture

[![CI](https://github.com/PetitCastor/StarCitizenTracker/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/PetitCastor/StarCitizenTracker/actions/workflows/ci.yml)
[![Release](https://github.com/PetitCastor/StarCitizenTracker/actions/workflows/release.yml/badge.svg)](https://github.com/PetitCastor/StarCitizenTracker/releases)

<!-- NuGet badges (GameCapture.Sdk, GameCapture.Contracts, GameCapture.Sdk.Testing) land with the
     packaging/release work in TASK-16/17, once the packages are actually published. -->

**Read a game's own UI and turn it into data — without reading the game.** GameCapture
captures the screen, runs OCR on the regions a plugin asks for, and hands each plugin one frame's
worth of readings at a time. Nothing touches game memory, game files, or the network: what a player
can see, a plugin can record.

A **capture engine** owns the screen, the OCR, and the hotkey. Each **plugin** is a separate process
that owns nothing but its own parsing and state. They talk over named-pipe gRPC, and only OCR
results and small pixel buffers ever cross — so a plugin cannot take down capture, cannot see
another plugin, and cannot be brought down by either.

```mermaid
flowchart LR
    Screen["Game window"] --> Engine

    subgraph Engine["GameCapture.Engine (one per user)"]
        Cap["WGC capture + Windows OCR"]
        Loop["ScanLoop<br/>one frame in, one tick per client out"]
        Cap --> Loop
    end

    Engine <==>|"named pipe gRPC<br/>OCR results only"| P1["MissionPlugin"]
    Engine <==>|"named pipe gRPC"| P2["RefineryPlugin"]
    Engine <==>|"named pipe gRPC"| P3["your plugin"]
```

A plugin implements `IGameCapturePlugin` — a name, a set of regions, and what to do with a tick — and
hands it to `GameCapturePluginHost.RunAsync`, which owns connecting, subscribing, reconnecting,
cancellation, and the end-of-run summary. Three members is a working tracker; see
[`docs/PLUGIN-AUTHORING.md`](docs/PLUGIN-AUTHORING.md).

## Quickstart

Windows 10/11 with an OCR language pack, and the .NET 10 SDK:

```powershell
git clone https://github.com/PetitCastor/StarCitizenTracker.git
cd StarCitizenTracker
dotnet build GameCapture.slnx
dotnet run --project src/GameCapture.Engine                    # terminal 1: owns the screen
dotnet run --project src/Plugins/MissionPlugin -- --verbose  # terminal 2: one tracker
```

The engine prints its pipe name, monitor, OCR language, and hotkey on startup; the plugin prints
what it connected to and which regions it subscribed. Ctrl+C ends either one cleanly. Engine and
plugin must agree on the pipe name — configured in `src/GameCapture.Engine/engine-config.json` and each
plugin's `config.json`, or overridden on both with `--pipe <name>`.

## Documentation

| Document | What it covers |
| --- | --- |
| [`docs/PLUGIN-AUTHORING.md`](docs/PLUGIN-AUTHORING.md) | Writing a tracker: project setup, `IGameCapturePlugin`, ROIs and calibration, error policy, session events, testing. Start here. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Processes, projects, and the frozen constraints behind the split. |
| [`docs/ENGINE-SERVICES.md`](docs/ENGINE-SERVICES.md) | The engine's service catalog: `Track`/`ReadRoi`/`DumpFrame`/`GetStatus`, replay mode, hotkeys, every budget and constant. |
| [`docs/PROTOCOL.md`](docs/PROTOCOL.md) | The wire contract: transport, handshake, version policy, coordinate spaces, tick atomicity, backpressure. |
| [`docs/REPLAY.md`](docs/REPLAY.md) | Replay corpora: layout, capturing one in-game, and running one against a plugin. |

## Repository map

| Project | Purpose |
| --- | --- |
| `protos/capture.proto` | The wire contract itself — every RPC and message shape. Linked into `GameCapture.Contracts` and guarded by `buf` in CI. |
| `src/GameCapture.Engine` | Captures monitor frames, runs OCR, hosts the named-pipe gRPC service. The only Windows-TFM project. |
| `src/GameCapture.Contracts` | Generated code for the contract above, plus the pure types both sides share. |
| `src/GameCapture.Sdk` | Plugin SDK: engine client, `IGameCapturePlugin`, `GameCapturePluginHost`. |
| `src/GameCapture.Sdk.Testing` | Testing companion: `TickDataBuilder`, `FakePluginServices`, `ReplayHarness`. |
| `src/Plugins/MissionPlugin` | Tracks mission acceptance from the contract manager. |
| `src/Plugins/RefineryPlugin` | Tracks refinery work orders; owns the order ledger. |
| `tests` | One test project per component, plus the shared replay corpora under `tests/fixtures/corpus/`. |

## Running

Start the engine first, then one or more plugins in separate terminals:

```powershell
dotnet run --project src/GameCapture.Engine
dotnet run --project src/Plugins/MissionPlugin
dotnet run --project src/Plugins/RefineryPlugin
```

Engine flags:

| Flag | Purpose |
| --- | --- |
| `--pipe <name>` | Overrides the configured named-pipe name. |
| `--monitor <index>` | Overrides which monitor is captured. |
| `--ocr-lang <bcp47>` | Overrides the configured OCR language. |
| `--replay <dir>` | Processes saved PNG frames instead of live monitor capture. |
| `--save-frames` | Saves a full PNG frame whenever the configured manual hotkey is pressed. |
| `--verbose` | Per-ROI logging on every scan. |

Plugin flags: `--pipe <name>` and `--verbose` are parsed by the host for every plugin; individual
plugins may add their own (RefineryPlugin takes `--ledger <path>`).

`--replay` is for deterministic corpus runs and cannot be combined with `--save-frames`. Each
plugin's own settings live in its `config.json`; everything about *how* the screen is read is the
engine's, in `src/GameCapture.Engine/engine-config.json`.

Releases publish the engine as a self-contained `win-x64` zip —
[Releases](https://github.com/PetitCastor/StarCitizenTracker/releases). Plugins are built from
source until the SDK ships on nuget.org.

## Testing

```powershell
dotnet test GameCapture.slnx
```

Some capture-engine integration tests require a supported Windows OCR language pack; the normal
development environment uses the installed English pack. Replay-parity tests spawn a real engine
binary against a corpus — `GAMECAPTURE_ENGINE_PATH` pins which one, otherwise the newest local build
is used.

Changes to `protos/capture.proto` are lint- and breaking-change-checked against `master` by the
`proto-guard` CI job (`buf.yaml`), and a grep gate fails any plugin source that reaches for a
transport or engine internal.
