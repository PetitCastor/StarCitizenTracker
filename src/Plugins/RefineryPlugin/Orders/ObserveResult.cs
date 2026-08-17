namespace RefineryPlugin.Orders;

/// <summary>Outcome of one <see cref="OrderLedger.Observe"/> call.</summary>
/// <param name="Merged">The record as it now stands after merging the observation.</param>
/// <param name="Changed">Whether the observation changed anything worth persisting (drives the append).</param>
public readonly record struct ObserveResult(WorkOrder Merged, bool Changed);
