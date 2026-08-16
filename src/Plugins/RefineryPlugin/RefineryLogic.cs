using System.Text;
using CaptureContracts;
using RefineryPlugin.Orders;
using TrackerSdk;

namespace RefineryPlugin;

/// <summary>
/// The regions this plugin subscribes, in reference space. Static for the life of the process:
/// per-tick atomicity means every decision is made from one tick, so the set a tick can answer
/// must be complete before the tick arrives — there is no mid-tick round-trip to add a ROI.
/// </summary>
/// <remarks>
/// Deliberate design change against the monolith: RefineryTracker gated its reads to save in-process
/// OCR — the Confirm-Delivery modal was only read when a panel was live (<c>needModal</c>), and
/// station/process/footer only on every 4th tick. Engine-side those reads cost about 1.3 ms each at
/// 2 Hz, which is not worth a gate, so the plugin subscribes the full set on every tick and the
/// header cadence is gone. Per-tick semantics are unchanged: everything still comes from one frame.
/// </remarks>
internal static class Rois
{
    private const double HeaderScale = 3.0;
    private const double ListScale = 2.5;
    private const double FooterScale = 3.0;

    // Calibrated against the 2560x1440 corpus (Fixtures/Replay/refinery-confirm) in reference
    // coordinates; the engine maps them to the actual frame size at scan time.
    public static readonly RoiSubscription Panel =                                    // SETUP | PROCESSING | COMPLETED
        new("panel", new RoiRect(900, 265, 250, 55), HeaderScale, RoiKind.Text);
    public static readonly RoiSubscription Station =                                  // "STANTON GATEWAY"
        new("station", new RoiRect(320, 190, 340, 55), HeaderScale, RoiKind.Text);
    public static readonly RoiSubscription Process =                                  // "Pyrometric Chromalysis"
        new("process", new RoiRect(650, 515, 440, 48), HeaderScale, RoiKind.Text);
    public static readonly RoiSubscription SetupList =                                // SETUP list: NAME QUALITY QTY YIELD
        new("setupList", new RoiRect(650, 640, 400, 270), ListScale, RoiKind.Detailed);
    public static readonly RoiSubscription Footer =                                   // TOTAL COST / PROCESSING TIME
        new("footer", new RoiRect(650, 950, 440, 120), FooterScale, RoiKind.Text);
    public static readonly RoiSubscription Toggles =                                  // SETUP refine toggles
        new("toggles", new RoiRect(1055, 645, 40, 250), 1.0, RoiKind.Pixels);
    public static readonly RoiSubscription YieldList =                                // PROCESSING/COMPLETED: NAME QUALITY YIELD ...
        new("yieldList", new RoiRect(650, 395, 470, 210), ListScale, RoiKind.Detailed);
    public static readonly RoiSubscription YieldTotal =                               // "YIELD 303 cSCU" checksum line
        new("yieldTotal", new RoiRect(650, 805, 480, 48), HeaderScale, RoiKind.Text);
    public static readonly RoiSubscription Modal =                                    // Confirm Delivery modal
        new("modal", new RoiRect(1052, 582, 625, 225), HeaderScale, RoiKind.Text);

    /// <summary>Reference-space sample column inside the toggle pill.</summary>
    public const int ToggleColumnX = 1073;

    // A field, not an expression-bodied property: the set never changes, and `=> [Panel, ...]`
    // would build a fresh array on every read.
    public static readonly IReadOnlyList<RoiSubscription> All =
        [Panel, Station, Process, SetupList, Footer, Toggles, YieldList, YieldTotal, Modal];
}

/// <summary>
/// Observes a refinery work order across its three panels and merges each read into the persistent
/// <see cref="OrderLedger"/>. While SETUP is open it scroll-stitches the materials list (rows keyed
/// by name+quality, last-seen wins). The middle-column state header is classified every tick into a
/// <see cref="PanelState"/> and fed to a <see cref="PanelStateMachine"/>, so PROCESSING/COMPLETED are
/// captured without any rising-edge bookkeeping — an order already in progress or completed when the
/// tracker starts is picked up on the first clean frame, and the idempotent ledger merge means
/// repeated reads collapse. The COMPLETED panel's printed YIELD total is a checksum (within a
/// rounding tolerance): a matching row-sum marks the read <c>Complete</c>, otherwise <c>Partial</c>
/// (with a scroll nudge); a read occluded by the Confirm-Delivery modal is <c>Unknown</c> and never
/// promoted to <c>Complete</c>.
/// </summary>
/// <remarks>
/// Port of the monolith's RefineryTracker. The OCR and the pixel sampling now happen engine-side and
/// arrive together on one <see cref="TickData"/>; the read gates that saved in-process OCR are gone
/// (see <see cref="Rois"/>), and with the every-4th-tick header cadence goes the tick counter that
/// drove it. Everything else — parsing, state, log strings, emit format — is unchanged.
/// </remarks>
public sealed class RefineryLogic
{
    /// <summary>Client name on the Track stream and the <see cref="TrackerRecord.Tracker"/> tag.</summary>
    public const string Name = "refinery";

