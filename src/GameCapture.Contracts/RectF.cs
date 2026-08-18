namespace GameCapture.Contracts;

/// <summary>Plain rectangle in upscaled-crop pixel space (no WinRT types so parsers stay testable).</summary>
public readonly record struct RectF(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}
