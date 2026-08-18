using Windows.Graphics.Imaging;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// Replay order is the contract: the corpus is chronological only because FrameSaver names are
/// timestamped and the source sorts ordinally. A culture-aware or filesystem-order enumeration
/// would still produce ticks, just in an order that quietly changes what a state machine decides.
/// </summary>
public class ReplayFrameSourceTests
{
    [Fact]
    public async Task NextFrameAsync_ReturnsFramesInOrdinalOrderThenNull()
    {
        using var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);

        var seen = new List<string>();
        while (await source.NextFrameAsync(CancellationToken.None) is { } bitmap)
        {
            using (bitmap)
            {
                // Bgra8 is what every downstream consumer assumes: the OCR crop path, the pixel
                // strip's 4-bytes-per-pixel arithmetic, and PNG dumps alike.
                Assert.Equal(BitmapPixelFormat.Bgra8, bitmap.BitmapPixelFormat);
            }

            seen.Add(source.LastFrameName!);
        }

        // Guard against the whole suite passing vacuously if the fixtures ever stop being copied
        // next to the test assembly: every assertion below is trivially true over zero frames.
        Assert.NotEmpty(seen);
        Assert.Equal(EngineTestFixtures.ExpectedFrameNames(), seen);
        Assert.Equal(seen.Count, source.FrameCount);

        // Exhausted stays exhausted — the scan loop reads null as "corpus finished" and breaks.
        Assert.Null(await source.NextFrameAsync(CancellationToken.None));
    }
}
