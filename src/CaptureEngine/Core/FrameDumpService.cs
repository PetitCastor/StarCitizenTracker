using Common;
using Windows.Graphics.Imaging;

namespace CaptureEngine;

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

    /// <summary>
    /// Acquires the scan loop's <see cref="ScanLoop.FrameGate"/> and saves the currently
    /// retained frame if one is available.
    /// </summary>
    public async Task<string?> DumpRetainedAsync(ScanLoop loop)
    {
        await loop.FrameGate.WaitAsync();
        try
        {
            if (loop.RetainedFrame is not { } frame)
            {
                _sink?.WriteLine("[frames] no frame retained yet to save");
                return null;
            }

            return await DumpFrameAsync(frame);
        }
        catch (Exception ex)
        {
            _sink?.WriteLine($"[frames] failed to save frame: {ex.Message}");
            return null;
        }
        finally
        {
            loop.FrameGate.Release();
        }
    }
}
