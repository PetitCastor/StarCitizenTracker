using GameCapture.Sdk;

// The whole lifecycle — connect, subscribe, feed ticks, reconnect, summarise — is the host's; this
// process only supplies the plugin. The host loads config.json itself (PluginHostOptions.ConfigFileName
// defaults to "config.json") for the two settings every plugin has: pipeName and saveDebugFrames.
return await GameCapturePluginHost.RunAsync(new MyCapturePlugin.CounterPlugin(), args);
