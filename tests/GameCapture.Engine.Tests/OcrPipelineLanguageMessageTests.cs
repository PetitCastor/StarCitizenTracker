using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// The engine-construction path itself needs a real OCR pack, so only the diagnostics are
/// pinned here: a bad language tag is the most likely first-run failure and the message is
/// the only thing the user gets to act on.
/// </summary>
public class OcrPipelineLanguageMessageTests
{
    [Fact]
    public void DescribeMissingPack_NamesTheRequestedTag()
    {
        var message = OcrPipeline.DescribeMissingPack("de-DE", ["en-US", "fr-FR"]);

        Assert.Contains("'de-DE'", message);
    }

    [Fact]
    public void DescribeMissingPack_ListsInstalledTags()
    {
        var message = OcrPipeline.DescribeMissingPack("de-DE", ["en-US", "fr-FR"]);

        Assert.Contains("Installed: en-US, fr-FR", message);
        Assert.Contains("ocrLanguage", message);
    }

    [Fact]
    public void DescribeMissingPack_WithNothingInstalled_SaysSoInsteadOfListingNothing()
    {
        var message = OcrPipeline.DescribeMissingPack("en-US", []);

        Assert.Contains("No OCR packs are installed at all.", message);
        Assert.DoesNotContain("Installed:", message);
    }

    [Fact]
    public void DescribeNoUserProfilePack_PointsAtTheDisplayLanguage()
    {
        var message = OcrPipeline.DescribeNoUserProfilePack(["en-US"]);

        Assert.Contains("Windows display language", message);
        Assert.Contains("Installed: en-US", message);
    }

    [Fact]
    public void BothMessages_ExplainHowToInstallAPack()
    {
        string[] installed = ["en-US", "fr-FR"];

        Assert.Contains("Optional language features", OcrPipeline.DescribeMissingPack("de-DE", installed));
        Assert.Contains("Optional language features", OcrPipeline.DescribeNoUserProfilePack(installed));
    }

    [Fact]
    public void BothMessages_ExplainHowToInstallAPack_EvenWithNothingInstalled()
    {
        Assert.Contains("Optional language features", OcrPipeline.DescribeMissingPack("de-DE", []));
        Assert.Contains("Optional language features", OcrPipeline.DescribeNoUserProfilePack([]));
    }
}
