using Windows.Graphics.Imaging;

namespace GameCapture.Engine.Tests;

/// <summary>
/// A replay corpus the test hands out one frame at a time, cycling as often as needed. The plain
/// <see cref="ReplayFrameSource"/> runs flat out by design, which makes "do X, then observe the
/// next tick" a race the test would lose most of the time: three frames fit inside the client's
/// outbound channel, so the whole corpus can be produced before the first tick is even read.
/// Gating the source turns that ordering into a fact instead of a hope.
/// </summary>
/// <remarks>
/// Defaults to reporting <c>IsReplay</c> so the loop keeps replay's blocking backpressure —
/// dropping a tick would break the very ordering this source exists to guarantee. Pass
/// <c>isReplay: false</c> to drive the live path instead, where the loop drops rather than blocks
/// and pushes to every registered client whether or not it has subscribed; that is the only way to
/// test behaviour a plugin will only ever meet in a live session. It never returns null, so a run
/// ends by cancellation rather than by corpus exhaustion.
/// </remarks>
internal sealed class GatedFrameSource : IFrameSource
{
    private readonly string[] _frames;
    private readonly SemaphoreSlim _gate = new(0);
    private int _next;

    // Enumerated and decoded through ReplayFrameSource: the gate and the cycling are the only
    // things this source is meant to do differently, and a private copy of the corpus handling
    // would let these tests validate a pixel path production does not use.
    public GatedFrameSource(string directory, bool isReplay = true)
    {
        _frames = ReplayFrameSource.EnumerateCorpus(directory);
        IsReplay = isReplay;
    }

    public bool IsReplay { get; }

    /// <summary>Lets the scan loop take <paramref name="count"/> more frames.</summary>
    public void Release(int count = 1) => _gate.Release(count);

    public async Task<SoftwareBitmap?> NextFrameAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        return await ReplayFrameSource.DecodeFrameAsync(_frames[_next++ % _frames.Length]);
    }

    public void Dispose() => _gate.Dispose();
}
