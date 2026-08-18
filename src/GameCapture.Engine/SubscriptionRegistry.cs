using System.Collections.Concurrent;

namespace GameCapture.Engine;

/// <summary>
/// The set of connected plugins. The scan loop reads a snapshot of it once per frame; Track calls
/// add and remove entries from arbitrary request threads. It also owns the mirror of that set in
/// <see cref="EngineStatus"/>, so GetStatus can never disagree with who is actually subscribed.
/// </summary>
internal sealed class SubscriptionRegistry
{
    /// <summary>Replay start gate poll interval — a corpus run costs seconds, not milliseconds.</summary>
    private static readonly TimeSpan SubscriberPollInterval = TimeSpan.FromMilliseconds(100);

    // The value is unused: ConcurrentDictionary is the only lock-free set in the BCL.
    private readonly ConcurrentDictionary<ClientConnection, byte> _clients = new();
    private readonly EngineStatus _status;

    // Once the loop is done there will never be another tick, so a client that registers during
    // the shutdown race must be completed too — otherwise its Track stream hangs until the
    // connection times out.
    private volatile bool _completed;

    public SubscriptionRegistry(EngineStatus status) => _status = status;

    public ClientConnection Register(bool replayMode)
    {
        var client = new ClientConnection(replayMode, c => _status.AddClient(c.Id, c.Name));
        _clients[client] = 0;
        _status.AddClient(client.Id, client.Name);

        if (_completed)
            client.Out.Writer.TryComplete();

        return client;
    }

    public void Unregister(ClientConnection c)
    {
        _clients.TryRemove(c, out _);
        _status.RemoveClient(c.Id);
        c.Out.Writer.TryComplete();
    }

    /// <summary>Point-in-time copy; the scan loop iterates it without holding anything.</summary>
    public IReadOnlyList<ClientConnection> Snapshot() => _clients.Keys.ToArray();

    /// <summary>
    /// Replay gate: completes when at least one client has sent a RoiSetUpdate. Without it a
    /// corpus would be consumed into the void while the plugin is still connecting, and the run
    /// would silently produce nothing.
    /// </summary>
    public async Task WaitForAnySubscribedAsync(CancellationToken ct)
    {
        while (!_clients.Keys.Any(c => c.HasSubscribed))
            await Task.Delay(SubscriberPollInterval, ct);
    }

    /// <summary>
    /// No more ticks will ever be produced (replay end or engine shutdown): complete every
    /// client's channel so the Track streams finish and plugins can run their finalisers.
    /// </summary>
    public void CompleteAll()
    {
        _completed = true;
        foreach (var client in Snapshot())
            client.Out.Writer.TryComplete();
    }
}
