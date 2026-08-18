using GameCapture.Contracts;

namespace GameCapture.Sdk.Testing;

/// <summary>One fabricated OCR word: text plus its bounding box in upscaled-crop space — the same
/// space a real <see cref="OcrWordInfo"/> arrives in, so a parser that reads word position (column
/// splitting, left-to-right ordering) is exercised on realistic geometry rather than on zeros.</summary>
public readonly record struct OcrWordSpec(string Text, RectF CropRect);

/// <summary>One fabricated OCR line for <see cref="TickDataBuilder.Detailed"/>.</summary>
/// <remarks>
/// The implicit conversion from <c>string</c> covers the common case — a line whose text is all a
/// test needs — without forcing every caller to invent word geometry it will never read. A parser
/// that reads word boxes (columnar layouts, the refinery panel's name/value split) uses the
/// positional constructor with explicit <see cref="OcrWordSpec"/> entries instead.
/// </remarks>
public sealed record OcrLineSpec(string Text, IReadOnlyList<OcrWordSpec> Words)
{
    public OcrLineSpec(params OcrWordSpec[] words)
        : this(string.Join(' ', words.Select(w => w.Text)), words)
    {
    }

    public static implicit operator OcrLineSpec(string text) => new(text, []);
}
