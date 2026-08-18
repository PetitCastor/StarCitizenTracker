using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

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
