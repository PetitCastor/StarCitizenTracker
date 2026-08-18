# Engine services

What `GameCapture.Engine` offers over the wire, one section per RPC in `protos/capture.proto`. This is
the catalog — "what can a plugin ask the engine for" — without reading the engine's source. For the
agreements the wire carries (handshake, version policy, coordinate spaces, compatibility rules),
see [`docs/PROTOCOL.md`](PROTOCOL.md); for the process layout, see
[`docs/ARCHITECTURE.md`](ARCHITECTURE.md).

## Track

The one long-lived RPC: a bidirectional stream a plugin opens once per connection and keeps open
for the life of its session (`protos/capture.proto:14`, `CaptureGrpcService.Track`,
`src/GameCapture.Engine/Grpc/CaptureGrpcService.cs:51-170`).

### Tick model

Every scanned frame produces exactly one `TickResult` per connected client
(`ScanLoop.RunAsync`, `src/GameCapture.Engine/ScanLoop.cs:134-163`). All OCR for the whole engine
happens inside that one loop, one frame at a time, so a tick can never straddle two frames or mix
in a different client's ROI set — per-tick atomicity is structural, not a convention enforced by
discipline.

**Every ROI in the client's set at tick time is answered in that tick** — either a successful
result or a per-ROI `error` (`ScanLoop.ReadOneAsync` never throws out of the tick;
`src/GameCapture.Engine/ScanLoop.cs:198-267`). A failing ROI never removes another ROI's result, for
that client or any other client sharing the engine.

The only thing a plugin *sends* on this stream past the initial `Hello` is a `RoiSetUpdate` — a
**full replacement** of its subscribed set, idempotent, with an empty set a legitimate
heartbeat-only state rather than "not ready" (`protos/capture.proto:77-80`; full rules in the Tick
atomicity section of `docs/PROTOCOL.md`).

### ROI kinds

Declared per-region on subscribe (`RoiSpec.mode`, `protos/capture.proto:66`, values from the
`RoiMode` enum at `protos/capture.proto:50-54`; SDK-side `RoiKind`, `src/GameCapture.Sdk/RoiKind.cs`):

| Kind | What it returns | When to use |
| --- | --- | --- |
| `Text` | Plain OCR string of the region. | Panels where you only need the text, not per-word position — status lines, single-value readouts. |
| `Detailed` | OCR text plus per-line, per-word geometry (`OcrLine`/`OcrWord`, crop-space rects). | Table-shaped UI where column position matters — matching a value to the row/column it sits in. |
| `Pixels` | Raw BGRA bytes of the region at 1:1, no OCR. | Small colour probes — a toggle's on/off state by its fill colour — never for reading text. |

### Coordinate spaces and scaling

A client declares every ROI in **reference space** (2560x1440, `RoiScaler.ReferenceWidth/Height`,
`src/GameCapture.Contracts/RoiScaler.cs:12-13`). **The engine does all scaling** — `RoiScaler.ToFrame`
maps a reference rect to the actual frame pixels for whatever resolution is being captured
(`ScanLoop.ReadOneAsync`, `src/GameCapture.Engine/ScanLoop.cs:214`), and the frame-space rect actually
read is echoed back on `RoiResult.frame_rect`. A plugin must never re-scale a rect the engine
reported — see the coordinate-spaces table in `docs/PROTOCOL.md` for the full three-space picture
(reference / frame / upscaled-OCR-crop). A reference ROI that cannot touch the frame at all —
`x`/`y` beyond the frame edge, or a zero width/height — is rejected as a per-ROI error rather than
silently clamped to a meaningless sliver (`ScanLoop.EnsureRoiInFrame`,
`src/GameCapture.Engine/ScanLoop.cs:292-314`).

### Scale clamp behavior (`Text`/`Detailed` only)

