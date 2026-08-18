using Xunit;

namespace CaptureEngine.Tests;

/// <summary>
/// The version the engine reports on the wire comes from <c>AssemblyInformationalVersion</c>, which
/// MinVer computes from the height above the latest <c>v*</c> tag (Directory.Build.props). Nothing
/// in the build fails when that goes wrong: the attribute is simply absent, <c>EngineStatus</c>
/// falls through to its "0.0.0" default, and every plugin is told it is talking to an engine of
/// unknown build — a diagnostic that reads as a real answer.
/// </summary>
/// <remarks>
/// Pins the pipeline, not a number. Asserting an exact version would fail on the next commit, and
/// asserting the tag height would encode this branch's distance from a release into a test.
/// </remarks>
public class EngineVersionTests
{
    [Fact]
    public void EngineVersion_isNotTheUnknownFallback()
    {
        var version = new EngineStatus(ocrLanguage: "en", replayMode: false).Snapshot().EngineVersion;

        Assert.False(string.IsNullOrWhiteSpace(version));

        // "0.0.0" is EngineStatus's own last-resort default, reached only when neither
        // AssemblyInformationalVersion nor the assembly version is present.
        Assert.NotEqual("0.0.0", version);

        // MinVer always produces a SemVer core, so a leading digit is the cheapest proof that what
        // arrived is a version rather than a placeholder string.
        Assert.Matches(@"^\d+\.\d+\.\d+", version);
    }
}
