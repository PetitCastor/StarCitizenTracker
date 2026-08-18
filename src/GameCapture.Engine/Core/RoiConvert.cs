using GameCapture.Contracts;
using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

/// <summary>BitmapBounds is WinRT and must not leak past the engine; RoiRect is the portable twin.</summary>
internal static class RoiConvert
{
    public static BitmapBounds ToBounds(this RoiRect r)
        => new() { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };

    public static RoiRect ToRoiRect(this BitmapBounds b) => new(b.X, b.Y, b.Width, b.Height);
}
