using System.Threading.Channels;
using GameCapture.Contracts.Proto;

namespace GameCapture.Engine;

/// <summary>
/// One connected plugin: the ROI set it subscribed and the queue of ticks waiting to be written
/// back to it. Everything about a client that the scan loop needs lives here, so the loop never
/// touches gRPC types or blocks on a stream write.
/// </summary>
internal sealed class ClientConnection
{
    /// <summary>
    /// Four ticks (~2 s at the default 500 ms cadence). Deep enough to ride out a plugin's
    /// occasional slow parse, shallow enough that a live client which fell behind resumes on
    /// near-current frames instead of replaying stale UI state.
    /// </summary>
    private const int OutboundCapacity = 4;

    private readonly Action<ClientConnection>? _onNameChanged;

    // Full-replacement swap under volatile: the scan loop reads this on every tick while a
    // RoiSetUpdate may be arriving on the request-pump thread. Swapping the whole list (never
    // mutating it in place) is what makes a tick see one coherent ROI set — a partially applied
    // update would silently break per-tick atomicity for that client.
    private volatile IReadOnlyList<RoiSpec> _rois = [];

    private string _name = "?";

    // Volatile for the same reason as _rois: written on the request-pump thread, polled by the
    // scan loop's replay start gate on another.
    private volatile bool _hasSubscribed;

    /// <param name="replayMode">Selects the overflow policy; see <see cref="Out"/>.</param>
    /// <param name="onNameChanged">Invoked when the client's Hello arrives, so the registry can
    /// refresh the status snapshot without the connection knowing about EngineStatus.</param>
    public ClientConnection(bool replayMode, Action<ClientConnection>? onNameChanged = null)
    {
        _onNameChanged = onNameChanged;

        Out = Channel.CreateBounded<TrackResponse>(new BoundedChannelOptions(OutboundCapacity)
        {
            // Live: never stall the scan loop for a slow plugin — drop the oldest tick, because
            // the freshest screen state is the only one worth acting on. Replay: block instead,
            // because a dropped frame changes the outcome and determinism is the whole point.
            FullMode = replayMode ? BoundedChannelFullMode.Wait : BoundedChannelFullMode.DropOldest,
            SingleReader = true, // the Track response writer
            SingleWriter = true, // the scan loop
        });
    }

    /// <summary>
    /// Stable per-connection id. Keyed on rather than <see cref="Name"/> because two instances of
    /// the same plugin must not collapse into one entry in the status snapshot.
    /// </summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Name the client sent in its Hello; "?" until then.</summary>
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            _onNameChanged?.Invoke(this);
        }
    }

    /// <summary>The client's current ROI set. Read once per tick; never mutated in place.</summary>
    public IReadOnlyList<RoiSpec> Rois => _rois;

    /// <summary>
    /// True once any RoiSetUpdate has arrived — including an empty one. Replay uses this as the
    /// start gate, and "subscribed to nothing" is a deliberate client state (heartbeat-only), not
    /// a not-ready-yet one.
    /// </summary>
    public bool HasSubscribed => _hasSubscribed;

    /// <summary>Ticks queued for this client. The scan loop writes; the Track stream reads.</summary>
    public Channel<TrackResponse> Out { get; }

    /// <summary>Applies a full replacement of the ROI set (the update is idempotent by design).</summary>
    public void SetRois(RoiSetUpdate update)
    {
        _rois = update.Rois.ToArray();
        _hasSubscribed = true;
    }
}
