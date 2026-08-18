namespace GameCapture.Sdk;

/// <summary>
/// The engine could not be reached, or stopped answering: no pipe on the other end, a dial that
/// ran out its deadline, a handshake nobody replied to. Retryable — this is the exception a host's
/// reconnect loop is for.
/// </summary>
public sealed class EngineUnavailableException : GameCaptureException
{
    public EngineUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
