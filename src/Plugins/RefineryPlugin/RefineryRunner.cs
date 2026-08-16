using CaptureContracts.Proto;
using Common;
using Grpc.Core;
using TrackerSdk;

namespace RefineryPlugin;

/// <summary>
/// The plugin's connect / subscribe / consume-ticks loop, with its reconnect and shutdown rules.
/// </summary>
/// <remarks>
/// Extracted from <c>Program</c> so the replay-parity tests drive the same path the real plugin
/// runs (ENGINE-SPLIT TASK-7). A test that stood up its own loop would prove the parser reproduces
/// the monolith's outcomes while saying nothing about the code that actually feeds it — and the
/// feeding is what the split changed.
///
/// The logic is built from a factory rather than handed in ready-made because the ledger it writes
/// to cannot be chosen until the engine has answered: only the engine knows whether it is replaying
/// a corpus, and a replay must never append to the user's real orders.jsonl. The factory runs once,
/// on the first successful connect; a later reconnect keeps the same logic, so its panel state and
/// its ledger survive an engine restart.
/// </remarks>
internal static class RefineryRunner
{
    /// <summary>
    /// How long to wait for an engine that is not up yet. WaitForEngineAsync needs a finite budget:
    /// Timeout.InfiniteTimeSpan is negative and would go straight to its timeout branch, and
    /// TimeSpan.MaxValue overflows the RPC deadline. A day is "forever" for a plugin left running —
    /// the loop retries anyway, and cancellation, not this, is what ends the wait.
    /// </summary>
    private static readonly TimeSpan EngineWait = TimeSpan.FromDays(1);

    /// <summary>Breathing room between a lost session and the next dial; see the RpcException branch.</summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Runs until the tick stream ends normally (replay finished, or the engine shut down) or
    /// <paramref name="ct"/> fires. Reconnects on its own when the engine goes away mid-session.
    /// </summary>
    public static async Task RunAsync(CaptureClient client, string pipeName,
        Func<StatusResponse, RefineryLogic> logicFactory, ConsoleSink sink, CancellationToken ct)
    {
        RefineryLogic? logic = null;

        // Announced once per disconnected stretch rather than per retry: a plugin started before
        // the engine would otherwise scroll the same line every few seconds.
        var announcedWait = false;

        while (true)
        {
            if (!announcedWait)
            {
                sink.WriteLine($"waiting for engine on pipe '{pipeName}'...");
                announcedWait = true;
            }

            try
            {
                var status = await client.WaitForEngineAsync(EngineWait, ct);
                announcedWait = false;

                logic ??= logicFactory(status);

                await using var session = await client.TrackAsync(RefineryLogic.Name, Rois.All, ct);

                sink.WriteLine($"Engine:    {status.EngineVersion}{(status.ReplayMode ? " (replay)" : "")}");
                sink.WriteLine($"Frame:     {(status.FrameWidth == 0
                    ? "no frame scanned yet"
                    : $"{status.FrameWidth}x{status.FrameHeight}")}");
                sink.WriteLine($"ROIs:      {string.Join(", ", Rois.All.Select(r => r.Id))}");
                sink.WriteLine();
                sink.WriteLine("Running. Ctrl+C to quit.");
                sink.WriteLine();

                await foreach (var tick in session.Ticks(ct))
                {
                    // As TrackerHost did per tracker: one bad tick must not end the run. A genuine
                    // transport failure is not swallowed — the next read from the stream raises it
                    // again and the reconnect below handles it.
                    try
                    {
                        await logic.OnTickAsync(tick, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        sink.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {RefineryLogic.Name}: tick failed: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // our own Ctrl+C: the channel maps a cancelled call to this, not RpcException
            }
            catch (RpcException) when (ct.IsCancellationRequested)
            {
                // Ctrl+C again: the channel's OCE mapping covers the call, but a write already in
                // flight on the request stream can still surface as CANCELLED. Not an engine failure.
                return;
            }
            catch (TimeoutException)
            {
                continue; // engine still not serving; the line above already says we are waiting
            }
            catch (Exception ex) when (ex is RpcException or OperationCanceledException)
            {
                // The engine went away mid-session. Reconnecting means a fresh subscription, and the
                // logic's panel state is deliberately kept: the ledger merges idempotently, so a
                // panel still on screen after the reconnect re-observes into the same record.
                //
                // OperationCanceledException lands here too, and only because ct did NOT cause it:
                // the channel sets ThrowOperationCanceledOnCancellation, which maps a call the
                // ENGINE cancelled (a restart aborting the in-flight Track with CANCELLED) to the
                // same exception type as our own Ctrl+C. Caught unfiltered, that would exit the
                // plugin 0 on exactly the failure this loop exists to survive.
                sink.WriteLine("engine connection lost — reconnecting");

                // Paced: WaitForEngineAsync returns immediately whenever GetStatus answers, so an
                // engine that is up but cannot serve a Track stream (mid-shutdown, for one) would
                // otherwise spin this loop with no delay at all.
                try { await Task.Delay(ReconnectDelay, ct); }
                catch (OperationCanceledException) { return; }

                continue;
            }

            return; // stream ended normally (engine replay finished or shutdown)
        }
    }
}
