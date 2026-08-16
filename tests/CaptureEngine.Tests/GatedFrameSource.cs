using Windows.Graphics.Imaging;

namespace CaptureEngine.Tests;

/// <summary>
/// A replay corpus the test hands out one frame at a time, cycling as often as needed. The plain
/// <see cref="ReplayFrameSource"/> runs flat out by design, which makes "do X, then observe the
/// next tick" a race the test would lose most of the time: three frames fit inside the client's
/// outbound channel, so the whole corpus can be produced before the first tick is even read.
/// Gating the source turns that ordering into a fact instead of a hope.
/// </summary>
/// <remarks>
/// Reports <c>IsReplay</c> so the loop keeps replay's blocking backpressure — dropping a tick
/// would break the very ordering this source exists to guarantee. It never returns null, so a run
/// ends by cancellation rather than by corpus exhaustion.
/// </remarks>
internal sealed class GatedFrameSource : IFrameSource
{
    private readonly string[] _frames;
    private readonly SemaphoreSlim _gate = new(0);
    private int _next;

    public GatedFrameSource(string directory)
        => _frames = Directory.GetFiles(directory, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToArray();

    public bool IsReplay => true;

    /// <summary>Lets the scan loop take <paramref name="count"/> more frames.</summary>
    public void Release(int count = 1) => _gate.Release(count);

    public async Task<SoftwareBitmap?> NextFrameAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        var path = _frames[_next++ % _frames.Length];

        using var fileStream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(fileStream.AsRandomAccessStream());
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
    }

    public void Dispose() => _gate.Dispose();
}
