using System.Text;
using RefineryPlugin;
using RefineryPlugin.Orders;
using TrackerSdk;
using Xunit;
using Xunit.Abstractions;

namespace CaptureEngine.Tests;

/// <summary>
/// Named pipes and OCR engine instances are process-wide resources, and these tests stand up a
/// full engine each; running two at once would have them competing for both while claiming to
/// measure a deterministic replay.
/// </summary>
[CollectionDefinition("ReplayParity", DisableParallelization = true)]
public class ReplayParityCollection;

/// <summary>
/// The acceptance gate for the engine/plugin split: the engine replaying the monolith's own PNG
/// corpora through RefineryPlugin must land the same ledger the monolith's integration tests
/// assert on today (RefineryTrackerReplayTests). Every assertion below is a restatement of one of
/// theirs — same corpus, same expectation — so a divergence anywhere in the split (ROI geometry,
/// the wire mapping, the scan loop's tick construction, the ported logic) fails here.
/// </summary>
/// <remarks>
/// Everything runs in ONE process: engine host on a private pipe with a ReplayFrameSource, the SDK
/// over that pipe, and RefineryLogic driven by RefineryRunner — the same runner Program uses, so
/// the tests cover the feeding code too, not just the parser. Real Windows OCR, so this is tagged
/// Integration exactly as the monolith's replay tests are.
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

    [Fact]
    public async Task RefineryConfirm_corpus_produces_baseline_ledger()
    {
        var (ledger, _) = await RunCorpusAsync("refinery-confirm");

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
        var (ledger, _) = await RunCorpusAsync("refinery-ice-rename");

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
    /// Replays one corpus through the real engine → pipe → SDK → plugin path and hands back the
    /// ledger it produced. The temp ledger is deleted before returning: what the assertions read is
    /// the in-memory state, which is authoritative either way, and no test may touch a real ledger.
    /// </summary>
    private async Task<(OrderLedger Ledger, List<TrackerRecord> Records)> RunCorpusAsync(string corpus)
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        // Two sinks, because there are now two ConsoleSink classes: the engine's own copy and the
        // SDK's. That is the deliberate fork (see CaptureEngine/Core/ConsoleSink.cs) showing through
        // in the one project that hosts both sides in a single process — after the repo split, these
        // would be two processes and the question could not arise.
        using var engineSink = new ConsoleSink();
        using var pluginSink = new TrackerSdk.ConsoleSink();

        var corpusDir = Path.Combine(FixturesRoot, corpus);
        Assert.True(Directory.Exists(corpusDir), $"corpus not copied to the test output: {corpusDir}");

        var ledgerDir = Path.Combine(Path.GetTempPath(), $"sc-parity-{Guid.NewGuid():N}");
        var ledgerPath = Path.Combine(ledgerDir, "orders.jsonl");

        var records = new List<TrackerRecord>();
        var ledger = new OrderLedger(ledgerPath);

        try
        {
            var pipeName = $"sc-parity-{Guid.NewGuid():N}";
            var source = new ReplayFrameSource(corpusDir);
            var frameCount = source.FrameCount;

            await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
                source, engineSink, verbose: false);
            await engine.StartAsync(cts.Token);

            // The loop's own stop signal, distinct from the client-side budget: the finally below
            // has to be able to end the loop on a path where cts never fired.
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

            // Started before anyone subscribes: in replay the loop holds the corpus until a client
            // is ready, so no frame is burned before the plugin is listening.
            var scan = engine.RunScanAsync(scanCts.Token);

            try
            {
                ledger.Load();

                using var client = new CaptureClient(pipeName);

                // The plugin's own runner, not a hand-rolled loop: the subscribe/consume path is
                // part of what the split changed and therefore part of what parity has to cover. It
                // returns when the engine completes the stream at corpus exhaustion.
                await RefineryRunner.RunAsync(client, pipeName,
                    _ => new RefineryLogic(records.Add, pluginSink, verbose: false, dumpFrame: null, ledger),
                    pluginSink, cts.Token);

                // Checked before the assertions so a hang is reported as one. The runner treats a
                // cancelled stream as a stream that ended, so a fired budget otherwise reaches the
                // baselines as an empty ledger — a timeout wearing a parity failure's clothes.
                Assert.False(cts.IsCancellationRequested,
                    $"{corpus}: timed out after {TestTimeout}");

                await scan;
                await engine.StopAsync();
            }
            finally
            {
                // Nothing else observes the loop once the path above throws, and leaving the try
                // block disposes the engine — ScanLoop.Dispose plus the frame source — underneath a
                // loop still mid-frame. That surfaces as an ObjectDisposedException standing next to
                // the real failure, so stop the loop here and let the primary exception through.
                scanCts.Cancel();
                try
                {
                    await scan;
                }
                catch (Exception ex)
                {
                    output.WriteLine($"{corpus}: scan loop ended with {ex.GetType().Name}: {ex.Message}");
                }
            }

            output.WriteLine($"{corpus}: {frameCount} frame(s) replayed, " +
                $"{ledger.All.Count} order(s), {records.Count} capture(s)");

            // Non-empty first, as ScanLoopTests does: Directory.Exists is satisfied by an empty
            // directory, so a corpus that failed to copy — or a wholesale ROI failure — would reach
            // the baselines as the same empty ledger a genuine parity break produces.
            Assert.NotEqual(0, frameCount);
            Assert.NotEmpty(records);

            return (ledger, records);
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
