using System.Text.Json;

namespace GameCapture.Sdk;

/// <summary>
/// The settings every plugin has, and the loader all of them were copying. Plugin-side settings
/// only: everything about *how* the screen is read — monitor, hotkey, OCR language, scan cadence —
/// belongs to the engine's own config, and a plugin that grew those knobs would be describing a
/// capture stack it no longer owns.
/// </summary>
/// <remarks>
/// Derive, add fields, and the loader below writes and reads them without further ceremony. The two
/// existing plugins had the same 20-line <c>Load</c> each, differing only in the type it named.
/// </remarks>
public abstract class PluginConfig
{
    /// <summary>Named pipe the engine listens on; must match the engine's own setting.</summary>
    public string PipeName { get; set; } = EngineDefaults.PipeName;

    /// <summary>
    /// Ask the engine to dump a PNG on every capture and write the plugin's rendering beside it.
    /// The PNG lands in the *engine's* output dir — the plugin only learns the path.
    /// </summary>
    public bool SaveDebugFrames { get; set; }

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
    /// <remarks>
    /// A config file that exists but deserialises to null — an empty file, or a bare <c>null</c> —
    /// yields defaults rather than throwing, and deliberately does NOT rewrite the file: the user
    /// put something there, and silently replacing it is how a hand-edited config disappears.
    /// </remarks>
    public static T Load<T>(string path) where T : PluginConfig, new()
    {
        if (!File.Exists(path))
        {
            var defaults = new T();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
            defaults.AfterLoad(path);
            return defaults;
        }

        var config = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? new T();
        config.AfterLoad(path);
        return config;
    }

    /// <summary>
    /// Runs after the values are in place, on both the first-run and the read-back path. Override to
    /// resolve anything that depends on where the config file itself lives — a relative ledger path
    /// against the config's directory, for one. <paramref name="configPath"/> is the file that was
    /// read or written, whether or not it existed a moment ago.
    /// </summary>
    protected virtual void AfterLoad(string configPath) { }
}