`RoiSpec.scale` is the OCR upscale factor, ignored for `Pixels`. `0` (proto3's unset) means
"engine default", `WireLimits.DefaultOcrScale = 1.0`
(`src/GameCapture.Contracts/WireLimits.cs:11,27-28`). Before capturing, the engine clamps the requested
scale so the upscaled crop never exceeds `OcrEngine.MaxImageDimension` (a Windows OCR limit) on
its longest side (`OcrPipeline.EffectiveScale`, `src/GameCapture.Engine/Core/OcrPipeline.cs:120-126`).
The scale actually applied is reported back on `RoiResult.effective_scale`, which is therefore
always `> 0` on a successful result and `0` only on an error.

### Backpressure: DropOldest cap-4 (live), Wait (replay)

Each connection has a bounded outbound channel of **4 ticks**
(`ClientConnection.OutboundCapacity`, `src/GameCapture.Engine/ClientConnection.cs:14-18` — roughly 2 s
at the default 500 ms cadence). The overflow policy is chosen per connection, live vs. replay
(`ClientConnection.cs:41-49`):

- **Live**: `BoundedChannelFullMode.DropOldest`. A plugin that falls behind loses old ticks rather
  than stalling the scan loop or any other connected plugin; it sees a **gap in
  `TickData.FrameSeq`**, never a stale backlog.
- **Replay**: `BoundedChannelFullMode.Wait`. A dropped frame changes the outcome and determinism is
  the whole point of a corpus run, so the scan loop blocks instead of dropping.

On the SDK side, `FrameSeqTracker` (`src/GameCapture.Sdk/Plugin/FrameSeqTracker.cs`) watches
`FrameSeq` across ticks and `GameCapturePluginHost`'s dispatcher raises `SessionEvent.TicksDropped(Gap)`
when it detects a jump (`src/GameCapture.Sdk/Plugin/SessionEvent.cs:28-39`) — a normal, if unwelcome,
live-mode event rather than a transport failure. A sequence that goes backwards (an engine
restart) is treated as a fresh start, not a negative gap, and does not raise the event
(`FrameSeqTracker.TryObserve`, `src/GameCapture.Sdk/Plugin/FrameSeqTracker.cs:31-42`).

### `manual` flag semantics

`TickResult.manual` is **the same value for every connected client on a given tick** — the hotkey
either fired on this frame or it did not, and no two plugins may disagree about which. The scan
loop reads the flag exactly once per frame via `Interlocked.Exchange(ref _manualFlag, 0)`
(`src/GameCapture.Engine/ScanLoop.cs:85,120`), so a press on the hook thread is picked up atomically by
the next tick and cleared for the one after. `TickData.Manual` on the SDK side is a straight copy
of that bit (`src/GameCapture.Sdk/TickData.cs:67`); the plugin host dispatches a manual tick to
`IGameCapturePlugin.OnManualTickAsync` instead of `OnTickAsync` (`TickDispatcher.DispatchAsync`,
`src/GameCapture.Sdk/Plugin/TickDispatcher.cs:78-79`), which a plugin overrides when the hotkey means
something different from its normal capture (`IGameCapturePlugin.cs:49-55`).

### HelloAck handshake

Every `Track` stream opens with the client's `Hello` (name + protocol version) and the engine's
single `HelloAck`, sent once before the first tick and travelling beside the tick channel rather
than through it — the channel evicts its *oldest* entry under live backpressure, and the oldest
entry would otherwise be the ack itself (`CaptureGrpcService.cs:63-70`). Full sequencing, version
negotiation, and the rejection path (`FAILED_PRECONDITION` with `gamecapture-protocol-min/max`
trailers) are documented in the Handshake section of `docs/PROTOCOL.md`; this section only notes
that a `Track` call is unusable before the ack, and the SDK's `CaptureClient.TrackAsync` awaits it
before returning a session.

## ReadRoi

One-shot OCR of a single ROI against the **most recently scanned frame** — a debug/calibration
aid, not a data path (`protos/capture.proto:16-17`, `CaptureGrpcService.ReadRoi`,
`src/GameCapture.Engine/Grpc/CaptureGrpcService.cs:176-197`).

- **One-shot vs. retained frame**: it deliberately does **not** capture a fresh frame. It reads
  whatever `ScanLoop` last scanned, under `ScanLoop.FrameGate` so it can never race a frame swap
  and OCR a disposed bitmap. If the engine has not scanned anything yet, `ReadRoiResponse.no_frame`
  is `true` and there is no result.
- Runs the exact same `ScanLoop.ReadOneAsync` path a live tick uses, so a calibration read behaves
  identically to what a subscribed ROI would have gotten on that frame.
- **Calibration workflow**: point a throwaway `RoiSpec` at a candidate rectangle, call `ReadRoi`
  repeatedly while nudging the rect, and read `RoiResult.frame_rect`/`text` back — no plugin
  process or subscription needed. The SDK exposes this as `CaptureClient.ReadRoiAsync`
  (`src/GameCapture.Sdk/CaptureClient.cs:236`).

## DumpFrame

Saves the most recently scanned frame (or a crop of it) as a PNG in the engine's own output
directory and returns the path — this, and not any bytes over the wire, is how a plugin builds a
replay corpus without a raw frame ever crossing the boundary
(`protos/capture.proto:19-20`, `CaptureGrpcService.DumpFrame`,
`src/GameCapture.Engine/Grpc/CaptureGrpcService.cs:203-237`).

- **Prefix sanitization**: `prefix` arrives from another process and becomes part of a filename.
  `SanitizePrefix` takes only the file-name component (`Path.GetFileName`, stripping any path the
  caller sent) and replaces every character `Path.GetInvalidFileNameChars()` rejects with `_`, so a
  plugin cannot steer a write outside the configured output directory; an empty result falls back
  to `"dump"` (`CaptureGrpcService.cs:22-23,301-312`).
- **Output dir ownership**: always `EngineConfig.OutputDir` (`src/GameCapture.Engine/Core/EngineConfig.cs:19`)
  — the engine's own config, never anything the client supplies directly. Relative paths in
  `engine-config.json` resolve against the config file's own directory (`EngineConfig.Load`,
  `EngineConfig.cs:51-69`).
