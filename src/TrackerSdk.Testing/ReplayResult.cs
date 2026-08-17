namespace TrackerSdk.Testing;

/// <summary>What one <see cref="ReplayHarness.RunAsync"/> run produced.</summary>
public sealed record ReplayResult(
    IReadOnlyList<TrackerRecord> Records,
    int ExitCode,
    StreamEndReason Reason);
