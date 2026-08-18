using Xunit;

namespace GameCapture.Engine.Tests;

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

        // MinVer always produces a SemVer core, so a leading digit is the cheapest proof that what
        // arrived is a version rather than a placeholder string.
        Assert.Matches(@"^\d+\.\d+\.\d+", version);

        // StartsWith, not equality. There are two separate ways to end up with a meaningless
        // version, and only this catches both: "0.0.0" exactly is EngineStatus's own fallback for a
        // missing attribute, while "0.0.0-alpha.0+<sha>" is what MinVer emits when it finds no v*
        // tag — which is precisely what a default depth-1 CI checkout produces, since it clones no
        // tags. That second form is not equal to "0.0.0", so an equality assertion passes straight
        // through the failure it exists to catch.
        //
        // The trade: this also fails when built from a source archive with no git history at all.
        // That is the intended reading — a build that cannot know its own version should not be
        // shipping one — and ci.yml pins fetch-depth: 0 so the supported path always can.
        Assert.False(version.StartsWith("0.0.0", StringComparison.Ordinal),
            $"engine reported '{version}': MinVer found no v* tag (shallow clone?) or the " +
            "informational-version attribute is missing.");
    }
}
