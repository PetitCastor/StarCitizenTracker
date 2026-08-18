using System.Text.Json;
using GameCapture.Contracts;

namespace GameCapture.Engine;

/// <summary>
/// Engine-side settings only. Everything a plugin decides for itself — which trackers run,
/// where the ledger lives — stays out: the engine is game-agnostic and must not grow knobs
/// that describe what is being tracked.
/// </summary>
public sealed class EngineConfig
{
    public string Hotkey { get; set; } = "Ctrl+Shift+F12";

    /// <summary>Index into the monitor list printed at startup (primary monitor is always index 0).</summary>
    public int MonitorIndex { get; set; } = 0;

    /// <summary>Where frame dumps land. Relative paths resolve against this config file's directory.</summary>
    public string OutputDir { get; set; } = "captures";

    /// <summary>
    /// BCP-47 tag of the OCR recognizer, e.g. "en-US". Empty means "first Windows display
    /// language that has an OCR pack". Windows OCR has no image-based language detection, so
    /// set this when the game's UI language differs from the Windows display language.
    /// </summary>
    public string OcrLanguage { get; set; } = "";

    /// <summary>Named pipe the gRPC server listens on; plugins must use the same name.</summary>
    public string PipeName { get; set; } = PipeContract.DefaultPipeName;

    /// <summary>Scan cadence for the capture loop; values below 100 are clamped up at use.</summary>
    public int ScanIntervalMs { get; set; } = 500;

    /// <summary>Live CPU/memory/GPU status bar at the bottom of the console.</summary>
    public bool MetricsEnabled { get; set; } = true;

    /// <summary>Status bar refresh cadence; values below 250 are clamped up at use.</summary>
    public int MetricsIntervalMs { get; set; } = 1000;

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
    public static EngineConfig Load(string path)
    {
        EngineConfig config;
        if (!File.Exists(path))
        {
            config = new EngineConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        }
        else
        {
            config = JsonSerializer.Deserialize<EngineConfig>(File.ReadAllText(path), JsonOptions)
                     ?? new EngineConfig();
        }

        if (!Path.IsPathRooted(config.OutputDir))
            config.OutputDir = Path.GetFullPath(config.OutputDir, Path.GetDirectoryName(path)!);

        return config;
    }
}
