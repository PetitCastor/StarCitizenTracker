using Windows.Graphics.Imaging;

namespace TrackingService.Trackers;

/// <summary>
/// A self-contained in-game event tracker: owns its trigger condition, screen regions,
/// and (in later phases) parser and output sink. Trackers are selected at launch
/// (--track name) and all run off the single shared capture stream.
/// </summary>
public interface ITracker
{
    string Name { get; }

    /// <summary>Called ~2 Hz with the latest frame. Tracker decides if its event fired.</summary>
    Task ScanAsync(SoftwareBitmap frame, CancellationToken ct);

    /// <summary>Hotkey fallback — capture now regardless of trigger condition.</summary>
    Task OnManualTriggerAsync(SoftwareBitmap frame, CancellationToken ct);
}
