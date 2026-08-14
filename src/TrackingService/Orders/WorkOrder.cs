namespace TrackingService.Orders;

/// <summary>
/// Lifecycle of a refinery work order. Ordering is significant: the ledger only ever advances
/// state monotonically (see <see cref="OrderStateExtensions.Rank"/>), so a later observation can
/// promote Pending → Processing → Ready → Collected but never regress.
/// </summary>
public enum OrderState { Pending, Processing, Ready, Collected }

/// <summary>
/// Whether a materials read is trustworthy. <see cref="Complete"/> requires the COMPLETED-panel
/// checksum (sum of row yields == printed total) to pass on a non-occluded frame with no
/// edge-dropped rows; <see cref="Partial"/> is a checksum mismatch or dropped rows; <see cref="Unknown"/>
/// is an occluded read (Confirm modal covering the list) that must never be promoted to Complete.
/// </summary>
public enum Completeness { Complete, Partial, Unknown }

/// <summary>
/// One material line of a work order, in integer cSCU (the raw on-screen unit). The ledger stores
/// cSCU rather than the SETUP path's decimal SCU so that rounding drift can never split a record's
/// identity or perturb a match score — divide by 100 only when displaying.
/// </summary>
public sealed record OrderMaterial(string Name, int QtyCscu, int YieldCscu, bool RefineOn);

/// <summary>
/// A persistent refinery work order, observed and merged across the SETUP / PROCESSING / COMPLETED
/// screens. This is the ledger's record type: one line of <c>orders.jsonl</c> per meaningful change.
/// </summary>
/// <param name="Id">
/// Stable unique record identity, assigned by the ledger when a record is first created and
/// preserved across every subsequent merge. This — not <paramref name="Key"/> — is what the JSONL
/// file and the in-memory dictionary key on. It has to be distinct from the match key because two
/// different orders can legitimately share a <paramref name="Key"/> (same station, same materials):
/// an H1 re-run of a mix whose prior order is already Collected spawns a fresh record with an
/// identical key, and keying storage on that would collapse the two. Observations handed to the
/// ledger leave this empty; the ledger fills it.
/// </param>
/// <param name="Key">Match token: <c>station | sorted(normalized material names)</c>. See <c>OrderMatcher.Key</c>. Not unique.</param>
/// <param name="TotalYieldCscu">Parsed COMPLETED-panel <c>YIELD</c> total; null until a COMPLETED read lands.</param>
/// <param name="RowsSeen">Max rows seen in any single observation — a truncation signal, not summed across ticks.</param>
/// <param name="Sources">Distinct screens this record was observed from, in first-seen order (e.g. SETUP, PROCESSING, COMPLETED).</param>
public sealed record WorkOrder(
    string Id,
    string Key,
    string Station,
    string Process,
    string Cost,
    string Eta,
    OrderState State,
    Completeness Completeness,
    IReadOnlyList<OrderMaterial> Materials,
    int? TotalYieldCscu,
    int RowsSeen,
    DateTime FirstSeen,
    DateTime LastSeen,
    IReadOnlyList<string> Sources);

public static class OrderStateExtensions
{
    /// <summary>Monotonic ordering rank. The ledger advances to <c>Max(existing, observed)</c> and never regresses.</summary>
    public static int Rank(this OrderState state) => (int)state;
}
