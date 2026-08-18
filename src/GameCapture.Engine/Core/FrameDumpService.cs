using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

/// <summary>
/// Saves full-frame PNG dumps to the configured output directory upon manual triggers.
/// Ported from the monolith's FrameDumpTracker for the engine/plugin split.
/// </summary>
internal sealed class FrameDumpService
{
    private readonly string _outputDir;
    private readonly ConsoleSink? _sink;

    public FrameDumpService(string outputDir, ConsoleSink? sink = null)
    {
        _outputDir = outputDir;
        _sink = sink;
    }

    /// <summary>
    /// Saves a copy of <paramref name="frame"/> as a timestamped PNG in the output directory.
    /// Does not dispose the bitmap.
    /// </summary>
    public async Task<string> DumpFrameAsync(SoftwareBitmap frame)
    {
        var path = await FrameSaver.SavePngAsync(frame, _outputDir, "frame");
        _sink?.WriteLine($"[frames] saved {path}");
        return path;
    }

}
