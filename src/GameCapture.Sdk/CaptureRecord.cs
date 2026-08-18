namespace GameCapture.Sdk
{
    /// <summary>One captured event emitted by a tracker.</summary>
    public sealed record CaptureRecord(DateTime Timestamp, string Plugin, TriggerKind Trigger, string RawText);
}
