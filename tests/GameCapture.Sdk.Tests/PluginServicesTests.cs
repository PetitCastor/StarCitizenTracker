using GameCapture.Contracts;
using Xunit;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// The services the host lends a plugin. The emit format is asserted here because it is the one
/// piece of plugin-visible output that was previously a closure inside each Program.cs — identical
/// in both, and therefore the thing most likely to be "improved" in one of them by accident.
/// </summary>
public class PluginServicesTests
{
    private static PluginServices New(out List<CaptureRecord> records, out RecordingOutput output,
        bool verbose = false,
        Func<RoiRect?, string, CancellationToken, Task<string?>>? dumpFrame = null,
        Func<RoiSubscription, CancellationToken, Task<OcrRegionResult?>>? readRoi = null)
    {
        records = [];
        output = new RecordingOutput();
        return new PluginServices(records, output, verbose, dumpFrame, readRoi);
    }

    [Fact]
    public void Emit_KeepsTheRecordForTheSummary()
    {
        var services = New(out var records, out _);

        services.Emit(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "text"));

        Assert.Single(records);
    }

    /// <summary>
    /// One output call per capture, not five: each WriteLine erases and redraws the status bar, so
    /// five separate calls would flicker it five times per tracker event.
    /// </summary>
    [Fact]
    public void Emit_WritesTheWholeBlockAsASingleCall()
    {
        var services = New(out _, out var output);

        services.Emit(new CaptureRecord(new DateTime(2026, 8, 16, 14, 3, 9, 500), "refinery",
            TriggerKind.Manual, "PRESSURIZED ICE"));

        var block = Assert.Single(output.Lines);
        Assert.Contains("===== refinery capture (Manual) at 14:03:09.500 =====", block);
        Assert.Contains("PRESSURIZED ICE", block);
    }

    [Fact]
    public void LogVerbose_IsSilentUnlessVerbose()
    {
        var services = New(out _, out var quiet);
        services.LogVerbose("noise");
        Assert.Empty(quiet.Lines);

        var loud = New(out _, out var output, verbose: true);
        loud.LogVerbose("noise");
        Assert.Equal(["noise"], output.Lines);
    }

    [Fact]
    public void Log_AlwaysWrites()
    {
        var services = New(out _, out var output);

        services.Log("something happened");

        Assert.Equal(["something happened"], output.Lines);
    }

    /// <summary>
    /// Debug dumps switched off is the ordinary case, and it answers null rather than throwing: a
    /// plugin calls this unconditionally and branches on the path it gets back.
    /// </summary>
    [Fact]
    public async Task DumpFrameAsync_WithoutADumper_AnswersNull()
    {
        var services = New(out _, out _);

        Assert.Null(await services.DumpFrameAsync(null, "prefix", CancellationToken.None));
    }

    [Fact]
    public async Task DumpFrameAsync_PassesTheRoiAndPrefixThrough()
    {
        RoiRect? seenRoi = null;
        string? seenPrefix = null;

        var services = New(out _, out _, dumpFrame: (roi, prefix, _) =>
        {
            seenRoi = roi;
            seenPrefix = prefix;
            return Task.FromResult<string?>(@"C:\engine\out\shot.png");
        });

        var rect = new RoiRect(1, 2, 3, 4);
        var path = await services.DumpFrameAsync(rect, "refinery_completed", CancellationToken.None);

        Assert.Equal(rect, seenRoi);
        Assert.Equal("refinery_completed", seenPrefix);
        Assert.Equal(@"C:\engine\out\shot.png", path);
    }

    [Fact]
    public async Task ReadRoiAsync_PassesTheSubscriptionThrough()
    {
        RoiSubscription? seen = null;
        var answer = new OcrRegionResult("SETUP", [], 3.0, 900, 265, 250, 55);

        var services = New(out _, out _, readRoi: (roi, _) =>
        {
            seen = roi;
            return Task.FromResult<OcrRegionResult?>(answer);
        });

        var subscription = new RoiSubscription("panel", new RoiRect(900, 265, 250, 55), 3.0, RoiKind.Text);
        var result = await services.ReadRoiAsync(subscription, CancellationToken.None);

        Assert.Equal(subscription, seen);
        Assert.Same(answer, result);
    }

    /// <summary>
    /// A plugin driven by a host that has no client to read through — a test harness, most of them —
    /// gets a null rather than a crash, the same shape <see cref="PluginServices.DumpFrameAsync"/>
    /// answers with when dumps are off.
    /// </summary>
    [Fact]
    public async Task ReadRoiAsync_WithoutAReader_AnswersNull()
    {
        var services = New(out _, out _);

        Assert.Null(await services.ReadRoiAsync(
            new RoiSubscription("panel", new RoiRect(1, 2, 3, 4), 1.0, RoiKind.Text),
            CancellationToken.None));
    }

    /// <summary>
    /// One instance for the whole run, so a plugin may hold the reference it is handed — and reading
    /// <c>Engine</c> off it after a reconnect must show the engine it is now talking to, not the one
    /// it was talking to when the reference was taken.
    /// </summary>
    [Fact]
    public void Engine_ReflectsTheLatestConnect()
    {
        var services = New(out _, out _);
        Assert.Equal("", services.Engine.EngineVersion);

        services.Engine = new EngineInfo("1.2.3", 1, 2560, 1440, false, "en-US", ["refinery"],
            TimeSpan.FromMilliseconds(250));

        Assert.Equal("1.2.3", services.Engine.EngineVersion);
        Assert.Equal(1u, services.Engine.NegotiatedProtocol);
    }
}
