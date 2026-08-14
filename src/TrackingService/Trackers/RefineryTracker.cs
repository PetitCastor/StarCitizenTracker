using System.Diagnostics;
using Windows.Graphics.Imaging;

namespace TrackingService.Trackers;

/// <summary>
/// Tracks refinery work-order setup: while the SETUP panel is open it scroll-stitches the
/// materials list (rows merge by name, last-seen wins, so scrolling and toggle flips
/// self-correct), and commits the accumulated order when the PROCESSING panel appears
/// after CONFIRM. CANCEL (setup disappears without PROCESSING) discards.
/// </summary>
public sealed class RefineryTracker : ITracker
{
    // Regions are placeholders pending calibration from --save-frames corpus captures.
    // CALIBRATE (2560x1440) — same hardcoded-resolution convention as MissionTracker.
    private static readonly BitmapBounds StationHeaderRoi = new() { X = 220, Y = 250, Width = 650, Height = 60 };
    private static readonly BitmapBounds SetupHeaderRoi = new() { X = 940, Y = 310, Width = 320, Height = 70 };
    private static readonly BitmapBounds ProcessingHeaderRoi = new() { X = 1450, Y = 310, Width = 420, Height = 70 };
    private static readonly BitmapBounds ProcessRoi = new() { X = 620, Y = 545, Width = 460, Height = 60 };
    private static readonly BitmapBounds MaterialsListRoi = new() { X = 620, Y = 640, Width = 440, Height = 340 };
    private static readonly BitmapBounds FooterRoi = new() { X = 610, Y = 980, Width = 500, Height = 140 };
    private static readonly BitmapBounds ToggleStripRoi = new() { X = 1050, Y = 640, Width = 28, Height = 340 };
    private const int ToggleColumnX = 1064; // CALIBRATE — frame-space sample column inside ToggleStripRoi

    private const double HeaderScale = 3.0;
    private const double ListScale = 2.5;
    private const double FooterScale = 3.0;

    // Setup anchor must be missing this many consecutive ticks (~1.5 s) before we act on it —
    // survives OCR flicker and brief UI animations.
    private const int AnchorGoneThreshold = 3;

    private enum State { Idle, Accumulating, AwaitingReset }

    internal sealed class Accumulator
    {
        private int _nextOrder;
        public readonly Dictionary<string, (int Order, MaterialRow Row)> Rows = new(StringComparer.OrdinalIgnoreCase);
        public string? Station, Process, Cost, Time;

        public bool IsEmpty => Rows.Count == 0;

        public void Merge(MaterialRow row)
        {
            Rows[row.Name] = Rows.TryGetValue(row.Name, out var existing)
                ? (existing.Order, row)
                : (_nextOrder++, row);
        }

        public RefineryWorkOrder ToOrder() => new(
            Station ?? "?", Process ?? "?", Cost ?? "?", Time ?? "?",
            Rows.Values.OrderBy(v => v.Order).Select(v => v.Row).ToList());
    }

    private readonly OcrPipeline _ocr;
    private readonly Action<TrackerRecord> _emit;
    private readonly bool _verbose;
    private readonly string? _debugDir;

    private State _state = State.Idle;
    private Accumulator _acc = new();
    private bool? _processingWasVisible;
    private int _setupGoneTicks;
    private int _tick;

    public RefineryTracker(OcrPipeline ocr, Action<TrackerRecord> emit, bool verbose, string? debugDir)
    {
        _ocr = ocr;
        _emit = emit;
        _verbose = verbose;
        _debugDir = debugDir;
    }

    public string Name => "refinery";

    // Orange filled toggle vs neutral dark gray. CALIBRATE against corpus frames,
    // including a hovered row (hover highlight shifts the background).
    internal static bool IsRefineOn((byte B, byte G, byte R) c) => c.R > 140 && c.R > c.B * 1.8;

    public async Task ScanAsync(SoftwareBitmap frame, CancellationToken ct)
    {
        _tick++;
        switch (_state)
        {
            case State.Idle:
                if (await IsAnchorVisibleAsync(frame, SetupHeaderRoi, "SETUP"))
                {
                    Log("setup screen opened, accumulating");
                    _acc = new Accumulator();
                    _processingWasVisible = null;
                    _setupGoneTicks = 0;
                    _state = State.Accumulating;
                }
                break;

            case State.Accumulating:
                await AccumulateTickAsync(frame, ct);
                break;

            case State.AwaitingReset:
                if (!await IsAnchorVisibleAsync(frame, SetupHeaderRoi, "SETUP"))
                {
                    if (++_setupGoneTicks >= AnchorGoneThreshold)
                    {
                        Log("setup screen closed, idle");
                        _state = State.Idle;
                    }
                }
                else
                {
                    _setupGoneTicks = 0;
                }
                break;
        }
    }

