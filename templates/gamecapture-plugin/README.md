# MyCapturePlugin

A [GameCapture](https://github.com/PetitCastor/StarCitizenTracker) plugin: a console process that
declares screen regions (ROIs) and what to do with the OCR result each time a tick carrying them
arrives. It never captures a frame, never runs OCR, and never speaks gRPC — `GameCapturePluginHost`
(from `GameCapture.Sdk`) owns connecting, subscribing, reconnecting, and shutdown.

## Calibrate your ROIs first

`MyCapturePlugin.cs` ships with one placeholder region (`Rois.Counter`) pointed at nothing in
particular. Before writing any tracking logic, find your own region's real coordinates:

1. Get an engine running — either a
   [release zip](https://github.com/PetitCastor/StarCitizenTracker/releases)
   (`GameCapture.Engine-vX.Y.Z-win-x64.zip`) or a clone of the engine repo built locally.
2. Set `"saveDebugFrames": true` in `config.json`.
3. Run the plugin with `--verbose` against the running engine and press the engine's capture
   hotkey (`engine-config.json`'s `hotkey`, default `Ctrl+Shift+F12`) while the screen you care
   about is up.
4. Compare the dumped PNG (path printed by `--verbose`) against `Rois.Counter`'s rectangle — it's
   declared in **reference space, 2560x1440, always** — and nudge `RoiRect(x, y, width, height)`
   and `Scale` until the crop lands on your text. Small UI text usually needs a `Scale` of 2-4.
5. Repeat for every region your tracker needs, then replace the counter-change logic in
   `OnTickAsync` with your own.

Full walkthrough — ROI kinds, scale, error handling, session events, testing, config/CLI — in the
hosted plugin-authoring guide:
[`docs/PLUGIN-AUTHORING.md`](https://github.com/PetitCastor/StarCitizenTracker/blob/master/docs/PLUGIN-AUTHORING.md).

## Build & test

```powershell
dotnet build
dotnet test tests/MyCapturePlugin.Tests.csproj --filter "Category!=Integration"
```

The one test tagged `Integration` is skipped until you have a replay corpus — see
[`docs/REPLAY.md`](https://github.com/PetitCastor/StarCitizenTracker/blob/master/docs/REPLAY.md)
and the `[Fact(Skip = ...)]` in `tests/MyCapturePluginTests.cs` for what to fill in.

## Run

```powershell
dotnet run --project . -- --verbose
```

Needs a `GameCapture.Engine` running first, listening on the pipe name in `config.json`
(`pipeName`, must match the engine's).
