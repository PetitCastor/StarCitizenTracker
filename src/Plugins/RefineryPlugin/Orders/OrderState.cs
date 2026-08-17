namespace RefineryPlugin.Orders;

/// <summary>
/// Lifecycle of a refinery work order. Ordering is significant: the ledger only ever advances
/// state monotonically (see <see cref="OrderStateExtensions.Rank"/>), so a later observation can
/// promote Pending → Processing → Ready → Collected but never regress.
/// </summary>
public enum OrderState { Pending, Processing, Ready, Collected }
