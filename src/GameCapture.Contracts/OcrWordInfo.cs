namespace GameCapture.Contracts;

/// <summary>One recognized word with its bounding box in upscaled-crop space.</summary>
public sealed record OcrWordInfo(string Text, RectF CropRect);
