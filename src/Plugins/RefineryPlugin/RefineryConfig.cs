using System.Text.Json;
using TrackerSdk;

namespace RefineryPlugin;

/// <summary>
/// Plugin-side settings only. Everything about *how* the screen is read — monitor, hotkey, OCR
/// language, scan cadence — belongs to the engine's own config; a plugin that grew those knobs
/// would be describing a capture stack it no longer owns. What is left here is the pipe to dial
/// and where this plugin's own ledger lives.
/// </summary>
public sealed class RefineryConfig
{
    /// <summary>Named pipe the engine listens on; must match the engine's own setting.</summary>
    public string PipeName { get; set; } = EngineDefaults.PipeName;

    /// <summary>Persist observed refinery work orders to an append-only JSONL ledger.</summary>
    public bool LedgerEnabled { get; set; } = true;

    /// <summary>
    /// Ledger file path. Empty ⇒ <c>%LOCALAPPDATA%\StarCitizenTracker\orders.jsonl</c>. A relative
    /// path resolves against this config file's directory; a rooted path is used verbatim. After
    /// <see cref="Load"/> this is always an absolute path.
    /// </summary>
    public string LedgerPath { get; set; } = "";

    /// <summary>
    /// Ask the engine to dump the completed-panel PNG on every emitted order and write the rendered
    /// order beside it. The PNG lands in the *engine's* output dir — the plugin only learns the path.
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
    public static RefineryConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new RefineryConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
            defaults.LedgerPath = ResolveLedgerPath(defaults.LedgerPath, path);
            return defaults;
        }

        var config = JsonSerializer.Deserialize<RefineryConfig>(File.ReadAllText(path), JsonOptions)
                     ?? new RefineryConfig();

        config.LedgerPath = ResolveLedgerPath(config.LedgerPath, path);

        return config;
    }

    /// <summary>
    /// Empty ⇒ the per-user LOCALAPPDATA default; relative ⇒ resolved against the config file's
    /// directory; rooted ⇒ verbatim. An empty value here means the special-folder default, not
    /// "relative to the config dir".
    /// </summary>
    internal static string ResolveLedgerPath(string ledgerPath, string configPath)
    {
        if (string.IsNullOrWhiteSpace(ledgerPath))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StarCitizenTracker", "orders.jsonl");

        if (!Path.IsPathRooted(ledgerPath))
            return Path.GetFullPath(ledgerPath, Path.GetDirectoryName(configPath)!);

        return ledgerPath;
    }
}
