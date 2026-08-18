namespace GameCapture.Sdk.Testing;

/// <summary>Finds a built <c>GameCapture.Engine.exe</c> for <see cref="ReplayHarness"/> to spawn.</summary>
public static class EngineLocator
{
    private const string EnvVar = "GAMECAPTURE_ENGINE_PATH";
    private const string ExeName = "GameCapture.Engine.exe";
    private const string RelativeBinRoot = "src/GameCapture.Engine/bin";

    /// <summary>
    /// The env var, if set — CI pins this to the exact artifact it built. Otherwise walks up from
    /// the running test assembly to the solution root looking for <c>src/GameCapture.Engine/bin</c>, then
    /// picks the newest <c>GameCapture.Engine.exe</c> under it (Release wins ties over Debug), which is
    /// right for a dev running the harness against whatever they last built locally.
    /// </summary>
    public static string Resolve() =>
        Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } fromEnv
            ? File.Exists(fromEnv)
                ? fromEnv
                : throw new InvalidOperationException($"{EnvVar} is set to '{fromEnv}', but no file exists there.")
            : ProbeBuildOutput()
            ?? throw new InvalidOperationException(
                $"Could not find {ExeName}. Set {EnvVar} to its path, or build the GameCapture.Engine project first.");

    private static string? ProbeBuildOutput()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var binRoot = Path.Combine(dir.FullName, RelativeBinRoot);
            if (Directory.Exists(binRoot) && FindNewestExe(binRoot) is { } exe)
                return exe;
        }
        return null;
    }

    private static string? FindNewestExe(string binRoot) =>
        Directory.EnumerateFiles(binRoot, ExeName, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(p => p.Contains(
                Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
}
