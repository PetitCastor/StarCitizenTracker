using RefineryPlugin;
using GameCapture.Sdk;

// The whole lifecycle — connect, subscribe, feed ticks, reconnect, summarise — is the host's; this
// process only supplies the plugin, its config, and the one flag the host does not know about.
var config = PluginConfig.Load<RefineryConfig>(Path.Combine(AppContext.BaseDirectory, "config.json"));

// Captured by the arg handler below and read lazily on the first connect (RefineryPlugin resolves
// its ledger target only once the engine has said whether it is replaying). -1/null when absent; a
// flag with nothing after it is a typo worth reporting, since silently falling back to the config
// value would write a different ledger than the one the user just named.
string? ledgerOverride = null;

var options = new PluginHostOptions
{
    Config = config,
    ExtraArgHandler = argv =>
    {
        var i = -1;
        for (var k = 0; k < argv.Count; k++)
        {
            if (!argv[k].Equals("--ledger", StringComparison.OrdinalIgnoreCase))
                continue;
            i = k;
            break;
        }

        if (i < 0)
            return null;

        if (i + 1 >= argv.Count)
            return "--ledger needs a file path after it.";

        var value = argv[i + 1];
        if (string.IsNullOrWhiteSpace(value))
            // A blank path beats the config value and then fails deep inside the first append, as an
            // ArgumentException the ledger's IO catch does not cover — a tick loop that reports a
            // failure twice a second and never records an order.
            return "--ledger needs a non-blank file path.";

        ledgerOverride = value;
        return null;
    },
};

return await GameCapturePluginHost.RunAsync(new RefineryPlugin.RefineryPlugin(config, () => ledgerOverride), args, options);