    private async Task AccumulateTickAsync(SoftwareBitmap frame, CancellationToken ct)
    {
        // Commit on the *rising edge* of PROCESSING: level-triggering would double-commit,
        // and a leftover PROCESSING panel from a previous order must not commit a fresh
        // accumulator (known limitation — manual hotkey is the escape hatch there).
        var processingVisible = await IsAnchorVisibleAsync(frame, ProcessingHeaderRoi, "PROCESSING");
        if (_processingWasVisible == false && processingVisible)
        {
            await CommitAsync(frame, TriggerKind.Auto, ct);
            _setupGoneTicks = 0;
            _state = State.AwaitingReset;
            return;
        }
        _processingWasVisible = processingVisible;

        if (!await IsAnchorVisibleAsync(frame, SetupHeaderRoi, "SETUP"))
        {
            if (++_setupGoneTicks >= AnchorGoneThreshold)
            {
                Log($"setup screen gone without confirm, discarding {_acc.Rows.Count} rows");
                _state = State.Idle;
            }
            return;
        }
        _setupGoneTicks = 0;

        var sw = Stopwatch.StartNew();
        var list = await _ocr.ReadRegionDetailedAsync(frame, MaterialsListRoi, ListScale);
        var strip = await PixelStrip.CaptureAsync(_ocr, frame, ToggleStripRoi);

        foreach (var row in RefineryParser.ExtractRows(list))
        {
            var (_, frameY) = list.ToFramePoint(0, row.CropCenterY);
            var refineOn = IsRefineOn(strip.AveragePatch(ToggleColumnX, frameY));
            _acc.Merge(new MaterialRow(row.Name, row.QtyScu, row.YieldScu, refineOn));
        }

        // Station/process/footer don't change while the panel is open — refresh occasionally
        // (each read re-encodes the full frame, so keep the per-tick OCR budget down).
        if (_tick % 4 == 0 || _acc.Station is null)
        {
            var stationText = await _ocr.ReadRegionAsync(frame, StationHeaderRoi, HeaderScale);
            var processText = await _ocr.ReadRegionAsync(frame, ProcessRoi, HeaderScale);
            var footerText = await _ocr.ReadRegionAsync(frame, FooterRoi, FooterScale);

            // Last-good-wins: one bad OCR tick must not blank a field already captured.
            _acc.Station = RefineryParser.ParseStation(stationText) ?? _acc.Station;
            _acc.Process = RefineryParser.ParseProcess(processText) ?? _acc.Process;
            _acc.Cost = RefineryParser.ParseCost(footerText) ?? _acc.Cost;
            _acc.Time = RefineryParser.ParseTime(footerText) ?? _acc.Time;
        }

        sw.Stop();
        Log($"tick {sw.ElapsedMilliseconds} ms, {_acc.Rows.Count} rows stitched");
    }

    public async Task OnManualTriggerAsync(SoftwareBitmap frame, CancellationToken ct)
    {
        if (_state == State.Accumulating && !_acc.IsEmpty)
        {
            // Escape hatch: force-commit (e.g. PROCESSING panel never had a rising edge).
            await CommitAsync(frame, TriggerKind.Manual, ct);
            _setupGoneTicks = 0;
            _state = State.AwaitingReset;
            return;
        }

        // Calibration aid: dump raw OCR of the regions this tracker depends on.
        var list = await _ocr.ReadRegionAsync(frame, MaterialsListRoi, ListScale);
        var footer = await _ocr.ReadRegionAsync(frame, FooterRoi, FooterScale);
        _emit(new TrackerRecord(DateTime.Now, Name, TriggerKind.Manual,
            $"[raw list ROI]\r\n{list}\r\n[raw footer ROI]\r\n{footer}"));
    }

    private async Task CommitAsync(SoftwareBitmap frame, TriggerKind trigger, CancellationToken ct)
    {
        if (_acc.IsEmpty)
        {
            Log("commit skipped: no rows accumulated");
            return;
        }

        var order = _acc.ToOrder();
        _emit(new TrackerRecord(DateTime.Now, Name, trigger, order.ToText()));

        if (_debugDir is not null)
        {
            using var listCrop = await _ocr.CropAndScaleAsync(frame, MaterialsListRoi, 1.0);
            var pngPath = await FrameSaver.SavePngAsync(listCrop, _debugDir, "refinery_list");
            await File.WriteAllTextAsync(Path.ChangeExtension(pngPath, ".txt"), order.ToText(), ct);
        }
    }

    private async Task<bool> IsAnchorVisibleAsync(SoftwareBitmap frame, BitmapBounds roi, string anchor)
    {
        var text = await _ocr.ReadRegionAsync(frame, roi, HeaderScale);
        return text.Contains(anchor, StringComparison.OrdinalIgnoreCase);
    }

    private void Log(string message)
    {
        if (_verbose)
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{Name}] {message}");
    }
}
