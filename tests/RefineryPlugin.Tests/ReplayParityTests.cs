using System.Text;
using RefineryPlugin.Orders;
using TrackerSdk;
using TrackerSdk.Testing;
using Xunit;
using Xunit.Abstractions;

namespace RefineryPlugin.Tests;

/// <summary>
/// A spawned engine is a process-wide resource (a named pipe, a Windows OCR engine instance), so
/// running two of these at once would have them competing for both while claiming to measure a
/// deterministic replay.
/// </summary>
[CollectionDefinition("ReplayParity", DisableParallelization = true)]
public class ReplayParityCollection;

/// <summary>
/// The acceptance gate for the engine/plugin split: the engine replaying the monolith's own PNG
/// corpora through RefineryPlugin must land the same ledger the monolith's integration tests
/// asserted on (RefineryTrackerReplayTests). Every assertion below is a restatement of one of
/// theirs — same corpus, same expectation — so a divergence anywhere in the split (ROI geometry,
/// the wire mapping, the scan loop's tick construction, the ported logic) fails here.
/// </summary>
/// <remarks>
/// Runs through <see cref="ReplayHarness"/> — a real, separately spawned <c>CaptureEngine.exe</c> —
/// rather than hosting the engine in-proc: this suite has to survive the repo split (TASK-13), where
/// this project can no longer take a <c>ProjectReference</c> on the engine, only on the SDK and its
/// testing companion. Real Windows OCR under the hood, so this is tagged Integration.
/// </remarks>
[Collection("ReplayParity")]
[Trait("Category", "Integration")]
public class ReplayParityTests(ITestOutputHelper output)
{
    /// <summary>The monolith's corpora, linked into this assembly's output by the csproj.</summary>
    private const string FixturesRoot = "Fixtures/Replay";

    /// <summary>
    /// A hang bound, not a performance budget: real OCR over these corpora measures 1-2s each, so
    /// anything near this means something is stuck rather than slow. Deliberately far above the
    /// measurement to stay quiet on a loaded CI box.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    private static string AbsoluteFixture(string relative) => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, relative));

    [Fact]
    public async Task RefineryConfirm_corpus_produces_baseline_ledger()
    {
        var ledger = await RunCorpusAsync("refinery-confirm");

        // Baseline: RefineryTrackerReplayTests.FullConfirmSequence_ProducesOneCollectedOrder.
        Verify(ledger, () =>
        {
            var order = Assert.Single(ledger.All);
            Assert.Equal(OrderState.Collected, order.State);
            Assert.Equal(Completeness.Complete, order.Completeness);
        });
    }

    [Fact]
    public async Task RefineryIceRename_corpus_produces_baseline_ledger()
    {
        var ledger = await RunCorpusAsync("refinery-ice-rename");

        // Baseline: RefineryTrackerReplayTests.RawToRefinedRename_MergesIntoOneOrder. The refinery
        // renames the raw input to its refined product between panels (SETUP "ICE (RAW)" ->
        // PROCESSING/COMPLETED "PRESSURIZED ICE"); quality is stable across the rename, so the two
        // panels must resolve to ONE order with ONE material rather than splitting into a yield-less
        // SETUP order plus an orphaned COMPLETED one. This corpus was captured through COMPLETED
        // only, so it reaches Ready/Complete — the Collected transition is the other test's job.
        Verify(ledger, () =>
        {
            var order = Assert.Single(ledger.All);
            Assert.True(order.State >= OrderState.Ready, $"expected Ready or later, got {order.State}");
            Assert.Equal(Completeness.Complete, order.Completeness);
            var material = Assert.Single(order.Materials);
            Assert.Equal(714, material.Quality);
            Assert.True(material.YieldCscu > 0, "refined yield must merge onto the material");
            Assert.NotNull(order.TotalYieldCscu);
        });
    }

