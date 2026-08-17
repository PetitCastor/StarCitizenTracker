using TrackerSdk;

namespace MissionPlugin;

/// <summary>
/// Plugin-side settings only. Everything about *how* the screen is read — monitor, hotkey, OCR
/// language, scan cadence — belongs to the engine's own config; a plugin that grew those knobs
/// would be describing a capture stack it no longer owns.
/// </summary>
public sealed class MissionConfig : PluginConfig;
