using System.Text.RegularExpressions;
using CaptureContracts;
using TrackerSdk;

// The class below shares its name with this namespace, which shadows the static Rois holder for
// any unqualified reference inside it (member lookup wins over enclosing-namespace lookup) — this
// alias is the least noisy way to reach Rois from there without spelling out `global::` each time.
using MissionRois = global::MissionPlugin.Rois;

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
public sealed partial class MissionPlugin : ITrackerPlugin
{
    [GeneratedRegex(@"Accepted\s*\(?\s*(\d+)\s*/\s*(\d+)\s*\)?", RegexOptions.IgnoreCase)]
    private static partial Regex AcceptedCounter();

    private string? _lastCounter;
    private int _lastAcceptedCount = -1;

    public string Name => "missions";

    public IReadOnlyList<RoiSubscription> Rois => MissionRois.All;

    // Explicit even though it matches ITrackerPlugin's own default: replaces the monolith's
    // silent-ignore of a failed "tab" region (an OCR error used to read as "counter gone",
    // resetting accepted-count state on a transient engine hiccup). Under AbortTick the host
    // never calls OnTickAsync at all while "tab" is failed, so the state-preserving branch in
    // OnTickAsync below only matters when a plugin is driven directly, as the tests do.
    public RoiErrorPolicy ErrorPolicy => RoiErrorPolicy.AbortTick;

    public async Task OnTickAsync(TickContext ctx, CancellationToken ct)
    {
        var tick = ctx.Tick;
        var services = ctx.Services;

        // Manual first, then the normal scan — the order the monolith used when a hotkey
        // press was queued, so a press during a counter change still captures the pane as it
        // was before the change is acted on.
        if (tick.Manual)
            await CapturePaneAsync(tick, services, TriggerKind.Manual, ct);

        // A failed or unsubscribed "tab" is not the same as a tab that read fine but shows no
        // counter (a different tab selected): the former says nothing about whether missions
        // are still accepted, so state is left untouched rather than reset.
        if (!tick.TryGetText(MissionRois.Tab.Id, out var tabText))
            return;

        services.LogVerbose($"[{Name}] tab: {tabText.ReplaceLineEndings(" ")}");

        var parsed = ParseAcceptedCounter(tabText);
        if (parsed is null)
        {
            if (_lastCounter is not null)
                services.LogVerbose($"[{Name}] counter no longer visible (was {_lastCounter})");
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
            services.Log($"[{DateTime.Now:HH:mm:ss.fff}] [{Name}] counter {_lastCounter ?? "none"} -> {counter}");

            if (isNewMission)
                await CapturePaneAsync(tick, services, TriggerKind.Auto, ct);

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

    private async Task CapturePaneAsync(TickData tick, IPluginServices services, TriggerKind trigger,
        CancellationToken ct)
    {
        tick.TryGetText(MissionRois.Pane.Id, out var paneText);

        // The tick's own timestamp, not DateTime.Now: the host buffers a few ticks per client,
        // so "when this was processed" can be a second or two after the frame it describes.
        services.Emit(new TrackerRecord(tick.Timestamp, Name, trigger, paneText));
        services.LogVerbose($"[{Name}] pane: {paneText.Length} chars");

        // The dump is a debugging aid, never the reason a capture counts: the record is already
        // emitted. Letting a failure out here would abort OnTickAsync before the counter state
        // advances, so the same accept would re-fire — and re-emit — on every following tick.
        try
        {
            // The engine writes the PNG and hands back where it put it; null means it has not
            // scanned a frame yet, in which case there is nothing to sit the text beside.
            var pngPath = await services.DumpFrameAsync(MissionRois.Pane.Rect, "mission_pane", ct);
            if (pngPath is not null)
                await File.WriteAllTextAsync(Path.ChangeExtension(pngPath, ".txt"), paneText, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            services.Log($"[{Name}] debug dump failed: {ex.Message}");
        }
    }
}
