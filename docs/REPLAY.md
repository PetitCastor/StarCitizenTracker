# Replay corpora

A **corpus** is a directory of full-frame PNGs the engine can feed through its scan loop instead
of live capture — the mechanism behind `--replay`, `ReplayFrameSource`, and every replay-parity
test in this repo (`RefineryPlugin.Tests/ReplayParityTests.cs`, `MissionPlugin.Tests/ReplayParityTests.cs`,
and the engine's own thin smoke in `GameCapture.Engine.Tests/ReplayHarnessTests.cs`).

## Layout

- One directory = one corpus. Every `*.png` directly inside it is one frame; no subdirectories.
- Filenames sort in **ordinal** order (`StringComparer.Ordinal`, not culture-aware) to produce
  playback order — `ReplayFrameSource.EnumerateCorpus` and every test source that has to agree
  with it share this rule. `FrameSaver`'s timestamped names (`frame_YYYYMMDD_HHmmss_fff.png`)
  already sort correctly this way; a hand-assembled corpus must preserve that property.
- Frames are full captures, not cropped ROIs — the engine applies ROI geometry itself, so a
  corpus exercises the same reference-space-in/frame-space-out path production does.
- The shared fixtures live under `tests/fixtures/corpus/<name>/` and are linked into whichever
  test project needs them, one `<None Include="..\fixtures\corpus\<name>\**\*.png" Link="Fixtures\Replay\<name>\..." />`
  per corpus — see `RefineryPlugin.Tests.csproj` and `MissionPlugin.Tests.csproj` for the pattern.
  Scoped per corpus rather than a `corpus\**` catch-all, so one plugin's corpus never gets copied
  into every other plugin's test output. The
  engine's own `Fixtures/engine-smoke` corpus (`GameCapture.Engine.Tests`) stays local to that project —
  it is the corpus essentially the whole engine-side suite drives (scan loop, gRPC host, SDK
  client, handshake, plugin host, and the `ReplayHarness` smoke), not just the harness.

## Capturing a corpus in-game

1. Run the engine against the live game with `--save-frames`:
   `dotnet run --project src\GameCapture.Engine -- --save-frames`
2. Press the configured hotkey (`engine-config.json`'s `Hotkey`, logged on startup) at each stage
   worth a frame — e.g. for a refinery order: SETUP open, after each scroll, on a REFINE toggle,
   post-CONFIRM, and (for a parity corpus that needs it) one CANCEL. Each press saves one
   full-frame PNG under the configured dump directory.
3. Copy the resulting PNGs into a new `tests/fixtures/corpus/<name>/` directory. No renaming
   needed — the timestamped names already sort in capture order.

`--save-frames` and `--replay` are mutually exclusive (replay has no live screen to save from —
see `Program.cs`'s check) — a corpus is captured live, then replayed offline afterward.

## Replaying a corpus

- Engine CLI: `--replay <dir>` feeds the directory through the scan loop instead of live capture,
  in the same process shape production uses, then exits when the corpus is exhausted.
- SDK test harness: `GameCapture.Sdk.Testing.ReplayHarness.RunAsync` spawns a real `GameCapture.Engine.exe`
  with `--replay <dir> --pipe <generated>`, drives a plugin against it through the plugin's real
  `GameCapturePluginHost` path, and returns every emitted record plus how the run ended
  (`StreamEndReason`, exit code). This is what a plugin's own CI uses for parity, and what every
  replay-parity test in this repo is built on now (TASK-13).

See [`docs/ENGINE-SERVICES.md`](ENGINE-SERVICES.md#replay-mode) for the engine's replay-mode flags
and determinism guarantees, [`docs/PROTOCOL.md`](PROTOCOL.md#backpressure-and-stream-end) for
the replay-vs-live backpressure policy, and
[`docs/PLUGIN-AUTHORING.md`](PLUGIN-AUTHORING.md#7-testing) for writing the parity test itself.

## Backpressure: replay vs. live

The engine's per-client channel picks its full-mode based on whether the session is a replay
(`ClientConnection.cs`): `BoundedChannelFullMode.Wait` in replay, `DropOldest` live. A live client
that falls behind loses old ticks rather than stalling the scan loop; a replay client must never
lose one — parity means every frame in the corpus produces exactly one tick, deterministically,
regardless of how slowly a plugin consumes them.

## Finding the engine binary

`GameCapture.Sdk.Testing.EngineLocator.Resolve()` — the `GAMECAPTURE_ENGINE_PATH` env var if set (CI
pins this to the exact artifact it built), otherwise walks up from the running test assembly to
the solution root looking for `src/GameCapture.Engine/bin`, then picks the newest `GameCapture.Engine.exe`
under it (Release wins ties over Debug). Right for a dev running tests against whatever they last
built locally; set the env var to point at a specific build (CI, or testing a published release).

## Git LFS

Corpora are small PNG sets today (a handful of frames each) and live as ordinary tracked files.
A corpus approaching ~50 MB should move to Git LFS instead of growing the repo's blob history —
not needed yet, but worth doing before capturing anything long or high-resolution.
