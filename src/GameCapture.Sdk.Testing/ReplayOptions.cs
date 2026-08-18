namespace GameCapture.Sdk.Testing;

/// <summary>The knobs on <see cref="ReplayHarness.RunAsync"/>.</summary>
public sealed class ReplayOptions
{
    /// <summary>Path to a built <c>GameCapture.Engine.exe</c>. Use <see cref="EngineLocator.Resolve"/>.</summary>
    public required string EnginePath { get; init; }

    /// <summary>A directory of PNGs the engine replays, in the format <c>ReplayFrameSource</c> reads.</summary>
    public required string CorpusDir { get; init; }

    /// <summary>The plugin under test, driven through its real <see cref="GameCapturePluginHost"/> path.</summary>
    public required IGameCapturePlugin Plugin { get; init; }

    /// <summary>
    /// A hang bound, not a performance budget. Fired means the engine never came up, the corpus
    /// never exhausted, or the plugin never returned — all of which are bugs, not slow runs.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}
