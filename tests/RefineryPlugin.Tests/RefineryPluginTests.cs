using RefineryPlugin.Orders;
using TrackerSdk;
using TrackerSdk.Testing;
using Xunit;
using static RefineryPlugin.Tests.RefineryTicks;

namespace RefineryPlugin.Tests;

/// <summary>
/// The lifecycle shell itself — the part TASK-11 added around <see cref="RefineryLogic"/>. Everything
/// here happens on the FIRST tick (the earliest point an <see cref="IPluginServices"/> exists to open
/// the ledger against, since <c>SessionEvent.Connected</c> carries no output handle): the ledger is
/// resolved and opened, its durability diagnostics are wired to the run's log, and the connect-time
/// path/note announcement is printed. These pin the two review fixes — a dropped warn callback and a
/// lost announcement — that the integration-only ReplayParityTests could not have caught.
/// </summary>
public sealed class RefineryPluginTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("refinery-plugin-shell-tests");
    private readonly FakePluginServices _services = new();

    public void Dispose()
    {
        _dir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private string LedgerPath => Path.Combine(_dir.FullName, "orders.jsonl");

    private RefineryPlugin NewPlugin(Action<OrderLedger>? onOpened = null)
        => new(new RefineryConfig(), ledgerOverride: () => LedgerPath, onLedgerOpened: onOpened);

    private Task Tick(RefineryPlugin plugin, string panel = "")
        => plugin.OnTickAsync(TickContext.ForTesting(RefineryTicks.Tick(panel), _services), default);

    [Fact]
    public async Task FirstTick_OpensTheLedgerOnceAndAnnouncesItsPath()
    {
        var opened = 0;
        var plugin = NewPlugin(_ => opened++);

        await Tick(plugin);
        await Tick(plugin);
        await Tick(plugin);

        // Opened exactly once — a reconnect (or just the next tick) must keep the ledger, not reopen it.
        Assert.Equal(1, opened);

        // The connect-time announcement the monolith printed, restored: the path the run is writing to.
        Assert.Contains(_services.Logs, l => l.Contains("Ledger:") && l.Contains(LedgerPath));
    }

    [Fact]
    public async Task LedgerDiagnostics_AreSurfacedThroughTheRunsLog()
    {
        // A pre-existing ledger with a torn line: OrderLedger.Load skips it and warns — and that warn
        // must reach the user. The bug this pins: the shell opened `new OrderLedger(path)` with no warn
        // delegate, so every durability diagnostic (skips, read failures, the "write failed, keeping in
        // memory" notice) went nowhere. The fix wires warn to services.Log.
        await File.WriteAllTextAsync(LedgerPath, "{ this is not valid json\n");

        await Tick(NewPlugin());

        Assert.Contains(_services.Logs, l => l.Contains("malformed"));
    }

    [Fact]
    public async Task InReplayMode_AnnouncesAThrowawayLedgerNote()
    {
        // No --ledger override this time, so the replay branch of LedgerTargetResolver picks a throwaway
        // file and the announcement must say so — the user has to be able to tell a replay from a run
        // that appends to their real orders.jsonl.
        _services.Engine = _services.Engine with { ReplayMode = true };
        var plugin = new RefineryPlugin(new RefineryConfig()); // no override

        await Tick(plugin);

        Assert.Contains(_services.Logs, l => l.Contains("Ledger:") && l.Contains("replay — throwaway file"));
    }

    [Fact]
    public async Task BeforeAnyTick_SummaryReportsNoLedger()
    {
        var plugin = NewPlugin();

        // No tick delivered → the ledger was never opened, and the summary says so rather than throwing.
        Assert.Contains(plugin.SummaryLines(), l => l.Contains("not opened"));

        await Tick(plugin);

        // After a tick it reports the opened ledger's path instead.
        Assert.Contains(plugin.SummaryLines(), l => l.Contains(LedgerPath));
    }
}
