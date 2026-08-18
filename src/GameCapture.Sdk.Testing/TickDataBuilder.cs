using GameCapture.Contracts;
using GameCapture.Contracts.Proto;
using Google.Protobuf;

namespace GameCapture.Sdk.Testing;

/// <summary>
/// Builds a <see cref="TickData"/> the way the engine would have sent it — a <see cref="TickResult"/>
/// through the SDK's own <c>TickData.From</c> — rather than faking the SDK type. The mapping (kind
/// checks, frame_rect, effective_scale) is part of what plugin logic depends on, so a hand-made
/// shortcut around it would let a tick that could never arrive on the wire pass a test.
/// </summary>
/// <remarks>
/// Consolidates the two private <c>TickFactory</c> types <c>MissionPlugin.Tests</c> and
/// <c>RefineryPlugin.Tests</c> each grew independently. Reaches <c>TickData.From</c> through
/// <c>InternalsVisibleTo("GameCapture.Sdk.Testing")</c> — the one grant that survives the split into
/// separate repos (TASK-22/23), because this package, unlike a plugin's own test project, ships
/// alongside the SDK.
/// </remarks>
public sealed class TickDataBuilder
{
    // Interlocked because xUnit runs test classes in parallel and a static counter is shared
    // across every builder instance, the same reason the two factories this replaces used one.
    private static long _seq;

    private readonly RoiRect _frameRect;
    private readonly List<RoiResult> _results = [];
    private readonly HashSet<string> _ids = [];
    private bool _manual;
    private ulong? _frameSeq;
    private DateTimeOffset? _at;

    public TickDataBuilder(int frameWidth = EngineDefaults.ReferenceWidth,
        int frameHeight = EngineDefaults.ReferenceHeight)
    {
        _frameRect = new RoiRect(0, 0, (uint)frameWidth, (uint)frameHeight);
    }

    /// <summary>Adds a TEXT result. Reference space == frame space (scale 1), matching a fixture
    /// sized to the builder's own frame dimensions.</summary>
    public TickDataBuilder Text(RoiId id, string text)
    {
        Add(id, new RoiResult
        {
            RoiId = id.Value,
            Kind = RoiResultKind.Text,
            FrameRect = _frameRect.ToProto(),
            EffectiveScale = 1.0,
            Text = text,
        });
        return this;
    }

    /// <summary>Adds a DETAILED result: OCR text with per-word geometry, for parsers that read
    /// column position rather than plain text.</summary>
    public TickDataBuilder Detailed(RoiId id, params OcrLineSpec[] lines)
    {
        var ocrLines = lines
            .Select(l => new OcrLineInfo(l.Text, l.Words.Select(w => new OcrWordInfo(w.Text, w.CropRect)).ToList()))
            .ToList();

        var ocr = new OcrRegionResult(
            string.Join(Environment.NewLine, ocrLines.Select(l => l.Text)),
            ocrLines,
            EffectiveScale: 1.0,
            _frameRect.X, _frameRect.Y, _frameRect.Width, _frameRect.Height);

        var result = new RoiResult
        {
            RoiId = id.Value,
            Kind = RoiResultKind.Detailed,
            FrameRect = _frameRect.ToProto(),
        };
        result.FillFrom(ocr);
        Add(id, result);
        return this;
    }

    /// <summary>Adds a PIXELS result: a solid-colour BGRA strip at 1:1 with the frame, the shape a
    /// colour probe (a toggle, a status pill) reads.</summary>
    public TickDataBuilder Pixels(RoiId id, byte b, byte g, byte r, int w, int h)
    {
        var stride = w * 4;
        var bgra = new byte[stride * h];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = 255;
        }

        Add(id, new RoiResult
        {
            RoiId = id.Value,
            Kind = RoiResultKind.Pixels,
            FrameRect = new RoiRect(0, 0, (uint)w, (uint)h).ToProto(),
            PixelsBgra = ByteString.CopyFrom(bgra),
            PixelsStride = (uint)stride,
            PixelsWidth = (uint)w,
            PixelsHeight = (uint)h,
        });
        return this;
    }

    /// <summary>Adds a failed result — mirrors the bare <see cref="RoiResult"/> ScanLoop's catch
    /// builds engine-side: <see cref="RoiResult.Error"/> set, no payload at all. A hand-built
    /// "flagged but populated" result would let a test pass on data a plugin will never see.</summary>
    public TickDataBuilder Errored(RoiId id, string message)
    {
        Add(id, new RoiResult
        {
            RoiId = id.Value,
            Error = true,
            ErrorMessage = message,
        });
        return this;
    }

    /// <summary>Marks the tick as the one on which the engine's hotkey fired.</summary>
    public TickDataBuilder Manual()
    {
        _manual = true;
        return this;
    }

    /// <summary>Overrides the auto-incrementing frame sequence — for tests asserting gap/repeat
    /// detection, which needs specific, not merely distinct, values.</summary>
    public TickDataBuilder FrameSeq(ulong seq)
    {
        _frameSeq = seq;
        return this;
    }

    /// <summary>Overrides the scan timestamp. Defaults to now; pass an older instant to tell the
    /// frame's own time apart from the time the tick is processed.</summary>
    public TickDataBuilder At(DateTimeOffset at)
    {
        _at = at;
        return this;
    }

    public TickData Build()
    {
        var proto = new TickResult
        {
            TimestampMs = (_at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
            FrameSeq = _frameSeq ?? (ulong)Interlocked.Increment(ref _seq),
            FrameWidth = _frameRect.Width,
            FrameHeight = _frameRect.Height,
            Manual = _manual,
        };
        proto.Results.AddRange(_results);
        return TickData.From(proto);
    }

    /// <summary>
    /// Indexer-shaped, not Add: a builder that added the same id twice would otherwise crash the
    /// whole tick here instead of reproducing the double-subscription case <c>TickData.From</c>
    /// itself has to tolerate (the engine echoes ids back unvalidated).
    /// </summary>
    private void Add(RoiId id, RoiResult result)
    {
        if (!_ids.Add(id.Value))
            _results.RemoveAll(r => r.RoiId == id.Value);

        _results.Add(result);
    }
}
