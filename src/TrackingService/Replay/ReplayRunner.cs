using TrackingService.Trackers;
using Windows.Graphics.Imaging;

namespace TrackingService.Replay;

/// <summary>
/// Offline mode: runs saved full-frame PNGs (see FrameDumpTracker) through a set of trackers
/// in filename order (= chronological, FrameSaver names are timestamped). No capture, no
/// hotkey — deterministic verification without the game running. Trackers must already be
/// wired to whatever <c>Action&lt;TrackerRecord&gt;</c> the caller wants captures emitted to.
/// </summary>
public static class ReplayRunner
{
    /// <returns>Number of frame PNGs processed, in replay order.</returns>
    public static async Task<int> RunAsync(
        string replayDir, IReadOnlyList<ITracker> trackers, ConsoleSink sink, bool verbose = false)
    {
        var frames = Directory.GetFiles(replayDir, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();

        foreach (var framePath in frames)
        {
            if (verbose)
                sink.WriteLine($"--- {Path.GetFileName(framePath)} ---");

            using var fileStream = File.OpenRead(framePath);
            var decoder = await BitmapDecoder.CreateAsync(fileStream.AsRandomAccessStream());
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

            foreach (var tracker in trackers)
                await tracker.ScanAsync(bitmap, CancellationToken.None);
        }

        return frames.Count;
    }
}
