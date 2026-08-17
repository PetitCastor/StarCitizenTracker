namespace RefineryPlugin.Orders;

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
