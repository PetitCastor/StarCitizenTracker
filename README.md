# Star Citizen Tracker

Reads the game off the screen and records what happened. No log files, no memory reading, no
game modification: a Windows Graphics Capture frame goes through Windows OCR, and the text is
parsed into records.

The system is three processes, split so that only one of them has to know about Windows:

| Process | Owns |
|---|---|
| **CaptureEngine** (`src/CaptureEngine`) | WGC capture, Windows OCR, pixel sampling, the hotkey hook, frame dumps and replay. Game-agnostic — it knows rectangles and pixels, never game semantics. Hosts a gRPC server on a named pipe. |
| **MissionPlugin** (`src/Plugins/MissionPlugin`) | The mission tab: accept-counter parsing, capturing the mission text on a new accept. |
| **RefineryPlugin** (`src/Plugins/RefineryPlugin`) | The refinery panels: row parsing, the panel state machine, and the work-order ledger (`orders.jsonl`). |

Supporting libraries: `src/CaptureContracts` (the `protos/capture.proto` wire contract plus the
pure shared types), `src/TrackerSdk` (the client every plugin talks through), `src/Common`
(console sink and record types).

Raw frames never cross the process boundary — only OCR results and small pixel buffers do — and
everything a plugin needs for one decision arrives in a single tick from a single frame.

## Running

Start the engine first, then any number of plugins against it. Each is a normal console app; the
engine cannot be a Windows service because WGC does not work from session 0.

```
dotnet run --project src\CaptureEngine
dotnet run --project src\Plugins\MissionPlugin
dotnet run --project src\Plugins\RefineryPlugin
```

### The `--pipe` convention

Engine and plugins meet on a named pipe. Both sides default to
`StarCitizenTracker.CaptureEngine` (from their config files) and both take `--pipe <name>` to
override it. **The name must match on every process in a run** — a plugin pointed at a pipe no
engine is serving simply never connects.

```
dotnet run --project src\CaptureEngine            -- --pipe my-session
dotnet run --project src\Plugins\RefineryPlugin   -- --pipe my-session
```

Use a distinct name to run a second, isolated set of processes (e.g. a replay alongside a live
session).

### Replay

Replay is an engine-side concept: point it at a directory of saved PNG frames and it feeds those
through the same pipeline instead of capturing the screen, deterministically and with no game
running. Plugins need no flag — they see ordinary ticks.

```
dotnet run --project src\CaptureEngine          -- --replay captures\refinery-run
dotnet run --project src\Plugins\RefineryPlugin
```

Frames come from the hotkey (`Ctrl+Shift+F12` by default) writing to the engine's `outputDir`.
In replay the engine holds the corpus until a plugin subscribes, so no frame is burned before
anyone is listening, and it ends the stream when the corpus is exhausted.

While replaying, RefineryPlugin redirects its ledger to a throwaway file so a replay can never
write into real order history; `--ledger <path>` overrides that explicitly.

### Flags

| Process | Flags |
|---|---|
| CaptureEngine | `--pipe <name>`, `--replay <dir>`, `--monitor <index>`, `--ocr-lang <bcp47>`, `--verbose` |
| MissionPlugin | `--pipe <name>`, `--verbose` |
| RefineryPlugin | `--pipe <name>`, `--ledger <path>`, `--verbose` |

## Configuration

Each process owns its own config file, copied next to its binary at build time. There is no
shared config: an engine setting is never a plugin's business and vice versa.

- `src/CaptureEngine/engine-config.json` — `hotkey`, `monitorIndex`, `outputDir`, `ocrLanguage`,
  `pipeName`, `scanIntervalMs`, `metricsEnabled`, `metricsIntervalMs`.
- `src/Plugins/MissionPlugin/config.json` — `pipeName`, `saveDebugFrames`.
- `src/Plugins/RefineryPlugin/config.json` — `pipeName`, `ledgerEnabled`, `ledgerPath`,
  `saveDebugFrames`. An empty `ledgerPath` means the per-user default under `LOCALAPPDATA`.

`ocrLanguage` empty means the Windows display language. A missing OCR pack is the most likely
first-run failure; the engine exits with install instructions rather than a stack trace.

ROIs are authored in a fixed 2560x1440 reference space and scaled to the actual frame by the
engine, so a different monitor resolution needs no config change.

## Building and testing

```
dotnet build StarCitizenTracker.slnx
dotnet test StarCitizenTracker.slnx
```

Tests tagged `Category=Integration` (the engine replay-parity suite) drive real Windows OCR over
saved corpora, so they need an OCR language pack installed — the same requirement the app has.
Filter them out with `--filter Category!=Integration` on a machine without one.

Coverage uses the exclusion policy in `coverlet.runsettings`, which drops the process-edge files
(capture interop, the WGC frame pump, engine config I/O, the named-pipe channel, each
`Program.cs`) from the aggregate; the parsers, state machines, ledger, OCR pipeline, hotkey
parsing and wire mapping all stay gated:

```
dotnet test StarCitizenTracker.slnx --settings coverlet.runsettings --collect:"XPlat Code Coverage"
```
