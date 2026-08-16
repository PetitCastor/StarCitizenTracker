using System.Text.RegularExpressions;
using CaptureContracts;
using Common;
using TrackerSdk;

namespace MissionPlugin;

/// <summary>
/// The regions this plugin subscribes, in reference space. Static for the life of the process:
/// per-tick atomicity means every decision is made from one tick, so the set a tick can answer
/// must be complete before the tick arrives — there is no mid-tick round-trip to add a ROI.
/// </summary>
public static class Rois
{
    // Regions measured from live 2560x1440 captures (2026-08-13), kept in reference
    // coordinates; the engine maps them to the actual frame size at scan time.
    public static readonly RoiSubscription Tab =
        new("tab", new RoiRect(1000, 110, 420, 100), 3.0, RoiKind.Text);

    // Scale is clamped to the OCR engine max dimension by the engine's pipeline.
    public static readonly RoiSubscription Pane =
        new("pane", new RoiRect(860, 180, 1560, 1010), 2.0, RoiKind.Text);

    // A field, not an expression-bodied property: the set never changes, and `=> [Tab, Pane]`
    // would build a fresh array on every read.
    public static readonly IReadOnlyList<RoiSubscription> All = [Tab, Pane];
}

/// <summary>
/// Tracks mission acceptance: watches the contract manager's "ACCEPTED (n/m)" tab counter;
/// when it increments (or on manual hotkey), reads the mission-details pane and emits the
/// raw text. Parsing to structured fields is a later phase.
/// </summary>
/// <remarks>
/// Port of the monolith's MissionTracker. The OCR itself now happens engine-side, so the
/// per-call stopwatches are gone — the verbose lines report what was read, not how long it
/// took. Everything else (parsing, state, log strings, emit format) is unchanged.
/// </remarks>
public sealed partial class MissionLogic
{
    [GeneratedRegex(@"Accepted\s*\(?\s*(\d+)\s*/\s*(\d+)\s*\)?", RegexOptions.IgnoreCase)]
    private static partial Regex AcceptedCounter();

    /// <summary>Client name on the Track stream and the <see cref="TrackerRecord.Tracker"/> tag.</summary>
    public const string Name = "missions";

    private readonly Action<TrackerRecord> _emit;
    private readonly ConsoleSink _sink;
    private readonly bool _verbose;

    // Non-null: ask the engine to dump the pane PNG per capture and write the text beside it.
    // A func rather than the client itself so the logic stays testable without a pipe.
    private readonly Func<RoiRect?, string, Task<string?>>? _dumpFrame;

    private string? _lastCounter;
    private int _lastAcceptedCount = -1;

    public MissionLogic(Action<TrackerRecord> emit, ConsoleSink sink, bool verbose,
        Func<RoiRect?, string, Task<string?>>? dumpFrame)
    {
        _emit = emit;
        _sink = sink;
        _verbose = verbose;
        _dumpFrame = dumpFrame;
    }

    public async Task OnTickAsync(TickData tick, CancellationToken ct = default)
    {
        // Manual first, then the normal scan — the order the monolith used when a hotkey
        // press was queued, so a press during a counter change still captures the pane as it
        // was before the change is acted on.
        if (tick.Manual)
            await CapturePaneAsync(tick, TriggerKind.Manual, ct);

        var tabText = tick.Text("tab");

        if (_verbose)
            _sink.WriteLine($"[{Name}] tab: {tabText.ReplaceLineEndings(" ")}");

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
                await CapturePaneAsync(tick, TriggerKind.Auto, ct);

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

    private async Task CapturePaneAsync(TickData tick, TriggerKind trigger, CancellationToken ct)
    {
        var paneText = tick.Text("pane");

        // The tick's own timestamp, not DateTime.Now: the engine buffers a few ticks per client,
        // so "when this was processed" can be a second or two after the frame it describes.
        _emit(new TrackerRecord(tick.Timestamp, Name, trigger, paneText));

        if (_verbose)
            _sink.WriteLine($"[{Name}] pane: {paneText.Length} chars");

        if (_dumpFrame is null)
            return;

        // The dump is a debugging aid, never the reason a capture counts: the record is already
        // emitted. Letting a failure out here would abort OnTickAsync before the counter state
        // advances, so the same accept would re-fire — and re-emit — on every following tick.
        try
        {
            // The engine writes the PNG and hands back where it put it; null means it has not
            // scanned a frame yet, in which case there is nothing to sit the text beside.
            var pngPath = await _dumpFrame(Rois.Pane.Rect, "mission_pane");
            if (pngPath is not null)
                await File.WriteAllTextAsync(Path.ChangeExtension(pngPath, ".txt"), paneText, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _sink.WriteLine($"[{Name}] debug dump failed: {ex.Message}");
        }
    }
}
