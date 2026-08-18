namespace GameCapture.Sdk;

/// <summary>
/// Something that happened to the session rather than on a tick. A plugin that only parses screens
/// can ignore every one of them; a plugin that keeps a state machine usually cannot, because the
/// events are exactly the moments where its assumption "I saw every frame since I started" stops
/// holding.
/// </summary>
public abstract record SessionEvent
{
    /// <summary>
    /// Non-public constructor: the nested records below are the whole set, and a plugin's switch
    /// over them is meant to be exhaustive today even though C# will not prove it.
    /// </summary>
    private protected SessionEvent() { }

    /// <summary>Subscribed and receiving. Raised once per connect, so a reconnect raises it again.</summary>
    public sealed record Connected(EngineInfo Engine) : SessionEvent;

    /// <summary>
    /// The session dropped and the host is about to dial again. <paramref name="Attempt"/> counts
    /// from 1 within the current disconnected stretch and resets on the next
    /// <see cref="Connected"/> — a plugin uses it to escalate its own logging, not to decide whether
    /// to keep going, which is the host's call.
    /// </summary>
    public sealed record Reconnecting(int Attempt) : SessionEvent;

    /// <summary>
    /// Frames were scanned that this plugin never saw, as proven by a jump in
    /// <see cref="TickData.FrameSeq"/>. <paramref name="Gap"/> is how many are missing.
    /// </summary>
    /// <remarks>
    /// The engine drops ticks for a client that cannot keep up rather than blocking every other
    /// client behind it, so this is a normal-if-unwelcome event, not a transport failure. It matters
    /// because a tracker watching for an edge — a counter incrementing, a panel appearing — can miss
    /// the edge entirely across a gap, and the honest response is usually to treat the next tick as
    /// a fresh observation rather than as the successor of the last one.
    /// </remarks>
    public sealed record TicksDropped(ulong Gap) : SessionEvent;

    /// <summary>The run is over; the host is about to print its summary and return.</summary>
    public sealed record Ended(StreamEndReason Reason) : SessionEvent;
}
