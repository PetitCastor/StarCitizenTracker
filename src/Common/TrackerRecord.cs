// TRANSITIONAL DUPLICATE of src/TrackingService/Trackers/TrackerRecord.cs. Plugins take this
// copy from ENGINE-SPLIT TASK-3; TASK-8 deletes the monolith's. Edit both together until then.
namespace Common
{
    /// <summary>One captured event emitted by a tracker.</summary>
    public sealed record TrackerRecord(DateTime Timestamp, string Tracker, TriggerKind Trigger, string RawText);
}
