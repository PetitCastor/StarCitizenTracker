using CaptureContracts;
using MissionPlugin;
using TrackerSdk;
using TrackerSdk.Testing;
using Xunit;

namespace MissionPlugin.Tests;

/// <summary>
/// The state machine driven by whole ticks, which is the shape the plugin actually runs in: what
/// used to be "a frame plus two OCR calls" is now one object, and the decision to emit still has
/// to come from the counter's movement rather than from its value.
/// </summary>
public class MissionLogicTickTests
{
    private static TickData Tick(string tabText, string paneText = "", bool manual = false,
        DateTimeOffset? at = null)
    {
        var builder = new TickDataBuilder().Text(Rois.Tab.Id, tabText).Text(Rois.Pane.Id, paneText);
        if (manual)
            builder.Manual();
        if (at is { } instant)
            builder.At(instant);
        return builder.Build();
    }

    private static TickContext Ctx(TickData tick, IPluginServices services) =>
        TickContext.ForTesting(tick, services);

    [Fact]
    public async Task CounterIncrement_EmitsOneAutoRecordCarryingThePaneText()
    {
        var services = new FakePluginServices();
        var plugin = new MissionPlugin();

        // First sighting: the counter is merely visible, which is the contract manager opening.
        await plugin.OnTickAsync(Ctx(Tick("ACCEPTED (2/5)", "stale pane"), services), default);
        Assert.Empty(services.Emitted);

        await plugin.OnTickAsync(Ctx(Tick("ACCEPTED (3/5)", "MISSION: Deliver crates"), services), default);

        var record = Assert.Single(services.Emitted);
        Assert.Equal(TriggerKind.Auto, record.Trigger);
        Assert.Equal("missions", record.Tracker);
        Assert.Equal("MISSION: Deliver crates", record.RawText);
    }

    [Fact]
    public async Task CounterDecrement_EmitsNothing()
    {
        var services = new FakePluginServices();
        var plugin = new MissionPlugin();

        await plugin.OnTickAsync(Ctx(Tick("ACCEPTED (3/5)", "pane"), services), default);
        await plugin.OnTickAsync(Ctx(Tick("ACCEPTED (2/5)", "pane"), services), default);

        // A completion or an abandon moves the counter too; only an increment is an accept.
        Assert.Empty(services.Emitted);
    }

    [Fact]
    public async Task ManualTick_EmitsAManualRecordEvenWithNoCounterOnScreen()
    {
        var services = new FakePluginServices();
        var plugin = new MissionPlugin();

        await plugin.OnTickAsync(Ctx(Tick("no counter here", "MISSION: Escort", manual: true), services), default);

        var record = Assert.Single(services.Emitted);
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
        var services = new FakePluginServices();
        var plugin = new MissionPlugin();

        await plugin.OnTickAsync(Ctx(Tick("ACCEPTED (2/5)", "pane"), services), default);
        await plugin.OnTickAsync(Ctx(Tick("ACCEPTED (3/5)", "pane", manual: true), services), default);

        Assert.Equal([TriggerKind.Manual, TriggerKind.Auto], services.Emitted.Select(r => r.Trigger));
    }

    /// <summary>
    /// The debug path: the engine writes the PNG and reports where, and the plugin drops the OCR
    /// text beside it under the same name. The pairing is the whole point — a corpus of panes with
    /// no record of what was read from them cannot be used to check a parser.
    /// </summary>
    [Fact]
    public async Task WithDebugDumps_WritesTheOcrTextBesideTheEnginesPng()
    {
        var dir = Directory.CreateTempSubdirectory("mission-plugin-tests");
        try
        {
            var pngPath = Path.Combine(dir.FullName, "mission_pane_20260816_015900.png");
            RoiRect? requestedRoi = null;
            string? requestedPrefix = null;

            var services = new FakePluginServices
            {
                DumpFrameHandler = (roi, prefix, _) =>
                {
                    requestedRoi = roi;
                    requestedPrefix = prefix;
                    return Task.FromResult<string?>(pngPath);
                },
            };
            var plugin = new MissionPlugin();

            await plugin.OnTickAsync(Ctx(Tick("no counter", "MISSION: Salvage", manual: true), services), default);

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
        var services = new FakePluginServices();
        var plugin = new MissionPlugin();

        var tick = Tick("no counter", "MISSION: Escort", manual: true,
            at: DateTimeOffset.UtcNow.AddMinutes(-5));

        await plugin.OnTickAsync(Ctx(tick, services), default);

        Assert.Equal(tick.Timestamp, Assert.Single(services.Emitted).Timestamp);
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
        var services = new FakePluginServices
        {
            DumpFrameHandler = (_, _, _) => throw new IOException("no space left on device"),
        };
        var plugin = new MissionPlugin();

        await plugin.OnTickAsync(Ctx(Tick("ACCEPTED (2/5)", "pane"), services), default);
        await plugin.OnTickAsync(Ctx(Tick("ACCEPTED (3/5)", "MISSION: Deliver crates"), services), default);

        // The counter has not moved again, so the next tick must be silent.
        await plugin.OnTickAsync(Ctx(Tick("ACCEPTED (3/5)", "MISSION: Deliver crates"), services), default);

        var record = Assert.Single(services.Emitted);
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
        var services = new FakePluginServices
        {
            DumpFrameHandler = (_, _, _) => Task.FromResult<string?>(null),
        };
        var plugin = new MissionPlugin();

        await plugin.OnTickAsync(Ctx(Tick("no counter", "MISSION: Bounty", manual: true), services), default);

        Assert.Equal("MISSION: Bounty", Assert.Single(services.Emitted).RawText);
    }

    /// <summary>
    /// Under <see cref="RoiErrorPolicy.AbortTick"/> the host never calls <c>OnTickAsync</c> at all
    /// while a subscribed region is failed — this exercises the plugin's own defence in depth, since
    /// a direct unit test bypasses that host-side filtering. The old monolith read a failed "tab" the
    /// same as a tab that read fine but showed no counter, which reset the accepted-count state on a
    /// transient OCR error; the fix is treating "unreadable" and "read, no counter" as different.
    /// </summary>
    [Fact]
    public async Task ErroredTabTick_EmitsNothingAndDoesNotResetCounterState()
    {
        var services = new FakePluginServices();
        var plugin = new MissionPlugin();

        await plugin.OnTickAsync(Ctx(Tick("ACCEPTED (2/5)", "pane"), services), default);

        var erroredTick = new TickDataBuilder()
            .Errored(Rois.Tab.Id, "ocr failed")
            .Text(Rois.Pane.Id, "pane")
            .Build();
        await plugin.OnTickAsync(Ctx(erroredTick, services), default);
        Assert.Empty(services.Emitted);

        // State was preserved through the errored tick: 2 -> 3 still reads as an increment.
        await plugin.OnTickAsync(Ctx(Tick("ACCEPTED (3/5)", "MISSION: Deliver crates"), services), default);

        var record = Assert.Single(services.Emitted);
        Assert.Equal(TriggerKind.Auto, record.Trigger);
        Assert.Equal("MISSION: Deliver crates", record.RawText);
    }
}
