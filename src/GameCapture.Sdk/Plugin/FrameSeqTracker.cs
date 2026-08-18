namespace GameCapture.Sdk;

/// <summary>
/// Watches <see cref="TickData.FrameSeq"/> for frames this client never received.
/// </summary>
/// <remarks>
/// Its own type, and unit-tested as one, because every interesting case here is a boundary the
/// integration tests cannot reach on demand: the first tick of a session (nothing to compare
/// against), the first tick after a reconnect (the engine's counter kept running while nobody was
/// listening), and a sequence that goes backwards (a restarted engine counting from zero again).
/// Provoking those against a live engine means racing it.
/// </remarks>
internal sealed class FrameSeqTracker
{
    private ulong? _last;

    /// <summary>
    /// Records <paramref name="seq"/> and reports how many frames were skipped to reach it.
    /// </summary>
    /// <returns>
    /// True when frames were missed, with <paramref name="gap"/> set to how many. False — gap 0 —
    /// for a contiguous tick, for the first tick observed, and for a sequence that did not advance
    /// or ran backwards.
    /// </returns>
    /// <remarks>
    /// A sequence going backwards is a fresh engine, not a gap: the counter is per engine process,
    /// so a restart mid-run replays low numbers the client has already seen. Reporting that as a
    /// negative gap would be nonsense and reporting it as a huge one — the unsigned subtraction, if
    /// nobody thought about it — would be worse.
    /// </remarks>
    public bool TryObserve(ulong seq, out ulong gap)
    {
        gap = 0;
        var previous = _last;
        _last = seq;

        if (previous is not { } last || seq <= last)
            return false;

        gap = seq - last - 1;
        return gap > 0;
    }

    /// <summary>
    /// Forgets the sequence seen so far, so the next tick counts as a first observation.
    /// </summary>
    /// <remarks>
    /// Called on reconnect. The engine keeps scanning while a client is away, so the first tick of
    /// the new session is legitimately far ahead of the last of the old one — reporting that as
    /// dropped ticks would fire the event on every single reconnect, which is exactly the noise
    /// that teaches a plugin author to ignore it.
    /// </remarks>
    public void Reset() => _last = null;
}
