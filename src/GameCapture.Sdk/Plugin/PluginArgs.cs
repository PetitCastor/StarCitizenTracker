namespace GameCapture.Sdk;

/// <summary>
/// The command line every plugin shares: <c>--pipe &lt;name&gt;</c> and <c>--verbose</c>. Parsed
/// once here rather than re-parsed per plugin, error messages included — those were already
/// character-for-character identical across both plugins, which is what made them worth extracting
/// rather than the parsing itself.
/// </summary>
internal sealed class PluginArgs
{
    private PluginArgs(string pipeName, bool verbose)
    {
        PipeName = pipeName;
        Verbose = verbose;
    }

    public string PipeName { get; }
    public bool Verbose { get; }

    /// <summary>
    /// Parses against <paramref name="configPipeName"/> as the fallback. Returns null and sets
    /// <paramref name="error"/> on a usage error; the messages are the existing ones verbatim,
    /// because they are what a user who mistyped a flag has already seen.
    /// </summary>
    public static PluginArgs? Parse(string[] args, string configPipeName, out string? error)
    {
        error = null;

        var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);

        // -1 when absent. A flag with nothing after it is a typo worth reporting: silently falling
        // back to the config value would connect to a different engine than the one the user just
        // named.
        var pipeArg = Array.FindIndex(args, a => a.Equals("--pipe", StringComparison.OrdinalIgnoreCase));
        if (pipeArg >= 0 && pipeArg + 1 >= args.Length)
        {
            error = "--pipe needs a pipe name after it.";
            return null;
        }

        var pipeName = pipeArg >= 0 ? args[pipeArg + 1] : configPipeName;
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            error = "Pipe name must not be blank (set \"pipeName\" in config.json or pass --pipe).";
            return null;
        }

        return new PluginArgs(pipeName, verbose);
    }
}
