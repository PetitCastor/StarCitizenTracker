using Xunit;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// The shared command line. The error messages are asserted verbatim because they are the ones both
/// plugins already printed — a user who mistyped a flag has seen these exact strings, and the point
/// of centralising the parsing was to keep them, not to rewrite them.
/// </summary>
public class PluginArgsTests
{
    private const string ConfigPipe = "config-pipe";

    [Fact]
    public void NoArgs_TakesThePipeFromConfig()
    {
        var parsed = PluginArgs.Parse([], ConfigPipe, out var error);

        Assert.NotNull(parsed);
        Assert.Null(error);
        Assert.Equal(ConfigPipe, parsed.PipeName);
        Assert.False(parsed.Verbose);
    }

    [Fact]
    public void PipeFlag_OverridesConfig()
    {
        var parsed = PluginArgs.Parse(["--pipe", "cli-pipe"], ConfigPipe, out _);

        Assert.NotNull(parsed);
        Assert.Equal("cli-pipe", parsed.PipeName);
    }

    [Fact]
    public void PipeFlag_IsCaseInsensitive()
    {
        var parsed = PluginArgs.Parse(["--PIPE", "cli-pipe"], ConfigPipe, out _);

        Assert.NotNull(parsed);
        Assert.Equal("cli-pipe", parsed.PipeName);
    }

    /// <summary>
    /// A flag with nothing after it is a typo worth reporting: silently falling back to the config
    /// value would connect to a different engine than the one the user just named.
    /// </summary>
    [Fact]
    public void PipeFlag_WithoutAValue_IsAUsageError()
    {
        var parsed = PluginArgs.Parse(["--pipe"], ConfigPipe, out var error);

        Assert.Null(parsed);
        Assert.Equal("--pipe needs a pipe name after it.", error);
    }

    [Fact]
    public void BlankConfigPipe_AndNoFlag_IsAUsageError()
    {
        var parsed = PluginArgs.Parse([], "   ", out var error);

        Assert.Null(parsed);
        Assert.Equal("Pipe name must not be blank (set \"pipeName\" in config.json or pass --pipe).",
            error);
    }

    [Theory]
    [InlineData("--verbose")]
    [InlineData("--VERBOSE")]
    public void VerboseFlag_IsRecognisedInAnyCase(string flag)
    {
        var parsed = PluginArgs.Parse([flag], ConfigPipe, out _);

        Assert.NotNull(parsed);
        Assert.True(parsed.Verbose);
    }

    /// <summary>
    /// The host consumes nothing, so a plugin's own flags travel straight past it — which is what
    /// makes <see cref="PluginHostOptions.ExtraArgHandler"/> able to read the same list.
    /// </summary>
    [Fact]
    public void UnknownFlags_AreIgnoredRatherThanRejected()
    {
        var parsed = PluginArgs.Parse(["--ledger", "x.jsonl", "--pipe", "p"], ConfigPipe, out var error);

        Assert.NotNull(parsed);
        Assert.Null(error);
        Assert.Equal("p", parsed.PipeName);
    }
}
