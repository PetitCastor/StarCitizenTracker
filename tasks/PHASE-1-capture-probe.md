# Phase 1 — Capture Probe (C#): Live Star Citizen Frame Capture Viability

## Context

Original spec (`Claude_Code_Prompt_Capture_Service.md`) describes a Python tray service that OCRs Star Citizen mobiGlas mission text and POSTs it to a Web API. Direction changed during planning:

- **Language switched to C#/.NET** — user is a C# programmer; capture perf identical (same OS APIs), everything else better (native tray, HttpClient, single-file publish).
- **Iterative dev**: this phase is NOT the tray service. Deliverable = a console app launchable from Visual Studio (F5) that proves live game capture is viable and reliable.
- **Hotkey-triggered capture**: frames are captured only when a configured key combo is pressed (user's choice) — matches the real use case (player opens mobiGlas, presses hotkey).
- **No OCR, no image processing, no game-log parsing** in this phase. Strictly capture.
- Project will be broken into per-phase `.md` task files going forward.

Environment verified: Windows 11 Pro, .NET SDK 10.0.301, primary monitor 2560x1440 (game), secondary 1920x1080.

## Decisions made

- **Capture API**: `Windows.Graphics.Capture` (WGC) — modern supported WinRT API, GPU-based, captures borderless-fullscreen game cleanly. TFM `net10.0-windows10.0.19041.0` gives WinRT projections without extra NuGets.
- **Capture strategy**: capture session stays alive while app runs ("armed"); hotkey press copies the latest frame out of the frame pool. Avoids per-press session startup latency (~100–300 ms) and lets us measure steady-state overhead on the game.
- **Global hotkey**: `RegisterHotKey` Win32 API + hidden message-only window on a dedicated thread (console apps have no message loop). Default combo `Ctrl+Shift+F12`, configurable.
- **Output**: timestamped PNGs + per-press latency stats printed to console.

## Files to create

```
Z:\Projects\Star Citizen\Scraper\
  StarCitizenScraper.sln
  tasks\
    PHASE-1-capture-probe.md      # this phase's task file (spec + acceptance criteria)
  src\CaptureProbe\
    CaptureProbe.csproj           # net10.0-windows10.0.19041.0, AllowUnsafeBlocks if needed
    Program.cs                    # wiring: load config, start capture, run hotkey loop, Ctrl+C exit
    ProbeConfig.cs                # config.json: hotkey, monitorIndex, outputDir
    HotkeyListener.cs             # RegisterHotKey + message-only window thread
    MonitorCapture.cs             # WGC session: GraphicsCaptureItem from HMONITOR, Direct3D11CaptureFramePool
    CaptureInterop.cs             # D3D11 device creation + IGraphicsCaptureItemInterop + IDirect3DDxgiInterfaceAccess (standard MS sample interop)
    FrameSaver.cs                 # GPU texture -> staging texture -> CPU bytes -> PNG (WIC or System.Drawing)
    config.json                   # copied to output dir
  captures\                       # PNG output (gitignored later)
```

## Implementation notes

1. **CaptureProbe.csproj**: `OutputType=Exe`, `TargetFramework=net10.0-windows10.0.19041.0`, `PlatformTarget=x64`. No external NuGets expected (WinRT projections built in; D3D11 via `DirectN`-style hand-rolled P/Invoke or minimal `Vortice.Direct3D11` NuGet if hand-rolling gets long — prefer minimal hand-rolled interop copied from Microsoft's WGC screenshot sample pattern).
2. **MonitorCapture**: enumerate monitors (`EnumDisplayMonitors`), pick `monitorIndex` (default = primary 1440p), create `GraphicsCaptureItem` via `IGraphicsCaptureItemInterop.CreateForMonitor`. `Direct3D11CaptureFramePool.CreateFreeThreaded`, 2 buffers, `B8G8R8A8UIntNormalized`. Keep latest frame reference; hotkey handler snapshots it.
3. **Capture border**: set `GraphicsCaptureSession.IsBorderRequired = false` (works unpackaged on Win11); if OS refuses, log and continue — cosmetic only. Also disable `IsCursorCaptureEnabled`.
4. **Hotkey press flow**: press → grab latest frame → copy to staging texture → `Map` → save PNG (`captures\capture_YYYYMMDD_HHmmss_fff.png`) → print line: press #, hotkey-to-saved latency ms, frame size.
5. **Console output**: startup banner (monitor, resolution, hotkey), one line per press, summary on exit (press count, failures, min/avg/max latency).
6. **PHASE-1-capture-probe.md**: goal, how to run, acceptance criteria (below), findings section to fill after live test.

## Verification (live test — user drives game)

1. `dotnet build` clean; F5 from Visual Studio runs the probe.
2. Star Citizen running **borderless** on primary monitor.
3. Press hotkey with mobiGlas mission open → PNG appears in `captures\`, mobiGlas text readable at 2560x1440.
4. ~20 presses across different scenes (menu, flight, ground): 0 failed captures, latency reported each press.
5. Confirm no perceptible game FPS drop while probe armed (WGC steady-state cost).
6. Without game (desktop only) probe also works — sanity path for dev without SC running.

## Risks

- WGC yellow capture border if `IsBorderRequired=false` rejected → cosmetic; fallback phase option: DXGI Desktop Duplication.
- HDR enabled on monitor → washed-out PNGs; if so, capture as `R16G16B16A16Float` + tonemap later, or user disables HDR for the game. Note in findings.
- Exclusive fullscreen may not capture via WGC → require borderless (SC default).

## Out of scope (later phases)

Tray UI, OCR, OpenCV preprocessing, regex parsing, Web API POST, packaging, game-log reading (explicitly excluded by user).

---

## Status: COMPLETE — live in-game capture VERIFIED (2026-08-13)

Phase 1 done. Hotkey capture works with Star Citizen focused (borderless, primary monitor).
Required switching the hotkey listener from RegisterHotKey to a low-level keyboard hook
(WH_KEYBOARD_LL) — SC reads keyboard via raw input and starves RegisterHotKey even when
the probe runs elevated. No elevation needed with the LL hook.

### How to run

- Open `StarCitizenScraper.slnx` in Visual Studio, F5 (CaptureProbe project). Or `dotnet run --project src\CaptureProbe`.
- Config: `src\CaptureProbe\config.json` (copied to output dir). Hotkey default `Ctrl+Shift+F12`, monitorIndex 0 = primary.
- Press hotkey anywhere (global) → PNG in `captures\`. Ctrl+C in console → summary stats.

### Smoke test findings (2026-08-13, desktop, no game)

- Solution format: `.slnx` (.NET 10 default), not `.sln`.
- Capture works: 2560x1440 PNG, crisp text, ~200 KB desktop screenshot.
- Latency: ~95 ms first press (JIT warm-up), ~46 ms after. Frame age at press ~82 ms on idle desktop (WGC only delivers frames on screen change — expected; in-game will be ≤1 frame at 60fps).
- Yellow capture border: `IsBorderRequired=false` + `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` both denied — unpackaged apps can't remove it. Cosmetic only. Options later: package with identity (MSIX/sparse manifest) or switch to DXGI Desktop Duplication.
- Synthetic hotkey via SendKeys triggers global RegisterHotKey — usable for automated tests.

### Remaining acceptance criteria (needs user + running game)

- [ ] SC borderless on primary monitor: hotkey with mobiGlas open → mobiGlas text readable in PNG.
- [ ] ~20 presses across scenes (menu, flight, ground): 0 failures, latencies logged.
- [ ] No perceptible game FPS drop while probe armed.
- [ ] Note HDR behavior if enabled (washed-out PNGs → revisit pixel format).
