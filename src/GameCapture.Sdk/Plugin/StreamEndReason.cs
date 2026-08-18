namespace GameCapture.Sdk;

/// <summary>Why the run finished.</summary>
public enum StreamEndReason
{
    /// <summary>The engine was replaying a corpus and reached the end of it.</summary>
    ReplayCompleted,

    /// <summary>A live engine completed the stream on its way down.</summary>
    EngineShutdown,

    /// <summary>
    /// The session failed in a way a reconnect cannot fix — a protocol the engine refuses, most
    /// likely. The host exits non-zero on this one.
    /// </summary>
    Faulted,

    /// <summary>Ctrl+C, or whatever token the embedding host passed in.</summary>
    Cancelled,
}
