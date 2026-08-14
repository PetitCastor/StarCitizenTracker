using System.Text;
using TrackingService.Orders;
using Windows.Graphics.Imaging;

namespace TrackingService.Trackers;

/// <summary>
/// Observes a refinery work order across its three panels and merges each read into the persistent
/// <see cref="OrderLedger"/>. While SETUP is open it scroll-stitches the materials list (rows merge
/// by name, last-seen wins). The middle-column state header is classified every tick into a
/// <see cref="PanelState"/> and fed to a <see cref="PanelStateMachine"/>, so PROCESSING/COMPLETED are
/// captured without any rising-edge bookkeeping — an order already in progress or completed when the
/// tracker starts is picked up on the first clean frame, and the idempotent ledger merge means
/// repeated reads collapse. The COMPLETED panel's printed YIELD total is a checksum: a read whose
/// row-sum matches is <c>Complete</c>, otherwise <c>Partial</c> (with a scroll nudge); a read
/// occluded by the Confirm-Delivery modal is <c>Unknown</c> and never promoted to <c>Complete</c>.
/// </summary>
public sealed class RefineryTracker : ITracker
{
    // Regions are placeholders pending calibration from --save-frames corpus captures.
    // CALIBRATE in 2560x1440 reference coordinates (RoiScaler.Reference*); RoiScaler maps
    // them to the actual frame size at scan time.
    private static readonly BitmapBounds StationHeaderRoi = new() { X = 220, Y = 250, Width = 650, Height = 60 };
    private static readonly BitmapBounds PanelStateRoi = new() { X = 930, Y = 285, Width = 320, Height = 60 };    // SETUP | PROCESSING | COMPLETED
    private static readonly BitmapBounds ProcessRoi = new() { X = 620, Y = 545, Width = 460, Height = 60 };
    private static readonly BitmapBounds MaterialsListRoi = new() { X = 620, Y = 640, Width = 440, Height = 340 }; // SETUP two-number list
    private static readonly BitmapBounds FooterRoi = new() { X = 610, Y = 980, Width = 500, Height = 140 };
    private static readonly BitmapBounds ToggleStripRoi = new() { X = 1050, Y = 640, Width = 28, Height = 340 };
    private const int ToggleColumnX = 1064; // CALIBRATE — reference-space sample column inside ToggleStripRoi
    private static readonly BitmapBounds CompletedListRoi = new() { X = 680, Y = 410, Width = 460, Height = 400 }; // MATERIALS YIELDED (name + yield)
    private static readonly BitmapBounds CompletedTotalRoi = new() { X = 680, Y = 805, Width = 460, Height = 50 }; // "YIELD 644" checksum line
    private static readonly BitmapBounds ConfirmModalRoi = new() { X = 1052, Y = 582, Width = 625, Height = 225 }; // Confirm Delivery modal

    private const double HeaderScale = 3.0;
    private const double ListScale = 2.5;
    private const double FooterScale = 3.0;

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
    private readonly ConsoleSink _sink;
    private readonly bool _verbose;
    private readonly string? _debugDir;
    private readonly OrderLedger _ledger;

    private readonly PanelStateMachine _machine = new();
    private Accumulator _acc = new();
    private WorkOrder? _lastOrder;      // the order to advance to Collected when the panel closes
    private PanelState _lastState = PanelState.None;
    private bool _expectCollect;        // saw a completed/processing panel → watch for the modal even after the header is gone
    private int _tick;

    public RefineryTracker(OcrPipeline ocr, Action<TrackerRecord> emit, ConsoleSink sink, bool verbose,
        string? debugDir, OrderLedger ledger)
    {
        _ocr = ocr;
        _emit = emit;
        _sink = sink;
        _verbose = verbose;
        _debugDir = debugDir;
        _ledger = ledger;
    }

    public string Name => "refinery";

    // Orange filled toggle vs neutral dark gray. CALIBRATE against corpus frames,
    // including a hovered row (hover highlight shifts the background).
    internal static bool IsRefineOn((byte B, byte G, byte R) c) => c.R > 140 && c.R > c.B * 1.8;

    private static int ToCscu(decimal scu) => (int)decimal.Round(scu * 100m);

    /// <summary>Maps a reference-space ROI to this frame's pixel space.</summary>
    private static BitmapBounds R(SoftwareBitmap frame, BitmapBounds referenceRoi)
        => RoiScaler.ToFrame(referenceRoi, frame.PixelWidth, frame.PixelHeight);

