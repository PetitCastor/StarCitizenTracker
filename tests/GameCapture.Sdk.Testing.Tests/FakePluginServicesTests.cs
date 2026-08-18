using GameCapture.Sdk;
using GameCapture.Sdk.Testing;
using Xunit;

namespace GameCapture.Sdk.Testing.Tests;

public class FakePluginServicesTests
{
    [Fact]
    public void Emit_Records_WithoutPrinting()
    {
        var services = new FakePluginServices();
        var record = new CaptureRecord(DateTime.Now, "tracker", TriggerKind.Auto, "raw");

        services.Emit(record);

        Assert.Same(record, Assert.Single(services.Emitted));
        Assert.Empty(services.Logs);
    }

    [Fact]
    public void Log_And_LogVerbose_AreCaptured()
    {
        var services = new FakePluginServices();

        services.Log("line one");
        services.LogVerbose("verbose line");

        Assert.Equal(["line one"], services.Logs);
        Assert.Equal(["verbose line"], services.VerboseLogs);
    }

    [Fact]
    public async Task DumpFrameAsync_DefaultsToAFabricatedTempPath()
    {
        var services = new FakePluginServices();

        var path = await services.DumpFrameAsync(null, "prefix", CancellationToken.None);

        Assert.NotNull(path);
        Assert.StartsWith("prefix-", Path.GetFileName(path));
        Assert.Equal(Path.GetTempPath().TrimEnd('\\', '/'), Path.GetDirectoryName(path)!.TrimEnd('\\', '/'));
    }

    [Fact]
    public async Task DumpFrameAsync_HandlerOverridesTheDefault()
    {
        var services = new FakePluginServices
        {
            DumpFrameHandler = (_, _, _) => Task.FromResult<string?>("fixed-path.png"),
        };

        var path = await services.DumpFrameAsync(null, "prefix", CancellationToken.None);

        Assert.Equal("fixed-path.png", path);
    }

    [Fact]
    public async Task ReadRoiAsync_DefaultsToNull()
    {
        var services = new FakePluginServices();
        var roi = new RoiSubscription("panel", new GameCapture.Contracts.RoiRect(0, 0, 10, 10), 1.0, RoiKind.Text);

        var result = await services.ReadRoiAsync(roi, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void Engine_IsSettable()
    {
        var services = new FakePluginServices();
        var engine = new EngineInfo("v2", 3, 2560, 1440, ReplayMode: true, OcrLanguage: "fr",
            ConnectedClients: ["x"], ScanInterval: TimeSpan.FromSeconds(1));

        services.Engine = engine;

        Assert.Same(engine, services.Engine);
    }
}
