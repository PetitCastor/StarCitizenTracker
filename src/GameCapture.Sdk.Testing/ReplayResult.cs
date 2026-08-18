namespace GameCapture.Sdk.Testing;

/// <summary>What one <see cref="ReplayHarness.RunAsync"/> run produced.</summary>
public sealed record ReplayResult(
    IReadOnlyList<CaptureRecord> Records,
    int ExitCode,
    StreamEndReason Reason);
