namespace GameCapture.Sdk;

/// <summary>
/// The session died in a way that is neither "no engine" nor "wrong version": the stream faulted,
/// or the engine broke its own handshake contract. Distinct from
/// <see cref="EngineUnavailableException"/> because a reconnect is a guess here, not a remedy.
/// </summary>
public sealed class SessionFaultedException : GameCaptureException
{
    public SessionFaultedException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
