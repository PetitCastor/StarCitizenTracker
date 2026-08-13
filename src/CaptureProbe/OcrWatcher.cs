using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace CaptureProbe;

/// <summary>
/// Side-quest PoC: polls the live capture stream, OCRs each frame with the built-in
/// Windows OCR engine, and reports when a watched counter like "Accepted (1/10)"
/// changes value. Saves a PNG on every state change as evidence.
/// </summary>
public static partial class OcrWatcher
{
    [GeneratedRegex(@"Accepted\s*\(?\s*(\d+)\s*/\s*(\d+)\s*\)?", RegexOptions.IgnoreCase)]
    private static partial Regex AcceptedCounter();

    // ROI around the contract manager "ACCEPTED (n/m)" tab at 2560x1440 (measured from a live
    // capture, generous margins). Upscaled before OCR — Windows OCR misses this text at 1:1.
    private static readonly BitmapBounds TabRoi = new() { X = 1000, Y = 110, Width = 420, Height = 100 };
    private const uint OcrScale = 3;

    public static async Task RunAsync(MonitorCapture capture, string outputDir, bool verbose, CancellationToken ct)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? throw new InvalidOperationException("No OCR language pack available.");

        Console.WriteLine($"OCR language: {engine.RecognizerLanguage.DisplayName}");
        Console.WriteLine("Watching for 'Accepted (n/m)' — Ctrl+C to stop.");
        Console.WriteLine();

        string? lastValue = null;
        var scans = 0;
        var hits = 0;

        while (!ct.IsCancellationRequested)
        {
            var frame = capture.TakeLatestFrame();
            if (frame is null)
            {
                await Task.Delay(200, ct).ContinueWith(_ => { }); // idle screen: no new frame
                continue;
            }

            try
            {
                var sw = Stopwatch.StartNew();
                using var fullFrame = await ToSoftwareBitmapAsync(frame);
                using var roi = await CropAndScaleAsync(fullFrame, TabRoi, OcrScale);
                var result = await engine.RecognizeAsync(roi);
                sw.Stop();
                scans++;

                if (verbose)
                {
                    var text = result.Text.ReplaceLineEndings(" ");
                    Console.WriteLine($"[scan {scans}] ocr {sw.ElapsedMilliseconds} ms, {text.Length} chars: " +
                        (text.Length <= 200 ? text : text[..200] + "..."));
                }

                var match = AcceptedCounter().Match(result.Text);
                if (match.Success)
                {
                    hits++;
                    var value = $"{match.Groups[1].Value}/{match.Groups[2].Value}";
                    if (value != lastValue)
                    {
                        var path = await FrameSaver.SavePngAsync(frame, outputDir);
                        Console.WriteLine(
                            $"[{DateTime.Now:HH:mm:ss.fff}] CHANGE  Accepted ({lastValue ?? "none"}) -> ({value})  " +
                            $"ocr {sw.ElapsedMilliseconds} ms  evidence {Path.GetFileName(path)}");
                        lastValue = value;
                    }
                }
                else if (lastValue is not null)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] counter no longer visible (was {lastValue})");
                    lastValue = null;
                }

                if (scans % 20 == 0)
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] heartbeat: {scans} scans, {hits} with counter, ocr {sw.ElapsedMilliseconds} ms/frame");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] scan failed: {ex.Message}");
            }
            finally
            {
                frame.Dispose();
            }

            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
        }

        Console.WriteLine();
        Console.WriteLine($"Watcher done: {scans} scans, {hits} frames contained the counter.");
    }

    /// <summary>Crops <paramref name="bounds"/> out of the frame and upscales it for OCR.</summary>
    private static async Task<SoftwareBitmap> CropAndScaleAsync(SoftwareBitmap source, BitmapBounds bounds, uint scale)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
        encoder.SetSoftwareBitmap(source);
        await encoder.FlushAsync();

        var decoder = await BitmapDecoder.CreateAsync(stream);

        // BitmapTransform applies Bounds in the *scaled* coordinate space.
        var transform = new BitmapTransform
        {
            ScaledWidth = decoder.PixelWidth * scale,
            ScaledHeight = decoder.PixelHeight * scale,
            InterpolationMode = BitmapInterpolationMode.Cubic,
            Bounds = new BitmapBounds
            {
                X = bounds.X * scale,
                Y = bounds.Y * scale,
                Width = bounds.Width * scale,
                Height = bounds.Height * scale,
            },
        };

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Direct3D11CaptureFrame frame)
    {
        // Same GPU->CPU path as FrameSaver; OCR needs Bgra8 without premultiplied alpha surprises.
        using var premultiplied = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
            frame.Surface, BitmapAlphaMode.Premultiplied);
        return SoftwareBitmap.Convert(premultiplied, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
    }
}
