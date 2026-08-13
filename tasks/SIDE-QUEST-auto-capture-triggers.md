# Side Quest — Auto-Capture Trigger Evaluation

Status: PoC VALIDATED 2026-08-13 — live in-game detection of ACCEPTED (1/10) -> (2/10) works.
Hotkey remains primary trigger for Phase 1.

## PoC results (branch feature/sidequest-poc)

`CaptureProbe --watch [--verbose]` polls the WGC stream at ~2 Hz, crops a 420x100 ROI around
the contract manager "ACCEPTED (n/m)" tab (2560x1440 coords: 1000,110), upscales 3x, runs
built-in Windows.Media.Ocr, regexes the counter, logs changes + saves evidence PNG.

- Live test: (1/10) -> (2/10) detected the moment the mission was accepted; evidence PNG
  shows the full contract details on screen at that instant — ideal capture trigger timing.
- 100/100 scans read the counter correctly; ~30 ms OCR per scan (80-100 ms first scan, JIT).
- Full-screen OCR at 1:1 does NOT find the tab text — ROI crop + upscale is required.
- No game files touched (Game.log exclusion respected) — pure screen capture.

Implication for trigger design: watch the tab counter, not the toast. Counter increment =
mission accepted while contract pane still open with all details visible. ROI coords are
resolution-dependent — needs scaling for other resolutions later.

## Options ranked

### 1. Toast watcher (recommended for Phase 2+)
Accepting a contract pops a "Contract Accepted" notification toast (top-right area).
- Frames already arrive from the armed WGC session — trigger adds only ROI analysis.
- Pipeline: crop ~400x150 ROI at 1–2 Hz → cheap pre-filter (frame diff / color mask) →
  tiny OCR confirm ("ACCEPTED") → full-frame capture burst.
- Cost negligible; no game-file access; fits current probe architecture (trigger = second
  producer feeding same capture path as hotkey).
- Risks: toast may appear after mobiGlas closes (mitigate: rolling frame buffer, pick frame
  with contract pane visible); false positives from other toasts (OCR confirm handles);
  UI patches / localization break templates.

### 2. Contract-pane watcher
Detect contract manager UI open via template match at low Hz; while open, capture on
pane-region frame diff, debounced.
- Pro: captures while details are on screen. Also captures browsed (not accepted) missions —
  feature or noise depending on goals.

### 3. Game.log tail (excluded by user for now)
Community tools live-parse `Game.log`; mission events appear there. Deterministic, exact
timing, zero false positives. Cleanest long-term trigger — could combine: log event triggers
visual capture. Revisit only if user lifts exclusion.

### 4. Rejected
- Audio cue fingerprinting — fragile, patch-sensitive.
- Raw input hooks — click ≠ accept, no UI context.
- Fixed-interval capture — wasteful, misses timing.

## Data needed from Phase 1 live test
- Hotkey-capture the accept moment: exact toast text, position, duration at 2560x1440.
- Does the contract pane stay open after clicking Accept?
- These captures give the ROI coordinates and template samples for free.