- **Returns an engine-local path**: `DumpFrameResponse.path` is the absolute path *on the machine
  running the engine* — meaningful to a human or a same-machine corpus copy, not something a
  plugin should assume it can open directly if it ever runs remotely.
- `full_frame = true` saves the whole retained bitmap (`FrameSaver.SavePngAsync`); `false` crops to
  `roi` (reference space) first, going through the same `EnsureRoiInFrame` / `RoiScaler.ToFrame`
  path `ReadOneAsync` uses, so a crop dump and a live ROI read agree on what region they mean.

## GetStatus

Unary, stateless snapshot of what the engine is doing right now
(`protos/capture.proto:22`, `CaptureGrpcService.GetStatus`,
`src/GameCapture.Engine/Grpc/CaptureGrpcService.cs:239-250`; assembled from `EngineStatus.Snapshot()`,
`src/GameCapture.Engine/EngineStatus.cs:68-86`).

| Field | Meaning |
| --- | --- |
| `engine_version` | The engine's own build version (assembly informational version, falling back to the assembly version, then `"0.0.0"`). |
| `frame_width` / `frame_height` | Last scanned frame's pixel size; both `0` until the first frame — not a 0x0 screen. |
| `frame_seq` | The last scanned frame's sequence number. |
| `replay_mode` | `true` when frames come from a PNG corpus rather than live WGC capture. |
| `ocr_language` | BCP-47 tag of the recognizer actually loaded (`OcrPipeline.LanguageTag`). |
| `connected_clients` | Names of every connection currently open on the engine, ordinal-sorted — not only ones that have subscribed. A client is listed the moment its `Track` call registers, as `"?"` until its `Hello` names it (`SubscriptionRegistry.Register`, `src/GameCapture.Engine/SubscriptionRegistry.cs:26-36`); a bare `GetStatus`/`WaitForEngineAsync` call opens no `Track` stream and does not appear. |
| `min_supported_protocol` / `max_supported_protocol` | The `[Min, Current]` protocol-version range this engine build accepts — `GameCapture.Contracts.ProtocolVersion`; see the version-policy section of `docs/PROTOCOL.md`. |
| `scan_interval_ms` | The cadence the scan loop *actually* runs at, after its own minimum clamp — not the raw config value. `0` means an engine older than this field; a client falls back to `EngineDefaults.DefaultScanInterval` (500 ms, `src/GameCapture.Sdk/EngineDefaults.cs:39`). |

