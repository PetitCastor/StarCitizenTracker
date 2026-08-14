using System.Text.Json;

namespace TrackingService;

public sealed class ProbeConfig
{
    public string Hotkey { get; set; } = "Ctrl+Shift+F12";

    /// <summary>Index into the monitor list printed at startup (primary monitor is always index 0).</summary>
    public int MonitorIndex { get; set; } = 0;

    public string OutputDir { get; set; } = "captures";

    /// <summary>Trackers active by default when no --track args are given.</summary>
    public List<string> Trackers { get; set; } = ["missions"];

    /// <summary>
    /// BCP-47 tag of the OCR recognizer, e.g. "en-US". Empty means "first Windows display
    /// language that has an OCR pack". Windows OCR has no image-based language detection, so
    /// set this when the game's UI language differs from the Windows display language.
    /// </summary>
    public string OcrLanguage { get; set; } = "";

    /// <summary>Live CPU/memory/GPU status bar at the bottom of the console (live mode only).</summary>
    public bool MetricsEnabled { get; set; } = true;

    /// <summary>Status bar refresh cadence; values below 250 are clamped up at use.</summary>
    public int MetricsIntervalMs { get; set; } = 1000;

    /// <summary>Persist observed refinery work orders to an append-only JSONL ledger.</summary>
    public bool LedgerEnabled { get; set; } = true;

    /// <summary>
    /// Ledger file path. Empty ⇒ <c>%LOCALAPPDATA%\StarCitizenTracker\orders.jsonl</c>. A relative
    /// path resolves against this config file's directory (like <see cref="OutputDir"/>); a rooted
    /// path is used verbatim. After <see cref="Load"/> this is always an absolute path.
    /// </summary>
    public string LedgerPath { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static ProbeConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new ProbeConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
            defaults.LedgerPath = ResolveLedgerPath(defaults.LedgerPath, path);
            return defaults;
        }

        var config = JsonSerializer.Deserialize<ProbeConfig>(File.ReadAllText(path), JsonOptions)
                     ?? new ProbeConfig();

        if (!Path.IsPathRooted(config.OutputDir))
            config.OutputDir = Path.GetFullPath(config.OutputDir, Path.GetDirectoryName(path)!);

        config.LedgerPath = ResolveLedgerPath(config.LedgerPath, path);

        return config;
    }

    /// <summary>
    /// Empty ⇒ the per-user LOCALAPPDATA default; relative ⇒ resolved against the config file's
    /// directory; rooted ⇒ verbatim. Deliberately not modeled on <see cref="OutputDir"/>'s scheme,
    /// since an empty value here means the special-folder default, not "relative to the config dir".
    /// </summary>
    private static string ResolveLedgerPath(string ledgerPath, string configPath)
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
