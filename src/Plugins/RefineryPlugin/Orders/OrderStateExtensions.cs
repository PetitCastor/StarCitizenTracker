namespace RefineryPlugin.Orders;

/// <summary>Extension methods for <see cref="OrderState"/>.</summary>
public static class OrderStateExtensions
{
    /// <summary>Monotonic ordering rank. The ledger advances to <c>Max(existing, observed)</c> and never regresses.</summary>
    public static int Rank(this OrderState state) => (int)state;
}
