using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// The same truth table <c>GameCapture.Sdk.Tests.ConsoleSinkFitToWidthTests</c> asserts, against the
/// engine's own copy of the class.
/// </summary>
/// <remarks>
/// A duplicated test for a deliberately duplicated class (see the remark on
/// <c>GameCapture.Engine/Core/ConsoleSink.cs</c>): the two are allowed to drift, and the moment one of
/// them drifts on THIS behaviour — a status row that touches the last column scrolls the buffer, so
/// the truncation is load-bearing rather than cosmetic — the copy that drifted must fail on its own,
/// not stay silently untested because the other copy still passes.
/// </remarks>
public class ConsoleSinkFitToWidthTests
{
    [Fact]
    public void FitToWidth_ShorterThanWidth_ReturnsUnchanged()
        => Assert.Equal("CPU 5%", ConsoleSink.FitToWidth("CPU 5%", 80));

    [Fact]
    public void FitToWidth_ExactlyWidthMinusOne_ReturnsUnchanged()
        => Assert.Equal("1234", ConsoleSink.FitToWidth("1234", 5));

    [Fact]
    public void FitToWidth_ExactlyWidth_TruncatesToWidthMinusOne()
    {
        // Never touch the last column — writing the bottom-right cell scrolls the buffer.
        Assert.Equal("1234", ConsoleSink.FitToWidth("12345", 5));
    }

    [Fact]
    public void FitToWidth_LongerThanWidth_Truncates()
        => Assert.Equal("abcdefghi", ConsoleSink.FitToWidth("abcdefghijklmnop", 10));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-5)]
    public void FitToWidth_DegenerateWidth_ReturnsEmpty(int width)
        => Assert.Equal("", ConsoleSink.FitToWidth("anything", width));

    [Fact]
    public void FitToWidth_EmptyText_ReturnsEmpty()
        => Assert.Equal("", ConsoleSink.FitToWidth("", 80));

    [Fact]
    public void FitToWidth_EmbeddedNewlines_CollapseToSpaces()
    {
        // Status must stay a single row even if a caller sneaks a newline in.
        Assert.Equal("a b c", ConsoleSink.FitToWidth("a\r\nb\nc", 80));
    }
}
