using System.Text.Json.Nodes;
using TrackingService.Orders;
using Xunit;

namespace TrackingService.Tests;

/// <summary>
/// First temp-dir-backed test class in the project: each test gets a fresh throwaway directory that
/// <see cref="Dispose"/> deletes, so real ledger data is never touched.
/// </summary>
public sealed class OrderLedgerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly List<string> _warnings = new();

    public OrderLedgerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sc-ledger-tests-" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "orders.jsonl");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a leaked temp dir must not fail the suite.
        }
    }

    private OrderLedger NewLedger()
    {
        var ledger = new OrderLedger(_path, _warnings.Add);
        ledger.Load();
        return ledger;
    }

    private static WorkOrder Obs(
        string station,
        IEnumerable<(string Name, int Qty, int Yield)> mats,
        OrderState state = OrderState.Pending,
        Completeness completeness = Completeness.Unknown,
        int? total = null,
        string source = "SETUP",
        DateTime when = default)
    {
        var at = when == default ? DateTime.Now : when;
        var materials = mats.Select(m => new OrderMaterial(m.Name, 0, m.Qty, m.Yield, false)).ToList();
        return new WorkOrder(
            Id: "", Key: "", Station: station, Process: "Diffusion", Cost: "1000 aUEC", Eta: "1h",
            State: state, Completeness: completeness, Materials: materials, TotalYieldCscu: total,
            RowsSeen: materials.Count, FirstSeen: at, LastSeen: at, Sources: [source]);
    }

    private static int LineCount(string path) =>
        File.Exists(path) ? File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l)) : 0;

    [Fact]
    public void Observe_NewOrder_AppendsAndAssignsId()
    {
        var ledger = NewLedger();

        var result = ledger.Observe(Obs("S", [("TITANIUM", 100, 313)]));

        Assert.True(result.Changed);
        Assert.NotEqual("", result.Merged.Id);
        Assert.Single(ledger.All);
        Assert.Equal(1, LineCount(_path));
    }

    [Fact]
    public void Observe_SameObservationTwice_NoSecondAppend()
    {
        var ledger = NewLedger();
        var first = Obs("S", [("TITANIUM", 100, 313)], when: new DateTime(2026, 1, 1, 0, 0, 0));

        var r1 = ledger.Observe(first);
        // Identical content, later timestamp only (LastSeen churn must not append — H2).
        var r2 = ledger.Observe(first with { LastSeen = new DateTime(2026, 1, 1, 0, 0, 5) });

        Assert.True(r1.Changed);
        Assert.False(r2.Changed);
        Assert.Single(ledger.All);
        Assert.Equal(1, LineCount(_path));
    }

    [Fact]
    public void Observe_RowsSeenOnlyDelta_DoesNotAppend()
    {
        var ledger = NewLedger();
        var baseObs = Obs("S", [("TITANIUM", 100, 313)]);

        ledger.Observe(baseObs);
        var r2 = ledger.Observe(baseObs with { RowsSeen = 99 }); // same materials, bigger RowsSeen only

        Assert.False(r2.Changed);
        Assert.Equal(1, LineCount(_path));
    }

    [Fact]
    public void AppendThenRebuild_RoundTrips()
    {
        var ledger = NewLedger();
        ledger.Observe(Obs("Station A", [("TITANIUM", 100, 313)]));
        ledger.Observe(Obs("Station B", [("GOLD", 50, 57)]));

        var reloaded = NewLedger();

        Assert.Equal(2, reloaded.All.Count);
        Assert.Contains(reloaded.All, w => w.Station == "Station A");
        Assert.Contains(reloaded.All, w => w.Station == "Station B");
    }

    [Fact]
    public void Observe_StateAdvancesMonotonically_NeverRegresses()
    {
        var ledger = NewLedger();
        ledger.Observe(Obs("S", [("TITANIUM", 100, 313)], state: OrderState.Ready));

        var merged = ledger.Observe(Obs("S", [("TITANIUM", 100, 313)], state: OrderState.Processing)).Merged;

        Assert.Equal(OrderState.Ready, merged.State); // Processing < Ready → no regression
    }

    [Fact]
    public void Observe_PartialThenComplete_UnionsMaterialsAndPromotesCompleteness()
    {
        var ledger = NewLedger();
        ledger.Observe(Obs("S", [("TITANIUM", 100, 313), ("GOLD", 50, 57)],
            completeness: Completeness.Partial, source: "COMPLETED"));

        var merged = ledger.Observe(Obs("S",
            [("TITANIUM", 100, 313), ("GOLD", 50, 57), ("IRON", 80, 274)],
            completeness: Completeness.Complete, total: 644, source: "COMPLETED")).Merged;

        Assert.Equal(3, merged.Materials.Count);
        Assert.Equal(Completeness.Complete, merged.Completeness);
        Assert.Equal(644, merged.TotalYieldCscu);
    }

    [Fact]
    public void Observe_OccludedUnknown_NeverPromotesToComplete()
    {
        var ledger = NewLedger();
        ledger.Observe(Obs("S", [("TITANIUM", 100, 313)], completeness: Completeness.Partial, source: "COMPLETED"));

        var merged = ledger.Observe(Obs("S", [("TITANIUM", 100, 313)],
            completeness: Completeness.Unknown, source: "COMPLETED")).Merged;

        Assert.Equal(Completeness.Partial, merged.Completeness); // Unknown can neither lower nor raise
    }

    [Fact]
    public void Observe_RepeatAfterCollected_SpawnsNewRecord()
    {
        var ledger = NewLedger();
        ledger.Observe(Obs("S", [("TITANIUM", 100, 313)], state: OrderState.Ready));
        ledger.Observe(Obs("S", [("TITANIUM", 100, 313)], state: OrderState.Collected)); // close it

        // Same mix, same station, but the only match is now closed → a fresh order must spawn (H1).
        ledger.Observe(Obs("S", [("TITANIUM", 100, 313)], state: OrderState.Processing));

        Assert.Equal(2, ledger.All.Count);
        Assert.Single(ledger.All, w => w.State == OrderState.Collected);
        Assert.Single(ledger.All, w => w.State == OrderState.Processing);
    }

    [Fact]
    public void Observe_ExplicitId_TargetsExactRecord_NotFuzzyTieBreak()
    {
        // Two open records that share station + materials can coexist in the ledger's backing file
        // (e.g. two genuinely separate runs of an identical recipe, both still open) even though a
        // *fresh* fuzzy Observe() of that exact mix would normally merge into one — so this seeds them
        // directly into the file, bypassing fuzzy matching, the way a reload would encounter them.
        var seed = NewLedger();
        var older = seed.Observe(Obs("S", [("TITANIUM", 100, 313)], when: new DateTime(2026, 1, 1))).Merged;

        var olderLine = File.ReadAllLines(_path).Single();
        var duplicate = JsonNode.Parse(olderLine)!.AsObject();
        duplicate["id"] = Guid.NewGuid().ToString("N");
        duplicate["firstSeen"] = new DateTime(2026, 6, 1).ToString("o");
        duplicate["lastSeen"] = new DateTime(2026, 6, 1).ToString("o");
        File.AppendAllText(_path, duplicate.ToJsonString() + Environment.NewLine);

        var ledger = NewLedger(); // reload: two distinct open records, identical station + materials
        Assert.Equal(2, ledger.All.Count);
        var newer = ledger.All.Single(w => w.Id != older.Id);
        Assert.NotEqual(older.Id, newer.Id);

        // A fuzzy match (no Id) would tie-break to the earliest (older) FirstSeen — see
        // TryMatch_IdenticalCandidates_EarliestFirstSeenWins. An observation naming the newer record's
        // Id must collect THAT one instead, regardless of tie-break ordering.
        var result = ledger.Observe(newer with { State = OrderState.Collected, LastSeen = DateTime.Now });

        Assert.Equal(newer.Id, result.Merged.Id);
        Assert.Equal(OrderState.Collected, result.Merged.State);
        var untouched = ledger.All.Single(w => w.Id == older.Id);
        Assert.Equal(OrderState.Pending, untouched.State); // the older record must be untouched
    }

    // ---- Durability matrix (F1-F5) ----

    [Fact]
    public void Load_MissingFileAndDir_StartsEmptyAndCreatesDir()
    {
        var ledger = NewLedger(); // _dir does not exist yet

        Assert.Empty(ledger.All);
        Assert.True(Directory.Exists(_dir));
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Load_EmptyFile_StartsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, "");

        var ledger = NewLedger();

        Assert.Empty(ledger.All);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Load_TornLastLine_LoadsRestAndWarns()
    {
        var seed = NewLedger();
        seed.Observe(Obs("Station A", [("TITANIUM", 100, 313)]));
        seed.Observe(Obs("Station B", [("GOLD", 50, 57)]));
        // Simulate an interrupted append: a truncated JSON fragment as the final line.
        File.AppendAllText(_path, "{\"id\":\"torn\",\"station\":\"C\",\"mate");

        var reloaded = NewLedger();

        Assert.Equal(2, reloaded.All.Count);
        Assert.Contains(_warnings, w => w.Contains("skipped 1"));
    }

    [Fact]
    public void Load_GarbageLineMidFile_SkippedRestKept()
    {
        var seed = NewLedger();
        seed.Observe(Obs("Station A", [("TITANIUM", 100, 313)]));
        seed.Observe(Obs("Station B", [("GOLD", 50, 57)]));

        // Insert a garbage line between the two valid ones.
        var lines = File.ReadAllLines(_path).ToList();
        lines.Insert(1, "not json at all");
        File.WriteAllLines(_path, lines);

        var reloaded = NewLedger();

        Assert.Equal(2, reloaded.All.Count);
        Assert.Contains(_warnings, w => w.Contains("skipped 1"));
    }

    [Fact]
    public void Observe_FileDeletedMidRun_SnapshotRewriteSelfHeals()
    {
        var ledger = NewLedger();
        ledger.Observe(Obs("Station A", [("TITANIUM", 100, 313)]));
        ledger.Observe(Obs("Station B", [("GOLD", 50, 57)]));

        File.Delete(_path); // history gone, memory still holds both

        // A new change with the file missing must snapshot ALL in-memory records, not one delta line.
        ledger.Observe(Obs("Station C", [("IRON", 80, 274)]));

        Assert.Equal(3, LineCount(_path));
        Assert.Equal(3, NewLedger().All.Count); // reload proves the earlier two were re-written
    }

    [Fact]
    public void Observe_AppendToLockedFile_DoesNotThrow_MemoryStaysAuthoritative()
    {
        var ledger = NewLedger();
        ledger.Observe(Obs("Station A", [("TITANIUM", 100, 313)])); // creates the file

        // Hold an exclusive lock so the next append fails (F3/F5).
        using (var _ = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Record.Exception(() => ledger.Observe(Obs("Station B", [("GOLD", 50, 57)])));
            Assert.Null(ex); // ledger I/O must never throw into the tracker loop
        }

        Assert.Equal(2, ledger.All.Count);                       // memory updated regardless
        Assert.Contains(_warnings, w => w.Contains("write failed"));
    }
}
