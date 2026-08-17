using System.Text.Json;
using TrackerSdk;

namespace MissionPlugin;

/// <summary>
/// Plugin-side settings only. Everything about *how* the screen is read — monitor, hotkey, OCR
/// language, scan cadence — belongs to the engine's own config; a plugin that grew those knobs
/// would be describing a capture stack it no longer owns.
/// </summary>
public sealed class MissionConfig
{
    /// <summary>Named pipe the engine listens on; must match the engine's own setting.</summary>
    public string PipeName { get; set; } = EngineDefaults.PipeName;

    /// <summary>
    /// Ask the engine to dump the pane PNG on every capture and write the OCR text beside it.
    /// The PNG lands in the *engine's* output dir — the plugin only learns the path.
    /// </summary>
    public bool SaveDebugFrames { get; set; } = false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Loads the config, writing a defaults file on first run so the settings are discoverable
    /// without documentation. Same contract as the monolith's ProbeConfig.Load.
    /// </summary>
    public static MissionConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new MissionConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
            return defaults;
        }

        return JsonSerializer.Deserialize<MissionConfig>(File.ReadAllText(path), JsonOptions)
               ?? new MissionConfig();
    }
}
