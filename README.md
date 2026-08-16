# Star Citizen Tracker

Star Citizen Tracker captures the game screen and lets independent tracker plugins turn what they
see into useful data. The capture engine and plugins run as separate processes and communicate over
named-pipe gRPC, so a plugin cannot take down screen capture or another plugin.

## Repository map

| Project | Purpose |
| --- | --- |
| `src/CaptureEngine` | Captures monitor frames, runs OCR, and hosts the named-pipe gRPC service. |
| `src/CaptureContracts` | Protocol Buffers contract shared by the engine and plugins. |
| `src/TrackerSdk` | Plugin SDK: the engine client, the `ITrackerPlugin` contract, and `TrackerPluginHost`. |
| `src/Plugins/MissionPlugin` | Tracks mission information from the in-game UI. |
| `src/Plugins/RefineryPlugin` | Tracks refinery work-order information from the in-game UI. |
| `tests` | Unit, integration, and replay-parity test projects. |

A plugin implements `ITrackerPlugin` — a name, a set of regions, and what to do with a tick — and
hands it to `TrackerPluginHost.RunAsync`, which owns connecting, subscribing, reconnecting,
cancellation, and the end-of-run summary.

The engine/plugin wire contract — transport, handshake, version policy, and the guarantees a plugin
may rely on — is documented in [`docs/PROTOCOL.md`](docs/PROTOCOL.md). Changes to
`protos/capture.proto` are lint- and breaking-change-checked against `master` by the `proto-guard`
CI job (`buf.yaml`).

## Build

Build the complete solution from the repository root:

```powershell
dotnet build StarCitizenTracker.slnx
```

## Run

Start the engine first, then start one or more plugins in separate terminals:

```powershell
dotnet run --project src/CaptureEngine
dotnet run --project src/Plugins/MissionPlugin
dotnet run --project src/Plugins/RefineryPlugin
```

The engine and plugins must use the same named pipe. Configure it in their JSON configuration files,
or override the engine's configured name with `--pipe <name>`:

```powershell
dotnet run --project src/CaptureEngine -- --pipe StarCitizenTracker
```

Useful engine flags:

| Flag | Purpose |
| --- | --- |
| `--pipe <name>` | Overrides the configured named-pipe name. |
| `--replay <dir>` | Processes saved PNG frames instead of live monitor capture. |
| `--save-frames` | Saves a full PNG frame whenever the configured manual hotkey is pressed. |

`--replay` is intended for deterministic corpus runs and cannot be combined with `--save-frames`.
The engine configuration is `src/CaptureEngine/engine-config.json`; each plugin has its own
`config.json`.

## Test

Run the full suite from the repository root:

```powershell
dotnet test
```

Some capture-engine integration tests require a supported Windows OCR language pack. The normal
development environment uses the installed English OCR pack.