    /// <summary>Yield checksum slack: per-row cSCU are rounded, so the printed total can differ from
    /// the row-sum by up to about a cSCU per row.</summary>
    private static int ChecksumTolerance(int rows) => Math.Max(2, rows);

    internal sealed class Accumulator
    {
        private int _nextOrder;
        public readonly Dictionary<string, (int Order, OrderMaterial Mat)> Rows = new(StringComparer.Ordinal);
        public string? Station, Process, Cost, Time;

        public bool IsEmpty => Rows.Count == 0;

        public void Merge(OrderMaterial material)
        {
            var key = OrderMatcher.MaterialKey(material);
            Rows[key] = Rows.TryGetValue(key, out var existing)
                ? (existing.Order, material)
                : (_nextOrder++, material);
        }

        public IReadOnlyList<OrderMaterial> Materials
            => Rows.Values.OrderBy(v => v.Order).Select(v => v.Mat).ToList();
    }

    private readonly Action<TrackerRecord> _emit;
    private readonly ConsoleSink _sink;
    private readonly bool _verbose;
    private readonly OrderLedger _ledger;

    // Non-null: ask the engine to dump the completed-panel PNG per emitted order and write the
    // rendered order beside it. A func rather than the client itself so the logic stays testable
    // without a pipe.
    private readonly Func<RoiRect?, string, Task<string?>>? _dumpFrame;

    private readonly PanelStateMachine _machine = new();
    private readonly SetupDepartureDebouncer _setupDebouncer = new();
    private Accumulator _acc = new();
    private WorkOrder? _lastOrder;      // the order to advance to Collected when the panel closes
    private bool _expectCollect;        // saw a completed/processing panel → watch for the modal even after the header is gone
    private bool _observedThisCycle;    // the current completed/processing cycle actually produced a yield read

    // ROI ids already reported as unreadable, cleared per id as soon as it reads again. A ROI that
    // fails for a structural reason — a toggle strip that scales past the wire's pixel budget on a
    // very large frame, say — fails on every tick, and a 2 Hz repeat would bury the console; silence
    // would be worse, since the symptom downstream is orders quietly filed with "?" fields.
    private readonly HashSet<string> _reportedFailures = new(StringComparer.Ordinal);

    public RefineryLogic(Action<TrackerRecord> emit, ConsoleSink sink, bool verbose,
        Func<RoiRect?, string, Task<string?>>? dumpFrame, OrderLedger ledger)
    {
        _emit = emit;
        _sink = sink;
        _verbose = verbose;
        _dumpFrame = dumpFrame;
        _ledger = ledger;
    }

    // Refine toggle: orange/red fill when ON (R high, B low), white knob when OFF (R≈B), dark when
    // disabled. Sampling the pill's fill column separates all three. Validated against the corpus.
    internal static bool IsRefineOn((byte B, byte G, byte R) c) => c.R > 140 && c.R > c.B * 1.8;

