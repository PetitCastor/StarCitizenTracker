// TRANSITIONAL DUPLICATE of src/TrackingService/Orders/OrderMatcher.cs. The monolith stays untouched
// until ENGINE-SPLIT TASK-8, which deletes its refinery path; until then both copies are live and
// must be edited together.
namespace RefineryPlugin.Orders;

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

    /// <summary>Minimum shared materials for two partial reads of the same order to be matched by overlap.</summary>
    public const int MinOverlapMaterials = 2;

    /// <summary>
    /// Relative quality tolerance for two nonzero quality reads to count as "the same" (±2% of the
    /// larger value). Quality is a 3-digit OCR-derived number, so a single misread digit is typically
    /// a few percent off (714 → 715, or a digit swap like 714 → 774 which this tolerance must still
    /// reject) — 2% is loose enough to absorb that single-digit noise but tight enough that two
    /// genuinely different quality batches at the same station (714 vs 262) never collapse.
    /// </summary>
    public const double QualityToleranceFraction = 0.02;

    /// <summary>Per-material identity token: base name (ore-suffix stripped) plus the quality value.</summary>
    public static string MaterialKey(OrderMaterial m) => $"{RefineryParser.BaseName(m.Name)}#{m.Quality}";

    /// <summary>
    /// Whether two material rows are the same physical batch. Quality must be equal-within-tolerance
    /// (it is stable across every panel, so it's a strong identity signal), and one name's token set
    /// must contain the other's: the refinery renames a raw input to its refined product by adding a
    /// descriptor word — "ICE (RAW)" → "PRESSURIZED ICE" — never by changing the core noun, so
    /// {ICE} ⊆ {PRESSURIZED, ICE} still resolves to one batch.
    /// </summary>
    /// <remarks>
    /// Quality is OCR-derived, not ground truth: <c>0</c> is the deliberate sentinel for "unreadable
    /// this tick" (see <c>RefineryLogic.ObserveYieldPanelAsync</c>/<c>Accumulate</c>, which use
    /// <c>Num(r,0) ?? 0</c>). Treating an exact-equality quality check as gospel means a single
    /// misread digit (or a fully-blank read) would wrongly re-split one physical order into two — the
    /// exact bug class this fix closes. So: quality <c>0</c> on EITHER side is "unknown," a wildcard
    /// that matches any quality; two nonzero qualities match within
    /// <see cref="QualityToleranceFraction"/> (relative to the larger of the two) rather than exactly,
    /// which absorbs a single OCR digit slip (714 vs 715) while still rejecting a clearly different
    /// batch (714 vs 262, or a digit-swap like 714 vs 774).
    /// </remarks>
    public static bool SameMaterial(OrderMaterial a, OrderMaterial b)
    {
        if (a.Quality != 0 && b.Quality != 0 && !QualityWithinTolerance(a.Quality, b.Quality))
            return false;
        var ta = NameTokens(a.Name);
        var tb = NameTokens(b.Name);
        return ta.Count > 0 && tb.Count > 0 && (ta.IsSubsetOf(tb) || tb.IsSubsetOf(ta));
    }

    private static bool QualityWithinTolerance(int a, int b)
    {
        var diff = Math.Abs(a - b);
        var largest = Math.Max(Math.Abs(a), Math.Abs(b));
        return diff <= largest * QualityToleranceFraction;
    }

    private static HashSet<string> NameTokens(string name) => RefineryParser
        .BaseName(name)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Identity key: <c>STATION | sorted(basename#quality)</c>.</summary>
    public static string Key(string station, IEnumerable<OrderMaterial> materials)
    {
        var keys = materials
            .Select(MaterialKey)
            .Where(k => k.Length > 2) // more than just "#0"
            .Distinct()
            .OrderBy(k => k, StringComparer.Ordinal);
        return $"{RefineryParser.NormalizeName(station)} | {string.Join(",", keys)}";
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
        var candMats = IdentifiableMaterials(candidate);
        if (candMats.Count == 0)
            return false;

        foreach (var e in existing)
        {
            if (RefineryParser.NormalizeName(e.Station) != candStation)
                continue;

            var eMats = IdentifiableMaterials(e);
            if (eMats.Count == 0)
                continue;

            // Materials are paired by SameMaterial (quality + name-token containment) rather than by an
            // exact key, so the raw→refined rename (ICE → PRESSURIZED ICE) still counts as an overlap.
            var intersection = candMats.Count(cm => eMats.Any(em => SameMaterial(cm, em)));
            var union = candMats.Count + eMats.Count - intersection;
            var jaccard = (double)intersection / union;

            // Match gate (station already equal above). Either one set contains the other, or the two
            // overlap strongly. Strong overlap tolerates the OCR reality that any single panel read can
            // miss rows: a partial SETUP read must still merge into the fuller PROCESSING/COMPLETED read.
            var contained = candMats.All(cm => eMats.Any(em => SameMaterial(cm, em)))
                || eMats.All(em => candMats.Any(cm => SameMaterial(cm, em)));
            var strongOverlap = intersection >= MinOverlapMaterials
                && intersection * 2 >= Math.Min(candMats.Count, eMats.Count);
            if (!contained && !strongOverlap)
                continue;

            // Tie-break: fraction of shared materials whose yields agree within tolerance. Weighted
            // tiny so it only separates otherwise-equal (same station, same materials) candidates.
            var closeShared = eMats.Count(em => candMats.Any(cm =>
                SameMaterial(cm, em) && Math.Abs(cm.YieldCscu - em.YieldCscu) <= YieldMatchToleranceCscu));
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

    /// <summary>Materials that carry an identity (non-empty base name), the only ones worth matching on.</summary>
    private static IReadOnlyList<OrderMaterial> IdentifiableMaterials(WorkOrder w)
        => w.Materials.Where(m => NameTokens(m.Name).Count > 0).ToList();
}
