using Windows.Graphics.Imaging;

namespace TrackingService.Trackers;

/// <summary>
/// Corpus collector: saves the full frame as PNG on every manual hotkey press. Added
/// automatically with --save-frames so replay corpora (--replay) can be captured by
/// walking through a scenario in game and tapping the hotkey at each stage.
/// </summary>
public sealed class FrameDumpTracker : ITracker
{
    private readonly string _outputDir;

    public FrameDumpTracker(string outputDir) => _outputDir = outputDir;

    public string Name => "framedump";

    public Task ScanAsync(SoftwareBitmap frame, CancellationToken ct) => Task.CompletedTask;

    public async Task OnManualTriggerAsync(SoftwareBitmap frame, CancellationToken ct)
    {
        var path = await FrameSaver.SavePngAsync(frame, _outputDir, "frame");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{Name}] saved {path}");
    }
}
