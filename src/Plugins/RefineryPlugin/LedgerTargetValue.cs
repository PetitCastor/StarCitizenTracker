namespace RefineryPlugin;

/// <summary>
/// Resolved ledger file path plus the console note to append after it, as returned by
/// <see cref="LedgerTargetResolver.Resolve"/>.
/// </summary>
internal readonly record struct LedgerTarget(string Path, string Note);
