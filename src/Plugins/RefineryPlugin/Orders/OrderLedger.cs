using System.Text.Json;
using System.Text.Json.Serialization;

namespace RefineryPlugin.Orders;

/// <summary>
/// Append-only JSONL store of refinery work orders. Any of the SETUP / PROCESSING / COMPLETED
/// screens feeds observations in via <see cref="Observe"/>; the ledger merges them idempotently by
/// identity, advances lifecycle state monotonically, and appends one line per meaningful change.
/// Rebuild-on-load with last-write-wins per record id makes it crash-safe with no rewrite path.
/// </summary>
/// <remarks>
/// Durability guarantees (a crashed or interfered-with file must never take down the tracker loop):
/// a torn or garbled line is skipped on load; a missing file/dir is created and started empty; a
/// write that fails (disk full, AV lock, permissions) is warned once and the in-memory state stays
/// authoritative, retried on the next real change; a file deleted mid-run is self-healed by writing
/// a full snapshot of memory instead of a lone delta line.
/// </remarks>
public sealed class OrderLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false, // JSONL: exactly one record per physical line
        Converters = { new JsonStringEnumConverter() }, // human-readable, reorder-stable enums
    };

    private readonly string _path;
    private readonly Action<string>? _warn;
    private readonly Dictionary<string, WorkOrder> _records = new(StringComparer.Ordinal); // keyed by WorkOrder.Id
    private bool _ioWarned;

    public OrderLedger(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
    }

    /// <summary>Every record currently held in memory (authoritative, regardless of file state).</summary>
    public IReadOnlyCollection<WorkOrder> All => _records.Values;

    /// <summary>
    /// Rebuilds in-memory state from the file. Missing file/dir ⇒ start empty (F2). Malformed lines
    /// — including the torn last line an interrupted append leaves — are counted and skipped, never
    /// thrown (F1). A file that can't be read at all warns and starts empty.
    /// </summary>
    public void Load()
    {
        _records.Clear();

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(_path))
            return;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"orders.jsonl: could not read ({e.Message}); starting empty");
            return;
        }

        var skipped = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            WorkOrder? record;
            try
            {
                record = JsonSerializer.Deserialize<WorkOrder>(line, JsonOptions);
            }
            catch (JsonException)
            {
                skipped++;
                continue;
            }

            if (record is null || string.IsNullOrEmpty(record.Id))
            {
                skipped++;
                continue;
            }

            _records[record.Id] = record; // last-write-wins
        }

        if (skipped > 0)
            _warn?.Invoke($"orders.jsonl: skipped {skipped} malformed line(s)");
    }

    /// <summary>
    /// Merges one observation into the ledger. An observation that names an existing record's
    /// <see cref="WorkOrder.Id"/> targets that record directly, bypassing fuzzy matching entirely —
    /// this is the fast path callers use when they already hold the exact record (e.g.
    /// <c>RefineryLogic.MarkCollected</c>), so a tie-break between two open records with identical
    /// station+materials can never steal the merge. Otherwise, matches against OPEN records only
    /// (Collected records are closed, so a repeat of an already-collected mix spawns a fresh order —
    /// H1); no match ⇒ a new record with a freshly assigned id. Appends a line only when something
    /// meaningful changed (H2): timestamp/row-count churn rides along in memory but never writes on
    /// its own.
    /// </summary>
    public ObserveResult Observe(WorkOrder observation)
    {
        WorkOrder merged;
        WorkOrder? previous;
        if (!string.IsNullOrEmpty(observation.Id) && _records.TryGetValue(observation.Id, out var exact))
        {
            previous = exact;
            merged = Merge(exact, observation);
        }
        else
        {
            var openRecords = _records.Values.Where(w => !OrderMatcher.IsClosed(w)).ToList();
            if (OrderMatcher.TryMatch(observation, openRecords, out var best, out _) && best is not null)
            {
                previous = best;
                merged = Merge(best, observation);
            }
            else
            {
                previous = null;
                merged = NewRecord(observation);
            }
        }

        var changed = previous is null || IsMeaningfulChange(previous, merged);

        _records[merged.Id] = merged; // memory always current, even when nothing is appended

        if (changed)
            Append(merged);

        return new ObserveResult(merged, changed);
    }

    private static WorkOrder NewRecord(WorkOrder obs)
    {
        var firstSeen = obs.FirstSeen == default ? obs.LastSeen : obs.FirstSeen;
        return obs with
        {
            Id = Guid.NewGuid().ToString("N"),
            Key = OrderMatcher.Key(obs.Station, obs.Materials),
            RowsSeen = Math.Max(obs.RowsSeen, obs.Materials.Count),
            FirstSeen = firstSeen,
            Sources = Distinct(obs.Sources),
        };
    }

    private static WorkOrder Merge(WorkOrder existing, WorkOrder obs)
    {
        var materials = MergeMaterials(existing.Materials, obs.Materials);
        return existing with
        {
            // Id and Key are the record's stable identity — never recomputed on merge.
            Station = PreferNew(existing.Station, obs.Station),
            Process = PreferNew(existing.Process, obs.Process),
            Cost = PreferNew(existing.Cost, obs.Cost),
            Eta = PreferNew(existing.Eta, obs.Eta),
            State = (OrderState)Math.Max(existing.State.Rank(), obs.State.Rank()), // monotonic
            Completeness = MoreTrusted(existing.Completeness, obs.Completeness),
            Materials = materials,
            TotalYieldCscu = obs.TotalYieldCscu ?? existing.TotalYieldCscu,
            RowsSeen = Math.Max(existing.RowsSeen, Math.Max(obs.RowsSeen, obs.Materials.Count)),
            FirstSeen = Earlier(existing.FirstSeen, obs.FirstSeen == default ? obs.LastSeen : obs.FirstSeen),
            LastSeen = Later(existing.LastSeen, obs.LastSeen),
            Sources = Distinct(existing.Sources.Concat(obs.Sources)),
        };
    }

    /// <summary>
    /// Union by material key (base name + quality), original insertion order preserved and new
    /// materials appended. Shared materials merge field-wise rather than wholesale, because different
    /// panels are authoritative for different fields: SETUP carries QTY and the refine choice, while
    /// PROCESSING/COMPLETED carry the real YIELD. Preferring the non-zero value for each lets a later
    /// clean COMPLETED read fill in the yield without erasing the SETUP-only fields.
    /// </summary>
    private static IReadOnlyList<OrderMaterial> MergeMaterials(
        IReadOnlyList<OrderMaterial> existing, IReadOnlyList<OrderMaterial> incoming)
    {
        var ordered = new List<OrderMaterial>(existing);

        foreach (var m in incoming)
        {
            // Pair by SameMaterial (quality + name-token containment) rather than an exact key, so the
            // raw→refined rename ("ICE (RAW)" → "PRESSURIZED ICE") merges field-wise onto one row
            // instead of appending a duplicate.
            var i = ordered.FindIndex(e => OrderMatcher.SameMaterial(e, m));
            if (i >= 0)
                ordered[i] = MergeMaterial(ordered[i], m);
            else
                ordered.Add(m);
        }

        return ordered;
    }

    private static OrderMaterial MergeMaterial(OrderMaterial existing, OrderMaterial incoming) => existing with
    {
        QtyCscu = incoming.QtyCscu != 0 ? incoming.QtyCscu : existing.QtyCscu,
        YieldCscu = incoming.YieldCscu != 0 ? incoming.YieldCscu : existing.YieldCscu,
        RefineOn = existing.RefineOn || incoming.RefineOn, // appearing on a yield panel proves it was refined
    };

    /// <summary>H2 change detection: material set/values, state, completeness, total and header
    /// fields count; LastSeen, RowsSeen, FirstSeen and Sources deliberately do not (they must never
    /// trigger an append on their own).</summary>
    private static bool IsMeaningfulChange(WorkOrder prev, WorkOrder merged)
        => !MaterialsEqual(prev.Materials, merged.Materials)
            || prev.State != merged.State
            || prev.Completeness != merged.Completeness
            || prev.TotalYieldCscu != merged.TotalYieldCscu
            || prev.Station != merged.Station
            || prev.Process != merged.Process
            || prev.Cost != merged.Cost
            || prev.Eta != merged.Eta;

    private static bool MaterialsEqual(IReadOnlyList<OrderMaterial> a, IReadOnlyList<OrderMaterial> b)
    {
        if (a.Count != b.Count)
            return false;

        var remaining = new List<OrderMaterial>(b);
        foreach (var m in a)
        {
            var i = remaining.FindIndex(other => OrderMatcher.SameMaterial(other, m));
            if (i < 0)
                return false;
            var other = remaining[i];
            if (m.QtyCscu != other.QtyCscu || m.YieldCscu != other.YieldCscu || m.RefineOn != other.RefineOn)
                return false;
            remaining.RemoveAt(i); // consume so duplicate-name rows must each find a distinct partner
        }

        return true;
    }

    private void Append(WorkOrder changed)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(_path))
            {
                // First write, or the file was deleted mid-run (F4): snapshot all of memory so the
                // rebuilt file self-heals to the authoritative state rather than a lone delta line.
                using var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(fs);
                foreach (var record in _records.Values)
                    writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
            }
            else
            {
                // FileShare.Read so a reader (or a second instance) can coexist; a losing writer
                // falls into the catch below (F5 → F3).
                using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(fs);
                writer.WriteLine(JsonSerializer.Serialize(changed, JsonOptions));
            }

            _ioWarned = false; // a good write clears the warning latch
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // F3: ledger I/O must never kill the tracker loop. Warn once; memory stays authoritative
            // and the record retries naturally on its next real change.
            if (!_ioWarned)
            {
                _warn?.Invoke($"orders.jsonl: write failed ({e.Message}); keeping in memory, will retry on next change");
                _ioWarned = true;
            }
        }
    }

    private static string PreferNew(string existing, string incoming)
        => string.IsNullOrWhiteSpace(incoming) || incoming == "?" ? existing : incoming;

    private static Completeness MoreTrusted(Completeness existing, Completeness incoming)
        => Trust(incoming) > Trust(existing) ? incoming : existing;

    // Complete is the most trusted; an occluded (Unknown) observation can never raise a record to it.
    private static int Trust(Completeness c) => c switch
    {
        Completeness.Complete => 2,
        Completeness.Partial => 1,
        _ => 0,
    };

    private static DateTime Earlier(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    private static IReadOnlyList<string> Distinct(IEnumerable<string> sources)
        => sources.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
}
