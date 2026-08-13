# Next Session — Where We Are & What's Next

Updated: 2026-08-13 evening.

## State

- **Phase 1 COMPLETE** (`tasks/PHASE-1-capture-probe.md`): WGC capture + LL keyboard hook
  hotkey (RegisterHotKey useless — SC raw input starves it; no elevation needed with hook).
- **Side quest VALIDATED** (`tasks/SIDE-QUEST-auto-capture-triggers.md`): ROI + upscale +
  Windows OCR detects ACCEPTED (n/m) counter changes, 100% reads, ~30 ms/scan.
- **Phase 2 COMPLETE** (`tasks/PHASE-2-tracker-framework.md`): ITracker/TrackerHost
  framework, MissionTracker auto-captures full mission text in-memory on accept.
  Live-verified. Key learning: **Windows OCR raw is enough — no OpenCV preprocessing
  phase needed.**
- Repo: master branch, all work committed (git ops via Haiku subagent — user rule).
- Run: `dotnet run --project src\CaptureProbe` or F5. Flags: `--track missions`,
  `--verbose`, `--save-frames`. Config: `src\CaptureProbe\config.json`.

## Phase 3 — Mission Parser (next up)

Turn MissionTracker's raw OCR text into structured record:
- Title (first line, strip `[200 Rep] [BP]*` suffixes), Reward (aUEC number near "Reward"),
  Contractor ("Contracted By"), Objectives (PRIMARY OBJECTIVES / numbered RS targets),
  Deadline.
- OCR noise handling: case glitches ("pRlMARY"), stray unicode ("Ž") — normalize before
  regex; known-vocabulary correction pattern ready for reuse.
- No live game needed: iterate against saved samples — run once with `--save-frames` to
  collect .txt corpus, or use sample in PHASE-2 doc.
- Output: per-tracker sink abstraction (console/JSON file first; CSV for Excel-bound
  trackers; Web API POST = later phase).

## Phase 4+ backlog

- **Refinery tracker** (user's Excel use case): trigger = work-order/delivery pane; parser =
  ore rows (Quantanium, Laranite, ... dictionary for OCR correction); sink = CSV append
  (Excel-native), ClosedXML .xlsx later. All slots into existing ITracker.
- **Multi-resolution ROIs**: coords hardcoded for 2560x1440 in MissionTracker — scale
  factors or per-resolution config.
- **Tray service** (original spec Phase 1): pystray equivalent = WinForms NotifyIcon;
  console host becomes background app, trackers toggled from tray menu.
- **Web API POST** (original spec Phase 5): HttpClient sink.
- **Yellow capture border**: unpackaged apps can't disable (both APIs denied). Fix via
  MSIX/sparse package identity, or DXGI Desktop Duplication swap. Cosmetic only.
- **GPU-side ROI crop** (CopySubresourceRegion) if scan cadence ever needs >2 Hz.

## Constraints to remember

- No Game.log reading — screen capture only (user rule, restated twice).
- All git actions via lesser model (Haiku agent).
- Iterative phases, each a tasks/*.md file, live-tested in-game before "complete".
