namespace RefineryPlugin;

/// <summary>
/// Decides where a run's <c>OrderLedger</c> writes, and whether that destination is the user's
/// real ledger or a throwaway file that disappears with the temp directory.
/// </summary>
/// <remarks>
/// This is the plugin's half of a rule the monolith also enforced: a replay must never append
/// corpus orders to the user's real <c>orders.jsonl</c>, and the same goes for any run where the
/// ledger is switched off in config — writing to a path nobody reads is how "disabled" is
/// implemented rather than skipping the ledger object entirely. The monolith redirected the same
/// way whenever it was handed <c>--replay</c>; here the decision is pulled out of
/// <c>Program</c>'s connect callback (it cannot run until the engine has answered <c>GetStatus</c>
/// and said whether it is replaying — only the engine knows) so the rule itself can be pinned by a
/// test without standing up a pipe, an engine, or an <c>OrderLedger</c>. An explicit
/// <c>--ledger</c> override always wins, replay or not: that is how the replay-parity harness
/// points a replay run at a file it can read back afterwards.
/// </remarks>
internal static class LedgerTargetResolver
{
    /// <summary>
    /// Reproduces <c>Program</c>'s connect-callback logic exactly: a throwaway path (under
    /// <see cref="System.IO.Path.GetTempPath"/>, named with a fresh <see cref="Guid"/> so concurrent runs
    /// cannot collide) whenever <paramref name="replayMode"/> is set or
    /// <paramref name="ledgerEnabled"/> is not, unless <paramref name="ledgerOverride"/> names a
    /// path explicitly — in which case that path wins outright and the note is blank.
    /// </summary>
    /// <param name="replayMode">From the engine's <c>GetStatus</c> response on first connect.</param>
    /// <param name="ledgerEnabled">Config's <c>LedgerEnabled</c>; false disables the real ledger.</param>
    /// <param name="ledgerOverride">The <c>--ledger</c> CLI argument, or null if not passed.</param>
    /// <param name="configLedgerPath">Config's resolved <c>LedgerPath</c> (always absolute).</param>
    public static LedgerTarget Resolve(bool replayMode, bool ledgerEnabled, string? ledgerOverride,
        string configLedgerPath)
    {
        var throwaway = replayMode || !ledgerEnabled;

        var path = ledgerOverride ?? (throwaway
            ? System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"sc-tracker-{(replayMode ? "replay" : "ephemeral")}-{Guid.NewGuid():N}.jsonl")
            : configLedgerPath);

        var note = ledgerOverride is not null || !throwaway
            ? ""
            : replayMode ? " (replay — throwaway file)" : " (ledger disabled — throwaway file)";

        return new LedgerTarget(path, note);
    }
}
