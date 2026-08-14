namespace TrackingService.Trackers
{
    /// <summary>One captured event emitted by a tracker.</summary>
    public sealed record TrackerRecord(DateTime Timestamp, string Tracker, TriggerKind Trigger, string RawText);
}