    public async Task ScanAsync(SoftwareBitmap frame, CancellationToken ct)
    {
        _tick++;

        var panelText = await _ocr.ReadRegionAsync(frame, R(frame, PanelStateRoi), HeaderScale);
        var state = RefineryParser.Classify(panelText);

        // A fresh SETUP starts a new order — reset the stitching accumulator on its rising edge.
        if (state == PanelState.Setup && _lastState != PanelState.Setup)
        {
            _acc = new Accumulator();
            _expectCollect = false;
        }

        // Only pay for the modal ROI read when it can matter: on a live panel, or while watching for
        // a delivery after the completed panel's header has already gone.
        var needModal = state != PanelState.None || _expectCollect;
        var modalVisible = needModal && await IsModalVisibleAsync(frame);

        // Submit: the SETUP order leaves for PROCESSING/COMPLETED with rows accumulated, so persist
        // the authoritative setup order exactly once. A CANCEL (SETUP -> gone, never PROCESSING)
        // never reaches here, so a discarded setup writes nothing to the ledger.
        if (_lastState == PanelState.Setup
            && (state == PanelState.Processing || state == PanelState.Completed)
            && !_acc.IsEmpty)
        {
            var submit = _ledger.Observe(BuildSetupObservation());
            _lastOrder = submit.Merged;
            if (submit.Changed)
                _emit(new TrackerRecord(DateTime.Now, Name, TriggerKind.Auto, RenderOrder(submit.Merged)));
        }

        var step = _machine.Step(new PanelObservation(state, modalVisible));
        switch (step.Action)
        {
            case LedgerAction.ObserveSetup:
                await AccumulateAsync(frame);
                break;
            case LedgerAction.ObserveCompleted:
                await ObserveCompletedAsync(frame, state, step.Occluded, ct);
                _expectCollect = true;
                break;
            case LedgerAction.MarkCollected:
                MarkCollected();
                _expectCollect = false;
                break;
            case LedgerAction.None:
                break;
        }

        if (step.Note is not null)
            Log(step.Note);

        _lastState = state;
    }

    /// <summary>Stitches the SETUP materials list and header/footer into the accumulator. Provisional
    /// only — nothing is written to the ledger until the order is submitted (see the submit handling
    /// in <see cref="ScanAsync"/>), so a cancelled setup is discarded.</summary>
    private async Task AccumulateAsync(SoftwareBitmap frame)
    {
        var list = await _ocr.ReadRegionDetailedAsync(frame, R(frame, MaterialsListRoi), ListScale);
        var strip = await PixelStrip.CaptureAsync(_ocr, frame, R(frame, ToggleStripRoi));
        var toggleColumnX = RoiScaler.ToFrameX(ToggleColumnX, frame.PixelWidth);

        foreach (var row in RefineryParser.ExtractRows(list))
        {
            var (_, frameY) = list.ToFramePoint(0, row.CropCenterY);
            var refineOn = IsRefineOn(strip.AveragePatch(toggleColumnX, frameY));
            _acc.Merge(new MaterialRow(row.Name, row.QtyScu, row.YieldScu, refineOn));
        }

        // Station/process/footer don't change while the panel is open — refresh occasionally. Since
        // the crop-at-encoder fix each ROI read only re-encodes the ROI (~1.3 ms), so this throttle
        // is a small budget guard, not a correctness requirement.
        if (_tick % 4 == 0 || _acc.Station is null)
        {
            var stationText = await _ocr.ReadRegionAsync(frame, R(frame, StationHeaderRoi), HeaderScale);
            var processText = await _ocr.ReadRegionAsync(frame, R(frame, ProcessRoi), HeaderScale);
            var footerText = await _ocr.ReadRegionAsync(frame, R(frame, FooterRoi), FooterScale);

            // Last-good-wins: one bad OCR tick must not blank a field already captured.
            _acc.Station = RefineryParser.ParseStation(stationText) ?? _acc.Station;
            _acc.Process = RefineryParser.ParseProcess(processText) ?? _acc.Process;
            _acc.Cost = RefineryParser.ParseCost(footerText) ?? _acc.Cost;
            _acc.Time = RefineryParser.ParseTime(footerText) ?? _acc.Time;
        }
    }

    /// <summary>Reads the COMPLETED/PROCESSING yield list + total, runs the checksum, and files the
    /// order as Ready (or Processing), flagging truncated or occluded reads.</summary>
    private async Task ObserveCompletedAsync(SoftwareBitmap frame, PanelState state, bool occluded, CancellationToken ct)
    {
        var listResult = await _ocr.ReadRegionDetailedAsync(frame, R(frame, CompletedListRoi), ListScale);
        var extract = RefineryParser.ExtractYieldRows(listResult);

        int? total = null;
        if (!occluded)
        {
            var totalText = await _ocr.ReadRegionAsync(frame, R(frame, CompletedTotalRoi), HeaderScale);
            total = RefineryParser.ParseYieldTotal(totalText);
        }

        var stationText = await _ocr.ReadRegionAsync(frame, R(frame, StationHeaderRoi), HeaderScale);
        var station = RefineryParser.ParseStation(stationText) ?? _acc.Station ?? "?";

        // Completed-panel rows have no toggle column — they were refined by definition.
        var materials = extract.Rows.Select(r => new OrderMaterial(r.Name, r.QtyCscu, r.YieldCscu, true)).ToList();
        if (materials.Count == 0 && total is null)
            return; // occluded/empty frame carried nothing

        var sum = materials.Sum(m => m.YieldCscu);
        var completeness = occluded
            ? Completeness.Unknown
            : extract.DroppedTopEdge + extract.DroppedBottomEdge == 0 && total is int t && t == sum
                ? Completeness.Complete
                : Completeness.Partial;

        var orderState = state == PanelState.Processing ? OrderState.Processing : OrderState.Ready;
        var source = state == PanelState.Processing ? "PROCESSING" : "COMPLETED";

        var obs = new WorkOrder(
            Id: "", Key: "", Station: station, Process: _acc.Process ?? "?", Cost: _acc.Cost ?? "?",
            Eta: _acc.Time ?? "?", State: orderState, Completeness: completeness, Materials: materials,
            TotalYieldCscu: total, RowsSeen: materials.Count, FirstSeen: DateTime.Now, LastSeen: DateTime.Now,
            Sources: [source]);

        var result = _ledger.Observe(obs);
        _lastOrder = result.Merged;

        if (completeness == Completeness.Partial && result.Changed)
            _sink.WriteLine($"refinery: order at {station} partial — {materials.Count} rows, {sum}/" +
                $"{(total?.ToString() ?? "?")} cSCU. Scroll the list to complete.");

        if (result.Changed && orderState == OrderState.Ready)
        {
            _emit(new TrackerRecord(DateTime.Now, Name, TriggerKind.Auto, RenderOrder(result.Merged)));
            await SaveDebugAsync(frame, ct);
        }
    }

