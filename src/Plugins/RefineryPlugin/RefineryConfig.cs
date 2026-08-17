using TrackerSdk;

namespace RefineryPlugin;

/// <summary>
/// Plugin-side settings for the refinery plugin. The pipe to dial and whether to save debug frames
/// come from <see cref="PluginConfig"/> — every plugin has them and the loader is shared; what is
/// added here is where this plugin's own order ledger lives. Everything about *how* the screen is
/// read (monitor, hotkey, OCR language, scan cadence) belongs to the engine's own config.
/// </summary>
public sealed class RefineryConfig : PluginConfig
{
    /// <summary>Persist observed refinery work orders to an append-only JSONL ledger.</summary>
    public bool LedgerEnabled { get; set; } = true;

    /// <summary>
    /// Ledger file path. Empty ⇒ <c>%LOCALAPPDATA%\StarCitizenTracker\orders.jsonl</c>. A relative
    /// path resolves against this config file's directory; a rooted path is used verbatim. After the
    /// config is loaded this is always an absolute path.
    /// </summary>
    public string LedgerPath { get; set; } = "";

    /// <summary>
    /// Resolves <see cref="LedgerPath"/> once the values are in place, against the config file's own
    /// directory — the base loader runs this on both the first-run and read-back paths.
    /// </summary>
    protected override void AfterLoad(string configPath)
        => LedgerPath = ResolveLedgerPath(LedgerPath, configPath);

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
