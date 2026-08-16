// TRANSITIONAL DUPLICATE of src/TrackingService/Core/FrameSaver.cs, byte-identical apart from
// the namespace. Nothing references this copy yet — the engine picks it up in ENGINE-SPLIT
// TASK-3, and TASK-8 deletes the monolith's copy in favour of it. Until then both are live and
// must be edited together. No parity test: this is disk I/O with no pure logic to assert on.
using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;

namespace CaptureEngine;

public static class FrameSaver
{
    /// <summary>
    /// Copies a captured GPU frame to the CPU and writes it as a timestamped PNG.
    /// Does not dispose the frame — the caller owns it.
    /// </summary>
    public static async Task<string> SavePngAsync(Direct3D11CaptureFrame frame, string outputDir)
    {
        // GPU -> CPU copy handled by the OS; avoids hand-rolled staging-texture interop.
        using var premultiplied = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
            frame.Surface, BitmapAlphaMode.Premultiplied);
        using var bitmap = SoftwareBitmap.Convert(premultiplied, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

        return await SavePngAsync(bitmap, outputDir, "capture");
    }

    public static async Task<string> SavePngAsync(SoftwareBitmap bitmap, string outputDir, string prefix)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

        using var fileStream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fileStream.AsRandomAccessStream());
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        return path;
    }
}