`GetStatus` is also the mechanism `CaptureClient.WaitForEngineAsync` polls to detect a running
engine before opening a `Track` stream — a pipe nobody is listening on makes the dial *block*
rather than fail cleanly, so a plugin cannot tell "no engine yet" from "engine hung" any other way
(`src/GameCapture.Sdk/CaptureClient.cs:70`, discussed in the Transport section of `docs/PROTOCOL.md`).

## Replay mode

`--replay <dir>` feeds a directory of full-frame PNGs through the exact same `ScanLoop` production
uses instead of live WGC capture (`ReplayFrameSource`, `src/GameCapture.Engine/Core/ReplayFrameSource.cs`;
wired in `Program.cs:41-52,71-76`). Full corpus layout, capture workflow, and the `ReplayHarness`
SDK-testing helper are documented in [`docs/REPLAY.md`](REPLAY.md); this section covers the flags
and the determinism guarantees specifically.

**Flags**:

- `--replay <dir>`: the directory must exist (checked at startup, `Program.cs:41-46`) but is not
  required to contain any `*.png` files — an empty corpus starts normally and produces a zero-tick
  run, since `ReplayFrameSource.EnumerateCorpus` finds nothing and the loop ends immediately.
  Mutually exclusive with `--save-frames` (`Program.cs:48-52`) — replay has no live screen to save a
  frame *from*.
- In replay, the hotkey listener and the live status-bar metrics timer are never started
  (`Program.cs:66-68,136-156`) — there is no live screen to trigger against, and a 1 Hz status bar
  over a batch run is pure flicker.

**Determinism guarantees**:

- Frames are enumerated once at construction, sorted by filename in **ordinal** order
  (`ReplayFrameSource.EnumerateCorpus`, `src/GameCapture.Engine/Core/ReplayFrameSource.cs:50-51`) — not
  culture-aware, so playback order cannot change with the machine's locale.
- Each frame is decoded identically to how the engine's live path decodes a captured frame
  (`ReplayFrameSource.DecodeFrameAsync`) — a replay exercises the same reference-space-in,
  frame-space-out path production does, not a parallel implementation.
- The scan loop does not start consuming the corpus until **at least one client has sent a
  `RoiSetUpdate`** (`SubscriptionRegistry.WaitForAnySubscribedAsync`,
  `src/GameCapture.Engine/SubscriptionRegistry.cs:48-57`) — otherwise a corpus could be consumed into the
  void while a plugin is still connecting, silently producing nothing.
- Replay's outbound channel is `Wait`, not `DropOldest` (see Track above): every frame in the
  corpus produces exactly one tick, regardless of how slowly the plugin consumes them.
- Replay runs flat out, with no inter-tick delay — the corpus is finite and the scan cadence is a
  live-capture concern, not a semantic one (`ScanLoop.cs:180-183`).
- When the corpus is exhausted, `SubscriptionRegistry.CompleteAll()` completes every client's
  `Track` stream normally — `Ticks` ends and a plugin runs its finalisers, exactly as a live
  engine shutdown does (`ScanLoop.cs:190-195`, `SubscriptionRegistry.cs:59-68`).

## `--save-frames`: corpus capture

`--save-frames` arms `FrameDumpService` on the manual hotkey path instead of the plain
`ScanLoop.TriggerManual()`: each press downloads the current frame, saves it as a full PNG under
`EngineConfig.OutputDir`, and logs the path (`Program.cs:139-147`,
`src/GameCapture.Engine/Core/FrameDumpService.cs`). Cannot be combined with `--replay`
(`Program.cs:48-52`). This is the mechanism behind the corpus-capture workflow in
`docs/REPLAY.md` — press the hotkey at each stage worth a frame while playing live, then copy the
resulting PNGs into `tests/fixtures/corpus/<name>/`.

