namespace GameCapture.Contracts;

/// <summary>One recognized line. WinRT gives boxes per word only; a line box is the union of its words.</summary>
public sealed record OcrLineInfo(string Text, IReadOnlyList<OcrWordInfo> Words);