    private void MarkCollected()
    {
        if (_lastOrder is null)
            return;

        var result = _ledger.Observe(_lastOrder with
        {
            Id = "", Key = "", State = OrderState.Collected, LastSeen = DateTime.Now,
        });
        _lastOrder = result.Merged;

        if (result.Changed)
            _emit(new TrackerRecord(DateTime.Now, Name, TriggerKind.Auto, RenderOrder(result.Merged)));
    }

    public async Task OnManualTriggerAsync(SoftwareBitmap frame, CancellationToken ct)
    {
        // Escape hatch: force the current SETUP accumulator into the ledger even if no panel
        // transition fired (e.g. classification stuck).
        if (!_acc.IsEmpty)
        {
            var result = _ledger.Observe(BuildSetupObservation());
            _lastOrder = result.Merged;
            _emit(new TrackerRecord(DateTime.Now, Name, TriggerKind.Manual, RenderOrder(result.Merged)));
            return;
        }

        // Calibration aid: dump raw OCR of the regions this tracker depends on.
        var list = await _ocr.ReadRegionAsync(frame, R(frame, MaterialsListRoi), ListScale);
        var footer = await _ocr.ReadRegionAsync(frame, R(frame, FooterRoi), FooterScale);
        _emit(new TrackerRecord(DateTime.Now, Name, TriggerKind.Manual,
            $"[raw list ROI]\r\n{list}\r\n[raw footer ROI]\r\n{footer}"));
    }

    private WorkOrder BuildSetupObservation()
    {
        var materials = _acc.Rows.Values
            .OrderBy(v => v.Order)
            .Select(v => new OrderMaterial(v.Row.Name, ToCscu(v.Row.QtyScu), ToCscu(v.Row.YieldScu), v.Row.RefineOn))
            .ToList();

        return new WorkOrder(
            Id: "", Key: "", Station: _acc.Station ?? "?", Process: _acc.Process ?? "?",
            Cost: _acc.Cost ?? "?", Eta: _acc.Time ?? "?", State: OrderState.Pending,
            Completeness: Completeness.Unknown, Materials: materials, TotalYieldCscu: null,
            RowsSeen: materials.Count, FirstSeen: DateTime.Now, LastSeen: DateTime.Now, Sources: ["SETUP"]);
    }

    private async Task<bool> IsModalVisibleAsync(SoftwareBitmap frame)
    {
        var text = await _ocr.ReadRegionAsync(frame, R(frame, ConfirmModalRoi), HeaderScale);
        return text.Contains("CONFIRM", StringComparison.OrdinalIgnoreCase)
            || text.Contains("DELIVER", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SaveDebugAsync(SoftwareBitmap frame, CancellationToken ct)
    {
        if (_debugDir is null)
            return;

        using var crop = await _ocr.CropAndScaleAsync(frame, R(frame, CompletedListRoi), 1.0);
        var pngPath = await FrameSaver.SavePngAsync(crop, _debugDir, "refinery_completed");
        if (_lastOrder is not null)
            await File.WriteAllTextAsync(Path.ChangeExtension(pngPath, ".txt"), RenderOrder(_lastOrder), ct);
    }

    private static string RenderOrder(WorkOrder o)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Station: {o.Station}   [{o.State}, {o.Completeness}]");
        sb.AppendLine($"Process: {o.Process}   Cost: {o.Cost}   ETA: {o.Eta}");
        sb.AppendLine($"Materials ({o.Materials.Count}):");
        foreach (var m in o.Materials)
            sb.AppendLine($"  {m.Name,-24} {m.YieldCscu / 100m,8:0.00} SCU  {(m.RefineOn ? "REFINE" : "skip")}");
        if (o.TotalYieldCscu is int total)
            sb.AppendLine($"  Total yield: {total / 100m:0.00} SCU");
        return sb.ToString().TrimEnd();
    }

    private void Log(string message)
    {
        if (_verbose)
            _sink.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{Name}] {message}");
    }
}