    public async Task OnTickAsync(TickData tick, CancellationToken ct = default)
    {
        // Manual first, then the normal scan — the order the monolith used when a hotkey press
        // was queued, so a press during a panel transition still forces the accumulator out first.
        if (tick.Manual)
            OnManualTrigger(tick);

        // The monolith read the state header and the modal before it touched any state at all, so
        // an OCR failure on either aborted the whole tick (its host caught it) and nothing moved.
        // Here a failure arrives as a per-ROI flag and the ROI still reads as empty text — which is
        // indistinguishable from a closed panel and from a dismissed modal, and the state machine
        // acts on both readings: an errored panel after a CANCEL looks like the panel closing and
        // fabricates a Collected order, and an errored modal looks like the confirm being dismissed
        // and throws away a real delivery. So the abort is reconstructed here. Skipping costs one
        // tick; the next frame is 500 ms away and every reader is idempotent.
        // Both are evaluated, never short-circuited: RoiFailed also clears a ROI's reported-once
        // latch when it reads cleanly again, and a panel failure must not hide the modal's recovery.
        var panelFailed = RoiFailed(tick, Rois.Panel.Id);
        var modalFailed = RoiFailed(tick, Rois.Modal.Id);
        if (panelFailed || modalFailed)
            return;

        var panelText = tick.Text(Rois.Panel.Id);
        var state = RefineryParser.Classify(panelText);

        // Debounced SETUP-session bookkeeping (H4): a single OCR-flicker tick must not reset the
        // scroll-stitch accumulator or fire a premature submit with a half-stitched order. See
        // SetupDepartureDebouncer for the confirm-N-ticks-before-acting rule. This only gates the
        // accumulator lifecycle — panel *content* below is still read every tick from the raw
        // classification, so a genuinely-transitioned panel is never read late.
        var transition = _setupDebouncer.Observe(state);
        if (transition.OpenedFresh)
        {
            // A fresh SETUP starts a new order — reset the stitching accumulator. _lastOrder belongs
            // to the order that just ended, so the new cycle starts with nothing to collect.
            _acc = new Accumulator();
            _expectCollect = false;
            _observedThisCycle = false;
        }
        else if (transition.DepartedTo == PanelState.None)
        {
            // SETUP closed without ever confirming a submit — a cancelled/abandoned order. Discard
            // the accumulator (H3) so its station/process/cost can't leak into a later, unrelated
            // yield-panel read; the station-reuse heuristic in ObserveYieldPanelAsync only trusts
            // _acc.Station while it belongs to the current cycle, which a fresh Accumulator restores.
            _acc = new Accumulator();
        }

        // The modal text is on the tick either way now, but the guard still decides when a modal
        // *counts*: only on a live panel, or while watching for a delivery after the completed
        // panel's header has already gone.
        var needModal = state != PanelState.None || _expectCollect;
        var modalVisible = needModal && IsModalVisible(tick.Text(Rois.Modal.Id));

        // Submit: the SETUP order leaves for PROCESSING/COMPLETED with rows accumulated, so persist
        // the authoritative setup order exactly once, once the debouncer confirms the departure.
        if (transition.DepartedTo is PanelState.Processing or PanelState.Completed && !_acc.IsEmpty)
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
                Accumulate(tick);
                break;
            case LedgerAction.ObserveCompleted:
                await ObserveYieldPanelAsync(tick, state, step.Occluded, ct);
                _expectCollect = true;
                break;
            case LedgerAction.MarkCollected:
                MarkCollected();
                _expectCollect = false;
                _observedThisCycle = false; // the cycle is closed; the next one has its own order
                break;
            case LedgerAction.None:
                break;
        }

