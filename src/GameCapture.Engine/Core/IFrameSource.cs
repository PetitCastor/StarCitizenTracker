using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

/// <summary>
/// Where the scan loop gets frames from. The abstraction exists so live capture and replay are
/// the same code path end to end: a replay run exercises the real registry, the real per-tick
/// assembly and the real gRPC streams, not a parallel implementation that can drift from them.
/// </summary>
internal interface IFrameSource : IDisposable
{
    /// <summary>
    /// Next frame to scan, or null. Live: the latest WGC frame downloaded to CPU, null while the
    /// screen is idle and no new frame has arrived. Replay: the next PNG, null when the corpus is
    /// exhausted. The two nulls mean different things to the loop — hence <see cref="IsReplay"/>.
    /// Caller owns the returned bitmap.
    /// </summary>
    Task<SoftwareBitmap?> NextFrameAsync(CancellationToken ct);

    /// <summary>
    /// True when frames come from a finite PNG corpus. Drives every determinism-vs-liveness
    /// decision in the loop: end-of-stream handling, backpressure mode, and whether the loop
    /// waits for a subscriber before burning frames.
    /// </summary>
    bool IsReplay { get; }
}
