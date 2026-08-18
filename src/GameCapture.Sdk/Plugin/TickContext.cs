namespace GameCapture.Sdk;

/// <summary>
/// One tick plus the services to act on it. Two members rather than passing both separately so the
/// plugin signature stays stable as the host grows more to lend.
/// </summary>
public sealed class TickContext
{
    internal TickContext(TickData tick, IPluginServices services)
    {
        Tick = tick;
        Services = services;
    }

    /// <summary>Every reading in here came from the same frame.</summary>
    public TickData Tick { get; }

    /// <summary>Emit, dump, log, and what the engine is.</summary>
    public IPluginServices Services { get; }

    /// <summary>
    /// Builds a context around a tick, for a plugin's own tests. The host uses the internal
    /// constructor; this exists so a plugin can drive <see cref="IGameCapturePlugin.OnTickAsync"/>
    /// without a pipe.
    /// </summary>
    /// <remarks>
    /// TASK-09 moves this to the <c>GameCapture.Sdk.Testing</c> companion package, along with the tick
    /// factory that builds the <see cref="TickData"/> to hand it. It is public here because
    /// <see cref="TickContext"/>'s constructor being internal would otherwise make an
    /// <see cref="IGameCapturePlugin"/> untestable outside this assembly, which is a worse trade for one
    /// task's duration.
    /// </remarks>
    public static TickContext ForTesting(TickData tick, IPluginServices services) => new(tick, services);
}
