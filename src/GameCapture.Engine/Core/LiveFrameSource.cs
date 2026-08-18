using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

/// <summary>
/// Live capture: hands the scan loop the most recent WGC frame, downloaded to a CPU bitmap.
/// Takes ownership of the <see cref="MonitorCapture"/> so the capture session has exactly one
/// owner — the loop's frame source — instead of being disposed from two places at shutdown.
/// </summary>
internal sealed class LiveFrameSource : IFrameSource
{
    private readonly MonitorCapture _capture;

    public LiveFrameSource(MonitorCapture capture) => _capture = capture;

    public bool IsReplay => false;

    public async Task<SoftwareBitmap?> NextFrameAsync(CancellationToken ct)
    {
        var frame = _capture.TakeLatestFrame();
        if (frame is null)
            return null; // idle screen: the loop owns the retry delay

        // The GPU frame is only a staging buffer for the CPU copy; releasing it immediately
        // returns the slot to the frame pool, exactly as the monolith's scan loop did.
        try
        {
            return await OcrPipeline.ToSoftwareBitmapAsync(frame);
        }
        finally
        {
            frame.Dispose();
        }
    }

    public void Dispose() => _capture.Dispose();
}
