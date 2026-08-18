namespace GameCapture.Sdk.Testing;

/// <summary>Resolves a replay corpus linked into a test assembly's own output. See
/// <c>docs/REPLAY.md</c> for corpus layout.</summary>
public static class ReplayCorpus
{
    /// <summary>Absolute path to <paramref name="relativeDir"/> (e.g. "Fixtures/Replay/refinery-confirm")
    /// under the calling test assembly's output directory. Absolute because <see cref="ReplayHarness"/>
    /// spawns a child process with its own working directory, so a relative path would resolve
    /// against the wrong one.</summary>
    public static string Resolve(string relativeDir) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativeDir));
}
