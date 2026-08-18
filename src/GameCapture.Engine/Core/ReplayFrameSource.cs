using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

/// <summary>
/// Offline mode: feeds saved full-frame PNGs to the scan loop in filename order (= chronological,
/// FrameSaver names are timestamped), decoding each exactly as the monolith's ReplayRunner does.
/// Deterministic verification without the game running.
/// </summary>
internal sealed class ReplayFrameSource : IFrameSource
{
    private readonly string[] _frames;
    private int _next;

    /// <param name="directory">Directory of *.png frame dumps; enumerated once at construction.</param>
    public ReplayFrameSource(string directory) => _frames = EnumerateCorpus(directory);

    public bool IsReplay => true;

    /// <summary>Number of PNGs in the corpus; the scan loop will produce exactly this many ticks.</summary>
    public int FrameCount => _frames.Length;

    /// <summary>File name of the frame handed out by the last <see cref="NextFrameAsync"/>, for verbose logs.</summary>
    public string? LastFrameName { get; private set; }

    public async Task<SoftwareBitmap?> NextFrameAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_next >= _frames.Length)
            return null; // corpus exhausted

        var path = _frames[_next++];
        LastFrameName = Path.GetFileName(path);

        return await DecodeFrameAsync(path);
    }

    public void Dispose()
    {
        // Nothing retained between calls: each frame is decoded on demand and owned by the caller.
    }

    /// <summary>A corpus directory's frames in replay order.</summary>
    /// <remarks>
    /// Ordinal, not the current culture: culture-aware sorting reorders timestamped names on some
    /// locales, and replay order is the whole point of the corpus. Shared with the test sources so
    /// they cannot enumerate a corpus differently than production does while claiming to replay it.
    /// </remarks>
    internal static string[] EnumerateCorpus(string directory)
        => Directory.GetFiles(directory, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToArray();

    /// <summary>Decodes one corpus frame, exactly as the monolith's ReplayRunner does.</summary>
    /// <remarks>Shared for the same reason as <see cref="EnumerateCorpus"/>: a test source with
    /// its own pixel format or alpha mode would exercise a different pixel path than the one it
    /// is meant to be validating.</remarks>
    internal static async Task<SoftwareBitmap> DecodeFrameAsync(string path)
    {
        using var fileStream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(fileStream.AsRandomAccessStream());
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
    }
}