    /// <summary>
    /// Replays one corpus through the real engine (spawned) → pipe → SDK → plugin path and hands
    /// back the ledger it produced. The temp ledger is deleted before returning: what the assertions
    /// read is the in-memory state, which is authoritative either way, and no test may touch a real
    /// ledger.
    /// </summary>
    /// <remarks>
    /// Drives the plugin through the public <see cref="TrackerPluginHost.RunAsync"/> surface — the
    /// same entry point Program uses — via <see cref="ReplayHarness"/>, rather than reaching into
    /// RefineryLogic over an InternalsVisibleTo grant (killed in TASK-11). The ledger the host's
    /// plugin opens is captured through <see cref="RefineryPlugin"/>'s test seam. An explicit
    /// <c>--ledger</c> override (via the plugin's ledger-override closure) points the replay at a
    /// file this method can delete afterwards.
    /// </remarks>
    private async Task<OrderLedger> RunCorpusAsync(string corpus)
    {
        var corpusDir = AbsoluteFixture(Path.Combine(FixturesRoot, corpus));
        Assert.True(Directory.Exists(corpusDir), $"corpus not copied to the test output: {corpusDir}");

        var frameCount = Directory.EnumerateFiles(corpusDir, "*.png").Count();
        // Non-empty first, as ScanLoopTests does: Directory.Exists is satisfied by an empty
        // directory, so a corpus that failed to copy would otherwise reach the baselines as the
        // same empty ledger a genuine parity break produces.
        Assert.NotEqual(0, frameCount);

        var ledgerDir = Path.Combine(Path.GetTempPath(), $"sc-parity-{Guid.NewGuid():N}");
        var ledgerPath = Path.Combine(ledgerDir, "orders.jsonl");

        OrderLedger? captured = null;

        try
        {
            var plugin = new RefineryPlugin(
                new RefineryConfig(),
                ledgerOverride: () => ledgerPath,
                onLedgerOpened: l => captured = l);

            var result = await ReplayHarness.RunAsync(new ReplayOptions
            {
                EnginePath = EngineLocator.Resolve(),
                CorpusDir = corpusDir,
                Plugin = plugin,
                Timeout = TestTimeout,
            });

            output.WriteLine($"{corpus}: {frameCount} frame(s) replayed, exit {result.ExitCode}, " +
                $"reason {result.Reason}, {captured?.All.Count ?? 0} order(s)");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(StreamEndReason.ReplayCompleted, result.Reason);

            // The host opens the ledger on its first connect; a corpus that never let it connect is
            // a failure of the harness, not a parity result.
            Assert.True(captured is not null, $"{corpus}: the plugin never opened a ledger (never connected)");
            Assert.NotEmpty(captured.All);

            return captured;
        }
        finally
        {
            if (Directory.Exists(ledgerDir))
                Directory.Delete(ledgerDir, recursive: true);
        }
    }

    /// <summary>
    /// Runs the baseline assertions, dumping the ledger it actually produced before letting a
    /// failure through. A parity failure means some layer of the split disagrees with the monolith,
    /// and "Assert.Single() Failure" alone says nothing about which — the records do.
    /// </summary>
    private void Verify(OrderLedger ledger, Action assertions)
    {
        try
        {
            assertions();
        }
        catch (Exception)
        {
            output.WriteLine($"--- ledger: {ledger.All.Count} order(s) ---");
            foreach (var order in ledger.All)
                output.WriteLine(Describe(order));
            throw;
        }
    }

    private static string Describe(WorkOrder order)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{order.Id} [{order.State}, {order.Completeness}] " +
            $"station={order.Station} process={order.Process} cost={order.Cost} eta={order.Eta}");
        sb.AppendLine($"  key={order.Key}");
        sb.AppendLine($"  sources={string.Join(",", order.Sources)} rowsSeen={order.RowsSeen} " +
            $"total={order.TotalYieldCscu?.ToString() ?? "null"}");
        foreach (var m in order.Materials)
            sb.AppendLine($"  material name={m.Name} quality={m.Quality} qty={m.QtyCscu} " +
                $"yield={m.YieldCscu} refine={m.RefineOn}");
        return sb.ToString().TrimEnd();
    }
}
