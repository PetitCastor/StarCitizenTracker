using System.Text.Json;
using Xunit;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// The loader both plugins were carrying a copy of. Its whole contract is the first run: a plugin
/// with no config file must end up with one, containing the defaults, so the settings are
/// discoverable without documentation.
/// </summary>
public class PluginConfigTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"sc-cfg-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path_(string name) => Path.Combine(_dir, name);

    private sealed class TestConfig : PluginConfig
    {
        public string LedgerPath { get; set; } = "";

        /// <summary>Records that the hook ran, and what it was told the config's own path was.</summary>
        internal string? ResolvedAgainst { get; private set; }

        protected override void AfterLoad(string configPath) => ResolvedAgainst = configPath;
    }

    [Fact]
    public void FirstRun_WritesADefaultsFile()
    {
        var path = Path_("config.json");

        var config = PluginConfig.Load<TestConfig>(path);

        Assert.True(File.Exists(path));
        Assert.Equal(EngineDefaults.PipeName, config.PipeName);
        Assert.False(config.SaveDebugFrames);
    }

    /// <summary>
    /// Camel-cased and indented, because a user is expected to open and edit this file — and because
    /// the existing plugins' config.json files are already in that shape.
    /// </summary>
    [Fact]
    public void FirstRun_WritesReadableCamelCasedJson()
    {
        var path = Path_("config.json");
        PluginConfig.Load<TestConfig>(path);

        var text = File.ReadAllText(path);

        Assert.Contains("\"pipeName\"", text);
        Assert.Contains("\"saveDebugFrames\"", text);
        Assert.Contains(Environment.NewLine, text);
    }

    [Fact]
    public void FirstRun_RoundTripsThroughTheFileItJustWrote()
    {
        var path = Path_("config.json");
        PluginConfig.Load<TestConfig>(path);

        var reloaded = PluginConfig.Load<TestConfig>(path);

        Assert.Equal(EngineDefaults.PipeName, reloaded.PipeName);
    }

    [Fact]
    public void ExistingFile_IsRead_AndDerivedFieldsComeWithIt()
    {
        var path = Path_("config.json");
        File.WriteAllText(path, """
            { "pipeName": "custom", "saveDebugFrames": true, "ledgerPath": "orders.jsonl" }
            """);

        var config = PluginConfig.Load<TestConfig>(path);

        Assert.Equal("custom", config.PipeName);
        Assert.True(config.SaveDebugFrames);
        Assert.Equal("orders.jsonl", config.LedgerPath);
    }

    [Fact]
    public void PropertyNames_AreCaseInsensitive()
    {
        var path = Path_("config.json");
        File.WriteAllText(path, """{ "PipeName": "custom" }""");

        Assert.Equal("custom", PluginConfig.Load<TestConfig>(path).PipeName);
    }

    /// <summary>
    /// A file that deserialises to null — an empty file, or a bare <c>null</c> — yields defaults
    /// rather than throwing, and deliberately does NOT get rewritten: the user put something there,
    /// and silently replacing it is how a hand-edited config disappears.
    /// </summary>
    [Fact]
    public void NullContent_YieldsDefaultsWithoutRewritingTheFile()
    {
        var path = Path_("config.json");
        File.WriteAllText(path, "null");

        var config = PluginConfig.Load<TestConfig>(path);

        Assert.Equal(EngineDefaults.PipeName, config.PipeName);
        Assert.Equal("null", File.ReadAllText(path));
    }

    [Fact]
    public void MalformedJson_Throws()
    {
        var path = Path_("config.json");
        File.WriteAllText(path, "{ not json");

        // Louder than a silent fall back to defaults on purpose: a config the user edited into
        // invalidity must not run as though they had never edited it.
        Assert.Throws<JsonException>(() => PluginConfig.Load<TestConfig>(path));
    }

    [Fact]
    public void AfterLoad_RunsOnTheFirstRunPath()
    {
        var path = Path_("config.json");

        var config = PluginConfig.Load<TestConfig>(path);

        Assert.Equal(path, config.ResolvedAgainst);
    }

    [Fact]
    public void AfterLoad_RunsOnTheReadBackPath()
    {
        var path = Path_("config.json");
        File.WriteAllText(path, """{ "pipeName": "custom" }""");

        var config = PluginConfig.Load<TestConfig>(path);

        Assert.Equal(path, config.ResolvedAgainst);
    }

    /// <summary>
    /// The first-run file is written BEFORE <c>AfterLoad</c> resolves anything, so a path the hook
    /// expands to an absolute location is not what lands on disk. That ordering is deliberate — it is
    /// what RefineryConfig.Load already did — because the written file is meant to show the user the
    /// setting as they would type it, not as the process resolved it.
    /// </summary>
    [Fact]
    public void FirstRun_WritesTheUnresolvedValue()
    {
        var path = Path_("config.json");
        PluginConfig.Load<ResolvingConfig>(path);

        Assert.Contains("\"ledgerPath\": \"\"", File.ReadAllText(path));
    }

    private sealed class ResolvingConfig : PluginConfig
    {
        public string LedgerPath { get; set; } = "";

        protected override void AfterLoad(string configPath)
            => LedgerPath = Path.Combine(Path.GetDirectoryName(configPath)!, "resolved.jsonl");
    }
}
