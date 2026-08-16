using Xunit;

namespace RefineryPlugin.Tests;

/// <summary>
/// Pins the ledger-safety rule that used to live only inside a lambda in <c>Program</c>'s
/// top-level statements, unreachable from any test: a replay run, or a run with the ledger
/// disabled in config, must never append to the user's real <c>orders.jsonl</c> — it gets
/// redirected to a throwaway temp file instead. The monolith redirected the same way whenever it
/// was handed <c>--replay</c>; here the decision has to wait for the engine's first
/// <c>GetStatus</c> answer, because only the engine knows whether it is replaying a corpus, which
/// is why <see cref="LedgerTargetResolver.Resolve"/> exists as a callback rather than something
/// <c>Program</c> can decide up front. An explicit <c>--ledger</c> override always wins — that is
/// how the replay-parity harness points a replay at a file it can read back afterwards.
/// </summary>
public class LedgerTargetTests
{
    private const string ConfigPath = @"C:\fake\StarCitizenTracker\orders.jsonl";

    [Fact]
    public void ReplayMode_WithNoOverride_RedirectsToAThrowawayReplayFile()
    {
        var target = LedgerTargetResolver.Resolve(replayMode: true, ledgerEnabled: true, ledgerOverride: null, ConfigPath);

        Assert.StartsWith(Path.GetTempPath(), target.Path);
        Assert.NotEqual(ConfigPath, target.Path);
        Assert.Contains("replay", target.Path);
        Assert.EndsWith(".jsonl", target.Path);
        Assert.Equal(" (replay — throwaway file)", target.Note);
    }

    [Fact]
    public void LedgerDisabled_WithNoOverride_RedirectsToAThrowawayEphemeralFile()
    {
        var target = LedgerTargetResolver.Resolve(replayMode: false, ledgerEnabled: false, ledgerOverride: null, ConfigPath);

        Assert.StartsWith(Path.GetTempPath(), target.Path);
        Assert.NotEqual(ConfigPath, target.Path);
        Assert.Contains("ephemeral", target.Path);
        Assert.Equal(" (ledger disabled — throwaway file)", target.Note);
    }

    [Fact]
    public void ReplayAndLedgerDisabled_TheReplayTokenWinsTheFilename()
    {
        // Both conditions make the run throwaway; the filename only needs to say which reason,
        // and replay is the one that also governs the note, so it takes the token too.
        var target = LedgerTargetResolver.Resolve(replayMode: true, ledgerEnabled: false, ledgerOverride: null, ConfigPath);

        Assert.StartsWith(Path.GetTempPath(), target.Path);
        Assert.Contains("replay", target.Path);
        Assert.DoesNotContain("ephemeral", target.Path);
        Assert.Equal(" (replay — throwaway file)", target.Note);
    }

    [Fact]
    public void NormalRun_UsesTheConfigPathVerbatimWithNoNote()
    {
        var target = LedgerTargetResolver.Resolve(replayMode: false, ledgerEnabled: true, ledgerOverride: null, ConfigPath);

        Assert.Equal(ConfigPath, target.Path);
        Assert.Equal("", target.Note);
    }

    /// <summary>
    /// The important case: this is how the replay-parity harness (and anyone debugging a replay
    /// by hand) points a replay run at a file it can inspect afterwards, instead of one that
    /// vanishes with the temp directory.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ExplicitOverride_AlwaysWinsRegardlessOfReplayOrLedgerEnabled(bool replayMode, bool ledgerEnabled)
    {
        const string overridePath = @"C:\fake\override.jsonl";

        var target = LedgerTargetResolver.Resolve(replayMode, ledgerEnabled, overridePath, ConfigPath);

        Assert.Equal(overridePath, target.Path);
        Assert.Equal("", target.Note);
    }

    /// <summary>
    /// The Guid in the throwaway filename is what lets two runs started at the same instant — two
    /// replay-parity tests in the same test session, say — write to different files instead of
    /// racing each other on one.
    /// </summary>
    [Fact]
    public void TwoThrowawayResolutions_NeverReturnTheSamePath()
    {
        var first = LedgerTargetResolver.Resolve(replayMode: true, ledgerEnabled: true, ledgerOverride: null, ConfigPath);
        var second = LedgerTargetResolver.Resolve(replayMode: true, ledgerEnabled: true, ledgerOverride: null, ConfigPath);

        Assert.NotEqual(first.Path, second.Path);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Throwaway_NeverReturnsTheConfigPath(bool replayMode, bool ledgerEnabled)
    {
        var target = LedgerTargetResolver.Resolve(replayMode, ledgerEnabled, ledgerOverride: null, ConfigPath);

        Assert.NotEqual(ConfigPath, target.Path);
    }
}
