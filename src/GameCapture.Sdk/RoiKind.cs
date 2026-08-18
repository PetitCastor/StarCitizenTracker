namespace GameCapture.Sdk;

/// <summary>What the engine should do with a subscribed region. Mirrors the wire's RoiMode, but
/// plugins never touch generated types — the enum is restated so a plugin can be written against
/// the SDK alone.</summary>
public enum RoiKind
{
    /// <summary>Plain OCR text.</summary>
    Text,

    /// <summary>OCR with per-word geometry (needed by parsers that read columns by position).</summary>
    Detailed,

    /// <summary>Raw BGRA bytes at 1:1, no OCR — colour probes such as the refinery toggle strip.</summary>
    Pixels,
}
