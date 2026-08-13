# Side Quest — Auto-Capture Trigger Evaluation

Status: evaluated 2026-08-13, no implementation. Hotkey remains primary trigger for Phase 1.

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