## Hotkeys

Hotkeys are **engine-owned**, never a plugin concern. `HotkeyListener` installs a low-level
keyboard hook (`WH_KEYBOARD_LL`) on a dedicated message-pump thread
(`src/GameCapture.Engine/Core/HotkeyListener.cs:6-11`) — `RegisterHotKey` is not used because a game
reading input through raw input never lets `WM_HOTKEY` fire while it has
focus; a low-level hook sees keys at the system input chain before the game does. The combo is
configured as a string (`EngineConfig.Hotkey`, default `"Ctrl+Shift+F12"`) and parsed by
`HotkeyListener.ParseHotkey` into modifier flags + a virtual key. The hook callback must return
fast, so it only sets a flag; the scan loop picks the flag up on its next frame and surfaces it to
every connected plugin as `TickResult.manual`. Every plugin sees the hotkey the same way — as
`ctx.Tick.Manual` on the `TickContext` it receives, routed to `OnManualTickAsync` (see the `manual`
flag semantics above) — there is no separate hotkey RPC or event.

## Budgets table

| Budget | Value | Source |
| --- | --- | --- |
| `ROI_MODE_PIXELS` payload cap | 256 KiB (a 256x256 BGRA patch) | `WireLimits.MaxPixelBytes`, `src/GameCapture.Contracts/WireLimits.cs:20` (re-exported as `EngineDefaults.MaxPixelBytes`, `src/GameCapture.Sdk/EngineDefaults.cs:46`). Checked against **frame-space** bounds, i.e. after `RoiScaler.ToFrame` (`ScanLoop.cs:242`) — a probe sized to fit at 2560x1440 can still exceed the cap on a higher-resolution capture. |
| OCR upscale clamp | Crop's longest side capped at `OcrEngine.MaxImageDimension` (Windows OCR API limit) | `OcrPipeline.EffectiveScale`, `src/GameCapture.Engine/Core/OcrPipeline.cs:120-126` |
| Minimum scan interval | 100 ms | `ScanLoop.MinScanInterval`, `src/GameCapture.Engine/ScanLoop.cs:22` |
| Default scan interval | 500 ms (a stock, unconfigured engine) | `EngineConfig.ScanIntervalMs` default, `src/GameCapture.Engine/Core/EngineConfig.cs:32`; re-exported as `EngineDefaults.DefaultScanInterval`, `src/GameCapture.Sdk/EngineDefaults.cs:39` |
| Reference ROI space | 2560x1440 | `RoiScaler.ReferenceWidth/Height`, `src/GameCapture.Contracts/RoiScaler.cs:12-13` |
| Pixel byte order | BGRA | `EngineDefaults.PixelChannelOrder`, `src/GameCapture.Sdk/EngineDefaults.cs:54`; produced by `PixelStrip.CaptureAsync`, `src/GameCapture.Engine/Core/PixelSampler.cs:41-58` |
| Outbound tick channel depth | 4 ticks (~2 s at the default cadence) | `ClientConnection.OutboundCapacity`, `src/GameCapture.Engine/ClientConnection.cs:14-18` |
| gRPC receive limit (whole `TickResult`) | 4 MiB (gRPC default) | Why the per-ROI pixel cap exists at all — an unbounded `PIXELS` ROI could sink an entire tick; see `WireLimits.cs:14-19` |

## See also

- [`docs/PROTOCOL.md`](PROTOCOL.md) — the wire contract's agreements: transport, handshake,
  version policy, coordinate spaces, compatibility rules, backpressure.
- [`docs/REPLAY.md`](REPLAY.md) — corpus layout, capturing one in-game, `ReplayHarness`.
- [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) — process diagram, frozen constraints, component table.
- [`docs/PLUGIN-AUTHORING.md`](PLUGIN-AUTHORING.md) — writing an `IGameCapturePlugin` against this
  catalog.
- [`docs/COMPATIBILITY.md`](COMPATIBILITY.md) — protocol/engine/SDK version matrix and the rules for
  bumping each.
