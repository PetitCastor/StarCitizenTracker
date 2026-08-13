using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;

namespace CaptureProbe.Trackers;

/// <summary>
/// Tracks mission acceptance: watches the contract manager's "ACCEPTED (n/m)" tab counter;
/// when it increments (or on manual hotkey), OCRs the mission-details pane and emits the
/// raw text. Parsing to structured fields is a later phase.
/// </summary>
public sealed partial class MissionTracker : ITracker
{
    [GeneratedRegex(@"Accepted\s*\(?\s*(\d+)\s*/\s*(\d+)\s*\)?", RegexOptions.IgnoreCase)]
    private static partial Regex AcceptedCounter();

    // Regions measured from live 2560x1440 captures (2026-08-13). Resolution-dependent —
    // scaling for other resolutions is future work.
    private static readonly BitmapBounds TabRoi = new() { X = 1000, Y = 110, Width = 420, Height = 100 };
    private const double TabScale = 3.0;
    private static readonly BitmapBounds PaneRoi = new() { X = 860, Y = 180, Width = 1560, Height = 1010 };
    private const double PaneScale = 2.0; // clamped to the OCR engine max dimension by the pipeline

    private readonly OcrPipeline _ocr;
    private readonly Action<TrackerRecord> _emit;
    private readonly bool _verbose;
    private readonly string? _debugDir; // non-null: save pane PNG + txt per capture

    private string? _lastCounter;
    private int _lastAcceptedCount = -1;

    public MissionTracker(OcrPipeline ocr, Action<TrackerRecord> emit, bool verbose, string? debugDir)
    {
        _ocr = ocr;
        _emit = emit;
        _verbose = verbose;
        _debugDir = debugDir;
    }

    public string Name => "missions";

    public async Task ScanAsync(SoftwareBitmap frame, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var tabText = await _ocr.ReadRegionAsync(frame, TabRoi, TabScale);
        sw.Stop();

        if (_verbose)
            Console.WriteLine($"[{Name}] tab ocr {sw.ElapsedMilliseconds} ms: {tabText.ReplaceLineEndings(" ")}");

        var match = AcceptedCounter().Match(tabText);
        if (!match.Success)
        {
            if (_lastCounter is not null && _verbose)
                Console.WriteLine($"[{Name}] counter no longer visible (was {_lastCounter})");
            _lastCounter = null;
            return;
        }

        var accepted = int.Parse(match.Groups[1].Value);
        var counter = $"{match.Groups[1].Value}/{match.Groups[2].Value}";

        if (counter != _lastCounter)
        {
            // Only an *increment* means a mission was just accepted; decrements are
            // completions/abandons, and the first sighting is just the pane opening.
            var isNewMission = _lastAcceptedCount >= 0 && accepted == _lastAcceptedCount + 1;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{Name}] counter {_lastCounter ?? "none"} -> {counter}");

            if (isNewMission)
                await CapturePaneAsync(frame, TriggerKind.Auto, ct);

            _lastCounter = counter;
            _lastAcceptedCount = accepted;
        }
    }

    public Task OnManualTriggerAsync(SoftwareBitmap frame, CancellationToken ct)
        => CapturePaneAsync(frame, TriggerKind.Manual, ct);

    private async Task CapturePaneAsync(SoftwareBitmap frame, TriggerKind trigger, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var paneText = await _ocr.ReadRegionAsync(frame, PaneRoi, PaneScale);
        sw.Stop();

        _emit(new TrackerRecord(DateTime.Now, Name, trigger, paneText));

        if (_verbose)
            Console.WriteLine($"[{Name}] pane ocr {sw.ElapsedMilliseconds} ms, {paneText.Length} chars");

        if (_debugDir is not null)
        {
            using var paneCrop = await _ocr.CropAndScaleAsync(frame, PaneRoi, 1.0);
            var pngPath = await FrameSaver.SavePngAsync(paneCrop, _debugDir, "mission_pane");
            await File.WriteAllTextAsync(Path.ChangeExtension(pngPath, ".txt"), paneText, ct);
        }
    }
}
