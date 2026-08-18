# GameCapture.Sdk

The client library for writing a GameCapture plugin. A plugin is a console process that
declares which screen regions it cares about and reacts to one frame's readings at a time; the
capture engine — a separate process — owns the screen, the OCR, and the hotkey, and only OCR results
and small pixel buffers cross between them.

`IGameCapturePlugin` is the contract (a name, a set of ROIs, `OnTickAsync`) and `GameCapturePluginHost` runs
it: connecting, subscribing, reconnecting, Ctrl+C, and the end-of-run summary. Plain `net10.0` — no
Windows dependency, so a plugin and its tests build anywhere.

## Install

```powershell
dotnet add package GameCapture.Sdk
```

> Not on nuget.org yet (TASK-16/17). Until then, reference
> `src/GameCapture.Sdk/GameCapture.Sdk.csproj` from a clone.

## Minimal plugin

`CounterPlugin.cs`:

```csharp
using GameCapture.Contracts;
using GameCapture.Sdk;

public sealed class CounterPlugin : IGameCapturePlugin
{
    private static readonly RoiSubscription Counter =
        new("counter", new RoiRect(1000, 110, 420, 100), 3.0, RoiKind.Text);

    // A field, not => [Counter]: the host re-reads Rois per connect and per errored tick,
    // and an expression body would allocate a fresh array each time.
    private static readonly IReadOnlyList<RoiSubscription> All = [Counter];

    public string Name => "counter";
    public IReadOnlyList<RoiSubscription> Rois => All;

    public Task OnTickAsync(TickContext ctx, CancellationToken ct)
    {
        // TryGetText, not Text: a failed region and a blank panel both read "".
        if (ctx.Tick.TryGetText(Counter.Id, out var text) && text.Length > 0)
            ctx.Services.Emit(new CaptureRecord(ctx.Tick.Timestamp, Name, TriggerKind.Auto, text));

        return Task.CompletedTask;
    }
}
```

`Program.cs` — the whole entry point:

```csharp
using GameCapture.Sdk;

return await GameCapturePluginHost.RunAsync(new CounterPlugin(), args);
```

ROIs are declared in reference space (2560x1440); the engine scales them to the actual capture
resolution, so the constants stay valid on any monitor.

## Documentation

Full tutorial: [`docs/PLUGIN-AUTHORING.md`](https://github.com/PetitCastor/StarCitizenTracker/blob/master/docs/PLUGIN-AUTHORING.md).
Unit and replay-parity testing: the companion package `GameCapture.Sdk.Testing`.
