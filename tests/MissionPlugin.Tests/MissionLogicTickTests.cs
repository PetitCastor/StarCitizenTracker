using CaptureContracts;
using Common;
using MissionPlugin;
using Xunit;
using static MissionPlugin.Tests.TickFactory;

namespace MissionPlugin.Tests;

/// <summary>
/// The state machine driven by whole ticks, which is the shape the plugin actually runs in: what
/// used to be "a frame plus two OCR calls" is now one object, and the decision to emit still has
/// to come from the counter's movement rather than from its value.
/// </summary>
public class MissionLogicTickTests
{
    [Fact]
    public async Task CounterIncrement_EmitsOneAutoRecordCarryingThePaneText()
    {
        using var sink = new ConsoleSink();
        var records = new List<TrackerRecord>();
        var logic = new MissionLogic(records.Add, sink, verbose: false, dumpFrame: null);

        // First sighting: the counter is merely visible, which is the contract manager opening.
        await logic.OnTickAsync(Tick("ACCEPTED (2/5)", "stale pane"));
        Assert.Empty(records);

        await logic.OnTickAsync(Tick("ACCEPTED (3/5)", "MISSION: Deliver crates"));

        var record = Assert.Single(records);
        Assert.Equal(TriggerKind.Auto, record.Trigger);
        Assert.Equal("missions", record.Tracker);
        Assert.Equal("MISSION: Deliver crates", record.RawText);
    }

    [Fact]
    public async Task CounterDecrement_EmitsNothing()
    {
        using var sink = new ConsoleSink();
        var records = new List<TrackerRecord>();
        var logic = new MissionLogic(records.Add, sink, verbose: false, dumpFrame: null);

        await logic.OnTickAsync(Tick("ACCEPTED (3/5)", "pane"));
        await logic.OnTickAsync(Tick("ACCEPTED (2/5)", "pane"));

        // A completion or an abandon moves the counter too; only an increment is an accept.
        Assert.Empty(records);
    }

    [Fact]
    public async Task ManualTick_EmitsAManualRecordEvenWithNoCounterOnScreen()
    {
        using var sink = new ConsoleSink();
        var records = new List<TrackerRecord>();
        var logic = new MissionLogic(records.Add, sink, verbose: false, dumpFrame: null);

        await logic.OnTickAsync(Tick("no counter here", "MISSION: Escort", manual: true));

        var record = Assert.Single(records);
        Assert.Equal(TriggerKind.Manual, record.Trigger);
        Assert.Equal("MISSION: Escort", record.RawText);
    }

    /// <summary>
    /// A hotkey press on the same tick that the counter moves. Both captures happen, manual first
    /// — the order the monolith used when a press was queued, and the one that lets a user grab the
    /// pane as it was before the accept is acted on.
    /// </summary>
    [Fact]
    public async Task ManualOnTheSameTickAsAnIncrement_EmitsManualThenAuto()
    {
        using var sink = new ConsoleSink();
        var records = new List<TrackerRecord>();
        var logic = new MissionLogic(records.Add, sink, verbose: false, dumpFrame: null);

        await logic.OnTickAsync(Tick("ACCEPTED (2/5)", "pane"));
        await logic.OnTickAsync(Tick("ACCEPTED (3/5)", "pane", manual: true));

        Assert.Equal([TriggerKind.Manual, TriggerKind.Auto], records.Select(r => r.Trigger));
    }

    /// <summary>
    /// The debug path: the engine writes the PNG and reports where, and the plugin drops the OCR
    /// text beside it under the same name. The pairing is the whole point — a corpus of panes with
    /// no record of what was read from them cannot be used to check a parser.
    /// </summary>
    [Fact]
    public async Task WithDebugDumps_WritesTheOcrTextBesideTheEnginesPng()
    {
        using var sink = new ConsoleSink();
        var dir = Directory.CreateTempSubdirectory("mission-plugin-tests");
        try
        {
            var pngPath = Path.Combine(dir.FullName, "mission_pane_20260816_015900.png");
            RoiRect? requestedRoi = null;
            string? requestedPrefix = null;

            var logic = new MissionLogic(_ => { }, sink, verbose: false, dumpFrame: (roi, prefix) =>
            {
                requestedRoi = roi;
                requestedPrefix = prefix;
                return Task.FromResult<string?>(pngPath);
            });

            await logic.OnTickAsync(Tick("no counter", "MISSION: Salvage", manual: true));

            Assert.Equal(Rois.Pane.Rect, requestedRoi);
            Assert.Equal("mission_pane", requestedPrefix);
            Assert.Equal("MISSION: Salvage",
                await File.ReadAllTextAsync(Path.ChangeExtension(pngPath, ".txt")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A record dates the frame, not the moment the plugin got round to it. The engine buffers a
    /// few ticks per client and this handler awaits an RPC, so processing time can trail the
    /// capture by seconds — and a mission's timestamp is the thing a later phase will join on.
    /// </summary>
    [Fact]
    public async Task Record_IsStampedWithTheTicksTimeNotTheProcessingTime()
    {
        using var sink = new ConsoleSink();
        var records = new List<TrackerRecord>();
        var logic = new MissionLogic(records.Add, sink, verbose: false, dumpFrame: null);

        var tick = Tick("no counter", "MISSION: Escort", manual: true,
            at: DateTimeOffset.UtcNow.AddMinutes(-5));

        await logic.OnTickAsync(tick);

        Assert.Equal(tick.Timestamp, Assert.Single(records).Timestamp);
    }

    /// <summary>
    /// A debug dump that throws must not take the capture down with it. The record is already
    /// emitted when the dump runs, and letting the failure out would abort the tick before the
    /// counter state advances — so the same accept would re-fire, and re-emit, on every tick
    /// that followed.
    /// </summary>
    [Fact]
    public async Task WhenTheDebugDumpFails_TheAcceptStillCountsOnceAndDoesNotRefire()
    {
        using var sink = new ConsoleSink();
        var records = new List<TrackerRecord>();
        var logic = new MissionLogic(records.Add, sink, verbose: false,
            dumpFrame: (_, _) => throw new IOException("no space left on device"));

        await logic.OnTickAsync(Tick("ACCEPTED (2/5)", "pane"));
        await logic.OnTickAsync(Tick("ACCEPTED (3/5)", "MISSION: Deliver crates"));

        // The counter has not moved again, so the next tick must be silent.
        await logic.OnTickAsync(Tick("ACCEPTED (3/5)", "MISSION: Deliver crates"));

        var record = Assert.Single(records);
        Assert.Equal(TriggerKind.Auto, record.Trigger);
        Assert.Equal("MISSION: Deliver crates", record.RawText);
    }

    /// <summary>
    /// No frame scanned yet on the engine side: DumpFrame answers null, and there is then no file
    /// to sit the text beside. The capture itself still counts — the text is in the record.
    /// </summary>
    [Fact]
    public async Task WithDebugDumps_WhenTheEngineHasNoFrame_StillEmitsAndWritesNothing()
    {
        using var sink = new ConsoleSink();
        var records = new List<TrackerRecord>();
        var logic = new MissionLogic(records.Add, sink, verbose: false,
            dumpFrame: (_, _) => Task.FromResult<string?>(null));

        await logic.OnTickAsync(Tick("no counter", "MISSION: Bounty", manual: true));

        Assert.Equal("MISSION: Bounty", Assert.Single(records).RawText);
    }
}
