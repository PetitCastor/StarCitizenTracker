using System.Reflection;
using CaptureContracts.Proto;

namespace CaptureEngine;

/// <summary>
/// The engine's observable state, shared between the scan loop (writer), the subscription
/// registry (writer) and any number of concurrent GetStatus calls (readers).
/// </summary>
/// <remarks>
/// One lock rather than Interlocked per field: a status snapshot that mixed the width of one
/// frame with the height of the next would be a confusing lie in a diagnostic surface, and the
/// call rate is a human pressing enter, not the scan loop.
/// </remarks>
public sealed class EngineStatus
{
    private static readonly string Version =
        typeof(EngineStatus).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(EngineStatus).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private readonly Lock _gate = new();
    private readonly SortedSet<string> _clients = new(StringComparer.Ordinal);

    private uint _frameWidth;
    private uint _frameHeight;
    private ulong _frameSeq;

    public EngineStatus(string ocrLanguage, bool replayMode)
    {
        OcrLanguage = ocrLanguage;
        ReplayMode = replayMode;
    }

    /// <summary>BCP-47 tag of the recognizer in use; fixed for the process lifetime.</summary>
    public string OcrLanguage { get; }

    /// <summary>True when frames come from a PNG corpus instead of live capture.</summary>
    public bool ReplayMode { get; }

    /// <summary>Records the frame the scan loop just processed (TASK-3 calls this per tick).</summary>
    public void OnFrame(uint width, uint height, ulong seq)
    {
        lock (_gate)
        {
            _frameWidth = width;
            _frameHeight = height;
            _frameSeq = seq;
        }
    }

    /// <summary>Registers a connected plugin by the name it sent in its Hello.</summary>
    public void AddClient(string clientName)
    {
        lock (_gate)
            _clients.Add(clientName);
    }

    public void RemoveClient(string clientName)
    {
        lock (_gate)
            _clients.Remove(clientName);
    }

    public StatusResponse Snapshot()
    {
        var response = new StatusResponse
        {
            EngineVersion = Version,
            ReplayMode = ReplayMode,
            OcrLanguage = OcrLanguage,
        };

        lock (_gate)
        {
            response.FrameWidth = _frameWidth;
            response.FrameHeight = _frameHeight;
            response.FrameSeq = _frameSeq;
            response.ConnectedClients.AddRange(_clients);
        }

        return response;
    }
}
