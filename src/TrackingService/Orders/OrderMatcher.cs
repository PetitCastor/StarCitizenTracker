using TrackingService.Trackers;

namespace TrackingService.Orders;

/// <summary>
/// Pure identity + matching for work orders. A work order is identified by its station plus its
/// set of material names — never by yields, because a SETUP yield and the COMPLETED yield for the
/// same material can differ by a cSCU or two, and keying on that would split one order into two.
/// Yield closeness is only ever a tie-break score between same-station, same-materials candidates.
/// </summary>
/// <remarks>
/// H1 (silent-repeat-loss fix): terminal <see cref="OrderState.Collected"/> records are <c>closed</c>
/// and excluded from <see cref="TryMatch"/> candidates by the caller, so re-running an identical
/// material mix at the same station spawns a fresh order instead of merging into (and being swallowed
/// by) the already-collected one. Time-based closure of stale <see cref="OrderState.Ready"/> records
/// was considered and deliberately DEFERRED — only <c>Collected</c> closes for now.
/// </remarks>
public static class OrderMatcher
{
    /// <summary>Per-material yield delta (cSCU) within which two yields count as "the same" for tie-break scoring.</summary>
    public const int YieldMatchToleranceCscu = 50;

    /// <summary>Identity key: <c>STATION | sorted(normalized material names)</c>.</summary>
    public static string Key(string station, IEnumerable<string> materialNames)
    {
        var names = materialNames
            .Select(RefineryParser.NormalizeName)
            .Where(n => n.Length > 0)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal);
        return $"{RefineryParser.NormalizeName(station)} | {string.Join(",", names)}";
    }

    /// <summary>A terminal record is closed and must not be a match candidate (H1).</summary>
    public static bool IsClosed(WorkOrder w) => w.State == OrderState.Collected;

    /// <summary>
    /// Finds the best open record that <paramref name="candidate"/> should merge into. A match
    /// requires the same station and one name-set containing the other (so a partial observation —
    /// a subset of names — still matches its complete record). Candidates are ranked by name overlap
    /// (Jaccard), then by how closely their per-material yields match the candidate, then by earliest
    /// <see cref="WorkOrder.FirstSeen"/>. Callers pass OPEN records only.
    /// </summary>
    public static bool TryMatch(
        WorkOrder candidate,
        IReadOnlyCollection<WorkOrder> existing,
        out WorkOrder? best,
        out double score)
    {
        best = null;
        score = 0;

        var candStation = RefineryParser.NormalizeName(candidate.Station);
        var candNames = NameSet(candidate);
        if (candNames.Count == 0)
            return false;

        foreach (var e in existing)
        {
            if (RefineryParser.NormalizeName(e.Station) != candStation)
                continue;

            var eNames = NameSet(e);
            if (eNames.Count == 0)
                continue;

            // Containment gate: one set must contain the other (subset or superset).
            if (!candNames.IsSubsetOf(eNames) && !eNames.IsSubsetOf(candNames))
                continue;

            var intersection = candNames.Count(eNames.Contains);
            var union = candNames.Count + eNames.Count - intersection;
            var jaccard = (double)intersection / union;

            // Tie-break: fraction of shared materials whose yields agree within tolerance. Weighted
            // tiny so it only separates otherwise-equal (same station, same names) candidates.
            var candYields = candidate.Materials.ToDictionary(m => m.Name, m => m.YieldCscu);
            var closeShared = e.Materials.Count(m =>
                candYields.TryGetValue(m.Name, out var cy) &&
                Math.Abs(cy - m.YieldCscu) <= YieldMatchToleranceCscu);
            var yieldCloseness = intersection > 0 ? (double)closeShared / intersection : 0;

            var candidateScore = jaccard + yieldCloseness * 1e-3;

            if (best is null ||
                candidateScore > score ||
                (candidateScore == score && e.FirstSeen < best.FirstSeen))
            {
                best = e;
                score = candidateScore;
            }
        }

        return best is not null;
    }

    private static HashSet<string> NameSet(WorkOrder w)
        => w.Materials.Select(m => RefineryParser.NormalizeName(m.Name))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
}
