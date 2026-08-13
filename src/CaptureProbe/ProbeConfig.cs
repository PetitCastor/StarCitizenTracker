using System.Text.Json;

namespace CaptureProbe;

public sealed class ProbeConfig
{
    public string Hotkey { get; set; } = "Ctrl+Shift+F12";

    /// <summary>Index into the monitor list printed at startup (primary monitor is always index 0).</summary>
    public int MonitorIndex { get; set; } = 0;

    public string OutputDir { get; set; } = "captures";

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
            return defaults;
        }

        var config = JsonSerializer.Deserialize<ProbeConfig>(File.ReadAllText(path), JsonOptions)
                     ?? new ProbeConfig();

        if (!Path.IsPathRooted(config.OutputDir))
            config.OutputDir = Path.GetFullPath(config.OutputDir, Path.GetDirectoryName(path)!);

        return config;
    }
}
