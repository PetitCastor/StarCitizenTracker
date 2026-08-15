using CaptureContracts;
using CaptureContracts.Proto;
using Google.Protobuf;
using Xunit;

namespace CaptureContracts.Tests;

/// <summary>
/// The mapping is the whole boundary contract: if a round-trip loses geometry, every plugin
/// parser silently reads the wrong pixels. These assert structural equality AND that
/// frame-space projection survives the crossing.
/// </summary>
public class ProtoMappingTests
{
    private static OcrRegionResult BuildSource() => new(
        Text: "PRESSURIZED ICE\n3.03 SCU",
        Lines:
        [
            new OcrLineInfo("PRESSURIZED ICE",
            [
                new OcrWordInfo("PRESSURIZED", new RectF(10.5, 20.25, 120.75, 30.5)),
                new OcrWordInfo("ICE", new RectF(140.0, 20.25, 40.5, 30.5)),
            ]),
            new OcrLineInfo("3.03 SCU",
            [
                new OcrWordInfo("3.03", new RectF(10.5, 60.75, 50.25, 30.5)),
            ]),
        ],
        EffectiveScale: 2.75,
        RoiX: 620, RoiY: 640, RoiWidth: 440, RoiHeight: 340);

    private static RoiResult ToWire(OcrRegionResult source, RoiRect frameRect)
    {
        var wire = new RoiResult { RoiId = "setup_materials", FrameRect = frameRect.ToProto() };
        wire.FillFrom(source);
        return wire;
    }

    [Fact]
    public void RoundTrip_PreservesTextLinesWordsAndScale()
    {
        var source = BuildSource();
        // frame_rect is arbitrary here — the mapping must carry it, not re-derive it.
        var frameRect = new RoiRect(465, 480, 330, 255);

        var result = ToWire(source, frameRect).ToOcrRegionResult();

        Assert.Equal(source.Text, result.Text);
        Assert.Equal(source.EffectiveScale, result.EffectiveScale);
        Assert.Equal(source.Lines.Count, result.Lines.Count);

        for (var i = 0; i < source.Lines.Count; i++)
        {
            Assert.Equal(source.Lines[i].Text, result.Lines[i].Text);
            Assert.Equal(source.Lines[i].Words.Count, result.Lines[i].Words.Count);

            for (var w = 0; w < source.Lines[i].Words.Count; w++)
            {
                Assert.Equal(source.Lines[i].Words[w].Text, result.Lines[i].Words[w].Text);
                Assert.Equal(source.Lines[i].Words[w].CropRect, result.Lines[i].Words[w].CropRect);
            }
        }
    }

    [Fact]
    public void RoundTrip_RoiRectComesFromFrameRect()
    {
        var frameRect = new RoiRect(465, 480, 330, 255);

        var result = ToWire(BuildSource(), frameRect).ToOcrRegionResult();

        Assert.Equal(frameRect.X, result.RoiX);
        Assert.Equal(frameRect.Y, result.RoiY);
        Assert.Equal(frameRect.Width, result.RoiWidth);
        Assert.Equal(frameRect.Height, result.RoiHeight);
    }

    [Fact]
    public void RoundTrip_ToFramePoint_UnchangedWhenFrameRectMatchesSourceRoi()
    {
        // With the engine handing back the same rect the OCR was taken from, a plugin's
        // frame-space projection is bit-identical to the monolith's in-process one.
        var source = BuildSource();
        var frameRect = new RoiRect(source.RoiX, source.RoiY, source.RoiWidth, source.RoiHeight);

        var result = ToWire(source, frameRect).ToOcrRegionResult();

        Assert.Equal(source.ToFramePoint(0, 123.5), result.ToFramePoint(0, 123.5));
        Assert.Equal(source.ToFramePoint(88.25, 0), result.ToFramePoint(88.25, 0));
    }

    [Fact]
    public void RectRoundTrip_IsFieldWiseIdentity()
    {
        var rect = new RoiRect(2100, 1300, 460, 140);

        Assert.Equal(rect, rect.ToProto().ToRoiRect());
    }

    [Fact]
    public void ToPixelSampler_TakesOriginFromFrameRect()
    {
        // 2x2 BGRA, all (1,2,3), placed at frame origin (100, 200).
        var bytes = new byte[2 * 2 * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = 1;
            bytes[i + 1] = 2;
            bytes[i + 2] = 3;
            bytes[i + 3] = 255;
        }

        var wire = new RoiResult
        {
            RoiId = "refine_toggles",
            FrameRect = new RoiRect(100, 200, 2, 2).ToProto(),
            PixelsBgra = ByteString.CopyFrom(bytes),
            PixelsStride = 8,
            PixelsWidth = 2,
            PixelsHeight = 2,
        };

        var sampler = wire.ToPixelSampler();

        Assert.Equal(100, sampler.FrameX);
        Assert.Equal(200, sampler.FrameY);
        Assert.Equal(2, sampler.Width);
        Assert.Equal(2, sampler.Height);
        Assert.Equal(((byte)1, (byte)2, (byte)3), sampler.AveragePatch(101, 201, radius: 0));
    }
}
