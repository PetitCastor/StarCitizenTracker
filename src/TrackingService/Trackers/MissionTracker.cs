using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;

namespace TrackingService.Trackers;

/// <summary>
/// Tracks mission acceptance: watches the contract manager's "ACCEPTED (n/m)" tab counter;
/// when it increments (or on manual hotkey), OCRs the mission-details pane and emits the
/// raw text. Parsing to structured fields is a later phase.
/// </summary>
public sealed partial class MissionTracker : ITracker
{
    [GeneratedRegex(@"Accepted\s*\(?\s*(\d+)\s*/\s*(\d+)\s*\)?", RegexOptions.IgnoreCase)]
    private static partial Regex AcceptedCounter();

    // Regions measured from live 2560x1440 captures (2026-08-13), kept in reference
    // coordinates; RoiScaler maps them to the actual frame size at scan time.
    private static readonly BitmapBounds TabRoi = new() { X = 1000, Y = 110, Width = 420, Height = 100 };
    private const double TabScale = 3.0;
    private static readonly BitmapBounds PaneRoi = new() { X = 860, Y = 180, Width = 1560, Height = 1010 };
    private const double PaneScale = 2.0; // clamped to the OCR engine max dimension by the pipeline

    private readonly OcrPipeline _ocr;
    private readonly Action<TrackerRecord> _emit;
    private readonly ConsoleSink _sink;
    private readonly bool _verbose;
    private readonly string? _debugDir; // non-null: save pane PNG + txt per capture

    private string? _lastCounter;
    private int _lastAcceptedCount = -1;

    public MissionTracker(OcrPipeline ocr, Action<TrackerRecord> emit, ConsoleSink sink, bool verbose, string? debugDir)
    {
        _ocr = ocr;
        _emit = emit;
        _sink = sink;
        _verbose = verbose;
        _debugDir = debugDir;
    }

    public string Name => "missions";

    /// <summary>Maps a reference-space ROI to this frame's pixel space.</summary>
    private static BitmapBounds R(SoftwareBitmap frame, BitmapBounds referenceRoi)
        => RoiScaler.ToFrame(referenceRoi, frame.PixelWidth, frame.PixelHeight);

    public async Task ScanAsync(SoftwareBitmap frame, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var tabText = await _ocr.ReadRegionAsync(frame, R(frame, TabRoi), TabScale);
        sw.Stop();

        if (_verbose)
            _sink.WriteLine($"[{Name}] tab ocr {sw.ElapsedMilliseconds} ms: {tabText.ReplaceLineEndings(" ")}");

        var parsed = ParseAcceptedCounter(tabText);
        if (parsed is null)
        {
            if (_lastCounter is not null && _verbose)
                _sink.WriteLine($"[{Name}] counter no longer visible (was {_lastCounter})");
            _lastCounter = null;
            return;
        }

        var (accepted, total) = parsed.Value;
        var counter = $"{accepted}/{total}";

        if (counter != _lastCounter)
        {
            // Only an *increment* means a mission was just accepted; decrements are
            // completions/abandons, and the first sighting is just the pane opening.
            var isNewMission = IsNewMissionAccepted(_lastAcceptedCount, accepted);
            _sink.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{Name}] counter {_lastCounter ?? "none"} -> {counter}");

            if (isNewMission)
                await CapturePaneAsync(frame, TriggerKind.Auto, ct);

            _lastCounter = counter;
            _lastAcceptedCount = accepted;
        }
    }

    /// <summary>Parses the "ACCEPTED (n/m)" tab counter text, tolerating OCR spacing variance.</summary>
    internal static (int Accepted, int Total)? ParseAcceptedCounter(string tabText)
    {
        var match = AcceptedCounter().Match(tabText);
        return match.Success
            ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value))
            : null;
    }

    /// <summary>
    /// True only when the counter just incremented by one — a fresh accept. Decrements
    /// (completions/abandons) and the first sighting (<paramref name="previousAccepted"/> == -1)
    /// are not new missions.
    /// </summary>
    internal static bool IsNewMissionAccepted(int previousAccepted, int currentAccepted)
        => previousAccepted >= 0 && currentAccepted == previousAccepted + 1;

    public Task OnManualTriggerAsync(SoftwareBitmap frame, CancellationToken ct)
        => CapturePaneAsync(frame, TriggerKind.Manual, ct);

    private async Task CapturePaneAsync(SoftwareBitmap frame, TriggerKind trigger, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var paneText = await _ocr.ReadRegionAsync(frame, R(frame, PaneRoi), PaneScale);
        sw.Stop();

        _emit(new TrackerRecord(DateTime.Now, Name, trigger, paneText));

        if (_verbose)
            _sink.WriteLine($"[{Name}] pane ocr {sw.ElapsedMilliseconds} ms, {paneText.Length} chars");

        if (_debugDir is not null)
        {
            using var paneCrop = await _ocr.CropAndScaleAsync(frame, R(frame, PaneRoi), 1.0);
            var pngPath = await FrameSaver.SavePngAsync(paneCrop, _debugDir, "mission_pane");
            await File.WriteAllTextAsync(Path.ChangeExtension(pngPath, ".txt"), paneText, ct);
        }
    }
}
