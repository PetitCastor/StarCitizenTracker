namespace GameCapture.Contracts;

/// <summary>
/// Maps ROIs declared in reference-resolution coordinates (2560x1440, the resolution all
/// regions are calibrated at) to actual frame pixels: X/width scale with frame width,
/// Y/height with frame height, so any 16:9 frame lands on the same UI spots. Other aspect
/// ratios scale each axis independently, which only holds if the game UI stretches the same
/// way — unverified, hence the warning in <see cref="DescribeFrame"/>.
/// </summary>
public static class RoiScaler
{
    public const int ReferenceWidth = 2560;
    public const int ReferenceHeight = 1440;

    /// <summary>Scales a reference-space ROI to frame space, clamped inside the frame.</summary>
    /// <remarks>
    /// There is deliberately no identity shortcut for the reference resolution: it would hand
    /// back a mis-configured out-of-bounds ROI unclamped, and the engine would take that
    /// straight to a bitmap crop. At 2560x1440 the scale factors are exactly 1.0 and every
    /// Math.Round below is exact, so the general path returns in-bounds ROIs unchanged anyway.
    /// </remarks>
    public static RoiRect ToFrame(RoiRect referenceRoi, int frameWidth, int frameHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameHeight);

        var sx = (double)frameWidth / ReferenceWidth;
        var sy = (double)frameHeight / ReferenceHeight;

        // Scale edges rather than width/height so adjacent ROIs stay adjacent after rounding.
        var x = (uint)Math.Clamp(Math.Round(referenceRoi.X * sx), 0, Math.Max(0, frameWidth - 1));
        var y = (uint)Math.Clamp(Math.Round(referenceRoi.Y * sy), 0, Math.Max(0, frameHeight - 1));
        var right = (uint)Math.Clamp(Math.Round((referenceRoi.X + referenceRoi.Width) * sx), x + 1, frameWidth);
        var bottom = (uint)Math.Clamp(Math.Round((referenceRoi.Y + referenceRoi.Height) * sy), y + 1, frameHeight);

        return new RoiRect(x, y, right - x, bottom - y);
    }

    /// <summary>Scales a reference-space X coordinate (e.g. a pixel sample column) to frame space.</summary>
    public static int ToFrameX(int referenceX, int frameWidth)
        => (int)Math.Round((double)referenceX * frameWidth / ReferenceWidth);

    /// <summary>Scales a reference-space Y coordinate to frame space.</summary>
    public static int ToFrameY(int referenceY, int frameHeight)
        => (int)Math.Round((double)referenceY * frameHeight / ReferenceHeight);

    /// <summary>One-line description of the capture size and how ROIs will be mapped to it.</summary>
    public static string DescribeFrame(int frameWidth, int frameHeight)
    {
        if (frameWidth == ReferenceWidth && frameHeight == ReferenceHeight)
            return $"capture {frameWidth}x{frameHeight} (reference resolution, ROIs used 1:1)";

        var sx = (double)frameWidth / ReferenceWidth;
        var sy = (double)frameHeight / ReferenceHeight;
        var text = $"capture {frameWidth}x{frameHeight}, ROIs scaled x{sx:0.###} / y{sy:0.###}";

        if (frameWidth * (long)ReferenceHeight != frameHeight * (long)ReferenceWidth)
            text += " — WARNING: aspect ratio differs from the 16:9 reference; ROI positions are unverified";
        return text;
    }
}
