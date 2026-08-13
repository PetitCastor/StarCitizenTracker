# Phase 2 — Event-Tracker Framework + Mission Tracker (C#, in-memory)

## Context

Phase 1 (hotkey WGC capture) complete, live-verified. Side quest PoC validated: ROI crop +
3x upscale + Windows OCR reads `ACCEPTED (n/m)` 100/100 at ~30 ms/scan, fully in-memory.
Merged to `master` (7e9110d).

**Direction change (user, 2026-08-13):** system is an extensible *event-tracker platform*,
not a mission-only scraper. At launch the user selects which trackers run ("track X Y Z").
Each tracker = self-contained feature: own trigger, own ROI(s), own parser, own output sink.
Known future tracker: refinery work-order output (ore type + SCU rows, known-vocabulary OCR
correction, CSV output for Excel). Mission tracker is simply the first implementation.

Core requirement: pipeline stays **in-memory** — frame → buffer → OCR text. Disk writes
(PNG/text evidence) opt-in debug only.

## Architecture

```
src/CaptureProbe/                     (rename/refactor grows in later phase; keep project for now)
  Core/
    MonitorCapture.cs      (exists — armed WGC session, TakeLatestFrame)
    HotkeyListener.cs      (exists — LL keyboard hook)
    OcrPipeline.cs         (NEW — extract from OcrWatcher: ToSoftwareBitmap, CropAndScaleAsync,
                            RecognizeAsync; reusable "OCR this ROI of this frame" service)
    FrameSaver.cs          (exists — debug-only evidence path)
    ProbeConfig.cs         (extend — trackers list, debug flags)
  Trackers/
    ITracker.cs            (NEW — contract, see below)
    TrackerHost.cs         (NEW — poll loop owning the frame cadence; calls each active
                            tracker with the latest frame; routes hotkey presses)
    MissionTracker.cs      (NEW — port side-quest logic: watch ACCEPTED (n/m) ROI; on
                            increment OR hotkey: OCR mission-pane ROI, emit record)
  Program.cs               (wire: config/CLI selects trackers, e.g. --track missions)
```

### ITracker contract (shape, adjust while coding)

```csharp
interface ITracker
{
    string Name { get; }
    // Called ~2 Hz with the latest frame (in-memory). Tracker does its own ROI/OCR via OcrPipeline.
    Task ScanAsync(TrackerFrame frame, CancellationToken ct);
    // Manual trigger fallback — hotkey routed to all active trackers.
    Task OnManualTriggerAsync(TrackerFrame frame, CancellationToken ct);
}
// Emits TrackerRecord { Timestamp, Tracker, Trigger, RawText } to its sink (Phase 2: console
// + optional debug file; refinery CSV sink comes with that tracker).
```

### MissionTracker behavior (Phase 2 concrete)

- Watch trigger ROI: `ACCEPTED (n/m)` tab (proven coords 1000,110 420x100 @1440p, config).
- On increment or hotkey: OCR mission-pane ROI (right pane: title/reward/DETAILS/PRIMARY
  OBJECTIVES; measure from existing evidence PNGs, config-driven, ~X 680..1900 Y 170..960).
- Emit raw OCR text record to console. NO parsing yet (Phase 3: parser per tracker).
- `--save-frames` debug flag: pane PNG + .txt to captures/.

## Out of scope Phase 2

Refinery tracker (Phase 3+ after mission parser proves the pattern), parsing to structured
fields, CSV/xlsx sinks, network POST, tray UI, GPU-side crop optimization
(CopySubresourceRegion — noted for higher cadences, unneeded at 2 Hz).

## Verification

1. Desktop sanity: sim window with "ACCEPTED (1/10)" text (same trick as PoC) → change to
   (2/10) → console shows mission-pane OCR record; captures/ untouched without --save-frames.
2. Live: `--track missions`, accept mission in-game → raw contract text in console ≤1 s.
3. Hotkey produces same record manually.
4. Assess OCR quality of pane text → decides if Phase 3 parser needs preprocessing.

## Done criteria

Tracker framework runs N trackers off one capture stream; MissionTracker delivers raw
mission text from memory alone; adding a future tracker requires only a new ITracker class
+ config entry.

---

## Status: COMPLETE — live-verified 2026-08-13 19:50

- Counter increment 0/10 -> 1/10 detected; mission pane auto-captured ~700 ms later.
- Full contract OCR'd in-memory, no disk: title ("Minor Mining Job [200 Rep]"), work brief,
  payment terms, authorization, reward (25,000), contractor — near-perfect text quality.
  Minor OCR noise only ("pRlMARY", stray "Ž") — Phase 3 parser handles easily. Windows OCR
  raw is sufficient; OpenCV-style preprocessing NOT needed.
- Manual hotkey path verified (desktop smoke test) — emits same record shape.
- Zero files written without --save-frames.
- Pane OCR ~430 ms first capture (JIT), tab scan ~15-25 ms steady state.

### Learnings
- OcrEngine.MaxImageDimension (2600) caps upscale for big ROIs — pipeline clamps scale
  automatically; pane 1560px wide gets ~1.66x effective, plenty.
- First counter sighting is baseline, not an event; only +1 increments trigger capture
  (completions/abandons decrement, pane-open is first sighting).
