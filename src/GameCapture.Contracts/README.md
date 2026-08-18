# GameCapture.Contracts

The wire contract between the GameCapture engine and its plugins: the generated
Protocol Buffers code for `CaptureEngineService` (`Track` / `ReadRoi` / `DumpFrame` / `GetStatus`),
plus the pure types both sides need in order to agree on what a region and a reading are.

Most plugin authors reference this only for `RoiRect` and the OCR/pixel result types — the rest of
the surface is reached through `GameCapture.Sdk`, which is what a plugin should be written against. A
plugin that names a generated proto type is a plugin that has to be recompiled when the wire
changes.

## Install

```powershell
dotnet add package GameCapture.Contracts
```

Usually transitively, via `GameCapture.Sdk`.

> Not on nuget.org yet (TASK-16/17). Until then, reference
> `src/GameCapture.Contracts/GameCapture.Contracts.csproj` from a clone.

## What is in it

| Type | Role |
| --- | --- |
| `RoiRect` | A region in reference space (2560x1440). What a plugin declares. |
| `RoiScaler` | Maps reference space **to** frame space, one way only. The engine applies it; a plugin never maps a reported rect back. |
| `OcrRegionResult`, `OcrLineInfo`, `OcrWordInfo` | An OCR reading, with per-word geometry for column-shaped UI. |
| `PixelPatchSampler` | CPU-side BGRA sampling over a small pixel region — colour probes. |
| `WireLimits` | The budgets a payload must stay inside (max pixel bytes, default/clamped OCR scale). |
| `ProtocolVersion` | The integer version the handshake negotiates, distinct from any package version. |
| `PipeContract` | The default named-pipe name. |

Plain `net10.0` and deliberately WinRT-free: a plugin referencing this must never gain a dependency
on the capture stack.

## Documentation

[`docs/PROTOCOL.md`](https://github.com/PetitCastor/StarCitizenTracker/blob/master/docs/PROTOCOL.md)
— transport, handshake, version policy, coordinate spaces. Changes to `protos/capture.proto` are
lint- and breaking-change-checked by `buf` in CI.