        if (step.Note is not null)
            Log(step.Note);
    }

    /// <summary>Stitches the SETUP materials list (NAME · QUALITY · QTY · YIELD) and header/footer
    /// into the accumulator. Provisional only — nothing is written to the ledger until submit.</summary>
    private void Accumulate(TickData tick)
    {
        // A ROI the engine failed to read is skipped for this tick rather than treated as an empty
        // list: the next tick re-reads it, and a blank stitch would drop rows already accumulated.
        // A strip that never arrived would sample as black and file every row as "not refined",
        // which is a wrong reading rather than a missing one.
        var listFailed = RoiFailed(tick, Rois.SetupList.Id);
        var stripFailed = RoiFailed(tick, Rois.Toggles.Id);
        if (listFailed || stripFailed)
            return;

        var list = tick.Ocr(Rois.SetupList.Id);
        var strip = tick.Pixels(Rois.Toggles.Id);
        if (list is null || strip is null)
            return;

        var toggleColumnX = RoiScaler.ToFrameX(Rois.ToggleColumnX, tick.FrameWidth);

        foreach (var row in RefineryParser.ExtractColumnarRows(list).Rows)
        {
            // 0 is the deliberate sentinel for "quality unreadable this tick" — OrderMatcher.SameMaterial
            // treats a 0 quality on either side as unknown and wildcards the comparison, rather than
            // letting a blank/misread column collapse into a false-different (or false-same) batch.
            var quality = Num(row, 0) ?? 0;
            var qty = Num(row, 1) ?? 0;
            var yield = Num(row, 2) ?? 0; // "--" before a quote
            var (_, frameY) = list.ToFramePoint(0, row.CropCenterY);
            var refineOn = IsRefineOn(strip.AveragePatch(toggleColumnX, frameY));
            Log($"setup row {row.Name} [{string.Join(",", row.Numbers)}] refine={refineOn}");
            _acc.Merge(new OrderMaterial(row.Name, quality, qty, yield, refineOn));
        }

        // Every tick now: the header/footer text is already on the tick, so the monolith's
        // every-4th-tick cadence would only be skipping work that has already been paid for.
        var stationText = tick.Text(Rois.Station.Id);
        var processText = tick.Text(Rois.Process.Id);
        var footerText = tick.Text(Rois.Footer.Id);

        // Last-good-wins: one bad OCR tick must not blank a field already captured.
        _acc.Station = RefineryParser.ParseStation(stationText) ?? _acc.Station;
        _acc.Process = RefineryParser.ParseProcess(processText) ?? _acc.Process;
        _acc.Cost = RefineryParser.ParseCost(footerText) ?? _acc.Cost;
        _acc.Time = RefineryParser.ParseTime(footerText) ?? _acc.Time;
    }

    /// <summary>Reads a PROCESSING or COMPLETED yield panel (NAME · QUALITY · YIELD · …), runs the
    /// checksum on a completed non-occluded read, and files the order as Processing or Ready.</summary>
    private async Task ObserveYieldPanelAsync(TickData tick, PanelState state, bool occluded, CancellationToken ct)
    {
        if (RoiFailed(tick, Rois.YieldList.Id))
            return;

        var list = tick.Ocr(Rois.YieldList.Id);
        if (list is null)
            return;

        var extract = RefineryParser.ExtractColumnarRows(list);

        // Completed-panel rows have no toggle column — they were refined by definition. QUALITY is
        // the first number, YIELD the second (PROCESSING then adds TO DO / DONE, which we ignore).
        // As in Accumulate, an unreadable QUALITY becomes the 0 sentinel (unknown, wildcards in
        // OrderMatcher.SameMaterial) rather than a fabricated literal zero that could re-split the order.
        var materials = extract.Rows
            .Select(r => new OrderMaterial(r.Name, Num(r, 0) ?? 0, 0, Num(r, 1) ?? 0, true))
            .ToList();
        if (materials.Count == 0)
            return;

        foreach (var r in extract.Rows)
            Log($"yield row {r.Name} [{string.Join(",", r.Numbers)}]");

        // Prefer the station captured during SETUP: the completed-panel header OCRs less reliably
        // (e.g. "STANTON" -> "•TANTON"), and an inconsistent station would split the record.
        var station = _acc.Station
            ?? RefineryParser.ParseStation(tick.Text(Rois.Station.Id))
            ?? "?";

        int? total = null;
        var completeness = Completeness.Unknown;
        var sum = materials.Sum(m => m.YieldCscu);

        if (state == PanelState.Completed && !occluded)
        {
            // The monolith read the total here and would have lost the whole tick had that OCR
            // thrown. An errored total reads as empty and parses to null, which would file an
            // otherwise-clean read as Partial and print a "scroll the list" nudge about a list that
            // was never truncated — a wrong reading persisted, so the observation waits instead.
            if (RoiFailed(tick, Rois.YieldTotal.Id))
                return;

            // Same frame as the rows above — the monolith's conditional OCR call is now a lookup.
            var totalText = tick.Text(Rois.YieldTotal.Id);
            total = RefineryParser.ParseYieldTotal(totalText);
            var clean = extract.DroppedTopEdge + extract.DroppedBottomEdge == 0
                && !materials.Any(m => m.YieldCscu == 0);
            completeness = clean && total is int t && Math.Abs(t - sum) <= ChecksumTolerance(materials.Count)
                ? Completeness.Complete
                : Completeness.Partial;
        }

        var orderState = state == PanelState.Processing ? OrderState.Processing : OrderState.Ready;
        var source = state == PanelState.Processing ? "PROCESSING" : "COMPLETED";

        var obs = new WorkOrder(
            Id: "", Key: "", Station: station, Process: _acc.Process ?? "?", Cost: _acc.Cost ?? "?",
            Eta: _acc.Time ?? "?", State: orderState, Completeness: completeness, Materials: materials,
            TotalYieldCscu: total, RowsSeen: materials.Count, FirstSeen: DateTime.Now, LastSeen: DateTime.Now,
            Sources: [source]);

        var result = _ledger.Observe(obs);
        _lastOrder = result.Merged;
        _observedThisCycle = true;

        if (completeness == Completeness.Partial && result.Changed)
            _sink.WriteLine($"refinery: order at {station} partial — {materials.Count} rows, {sum}/" +
                $"{(total?.ToString() ?? "?")} cSCU. Scroll the list to complete.");

        if (result.Changed && orderState == OrderState.Ready)
        {
            _emit(new TrackerRecord(DateTime.Now, Name, TriggerKind.Auto, RenderOrder(result.Merged)));
            await SaveDebugAsync(ct);
        }
    }

    private void MarkCollected()
    {
        // _observedThisCycle, not just _lastOrder: the machine reaches this from panel-state alone,
        // and _lastOrder survives across orders. A completed panel whose rows never parsed (an
        // errored yieldList for the whole cycle) would otherwise collect the PREVIOUS order —
        // filing an order the user never delivered while the one they did goes unrecorded.
        if (_lastOrder is null || !_observedThisCycle)
            return;

        // Keep Id/Key so OrderLedger.Observe targets this exact record via its Id fast path, rather
        // than re-matching fuzzily by station+materials. Blanking them (as this used to) meant that
        // with two open records sharing the same station+materials, fuzzy tie-break could collect the
        // wrong (older) one.
        var result = _ledger.Observe(_lastOrder with
        {
            State = OrderState.Collected, LastSeen = DateTime.Now,
        });
        _lastOrder = result.Merged;

        if (result.Changed)
            _emit(new TrackerRecord(DateTime.Now, Name, TriggerKind.Auto, RenderOrder(result.Merged)));
    }

    /// <summary>Hotkey escape hatch. Synchronous where the monolith's was not: everything it reads is
    /// already on the tick, so there is no OCR call left to await.</summary>
    private void OnManualTrigger(TickData tick)
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

        // Calibration aid: dump raw OCR of the regions this tracker depends on. A DETAILED
        // subscription still carries the plain text, so the setup list answers Text() too.
        var list = tick.Text(Rois.SetupList.Id);
        var footer = tick.Text(Rois.Footer.Id);
        _emit(new TrackerRecord(DateTime.Now, Name, TriggerKind.Manual,
            $"[raw list ROI]\r\n{list}\r\n[raw footer ROI]\r\n{footer}"));
    }

    private WorkOrder BuildSetupObservation() => new(
        Id: "", Key: "", Station: _acc.Station ?? "?", Process: _acc.Process ?? "?",
        Cost: _acc.Cost ?? "?", Eta: _acc.Time ?? "?", State: OrderState.Pending,
        Completeness: Completeness.Unknown, Materials: _acc.Materials, TotalYieldCscu: null,
        RowsSeen: _acc.Rows.Count, FirstSeen: DateTime.Now, LastSeen: DateTime.Now, Sources: ["SETUP"]);

    private static int? Num(ColumnarRow row, int index)
        => index < row.Numbers.Count ? row.Numbers[index] : null;

    /// <summary>
    /// Whether the engine flagged this ROI as failed on this tick, reporting it the first time and
    /// staying quiet until it reads again. An errored ROI is not a blank one: <c>Text()</c> answers
    /// empty either way, and every caller here has to treat "could not read" as "do nothing", never
    /// as an observation.
    /// </summary>
    private bool RoiFailed(TickData tick, string roiId)
    {
        var error = tick.Error(roiId);
        if (error is null)
        {
            _reportedFailures.Remove(roiId);
            return false;
        }

        if (_reportedFailures.Add(roiId))
            _sink.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{Name}] roi '{roiId}' unreadable: {error} " +
                "— skipping reads that depend on it until it recovers");

        return true;
    }

    private static bool IsModalVisible(string text)
        => text.Contains("CONFIRM", StringComparison.OrdinalIgnoreCase)
            || text.Contains("DELIVER", StringComparison.OrdinalIgnoreCase);

    private async Task SaveDebugAsync(CancellationToken ct)
    {
        if (_dumpFrame is null)
            return;

        // The engine writes the PNG and hands back where it put it; null means it has not scanned a
        // frame yet, in which case there is nothing to sit the rendered order beside.
        var pngPath = await _dumpFrame(Rois.YieldList.Rect, "refinery_completed");
        if (pngPath is not null && _lastOrder is not null)
            await File.WriteAllTextAsync(Path.ChangeExtension(pngPath, ".txt"), RenderOrder(_lastOrder), ct);
    }

    private static string RenderOrder(WorkOrder o)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Station: {o.Station}   [{o.State}, {o.Completeness}]");
        sb.AppendLine($"Process: {o.Process}   Cost: {o.Cost}   ETA: {o.Eta}");
        sb.AppendLine($"Materials ({o.Materials.Count}):");
        foreach (var m in o.Materials)
            sb.AppendLine($"  {m.Name,-20} q{m.Quality,-5} {m.YieldCscu / 100m,8:0.00} SCU  {(m.RefineOn ? "REFINE" : "skip")}");
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
