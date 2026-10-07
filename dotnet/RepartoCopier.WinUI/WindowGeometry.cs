namespace RepartoCopier.WinUI;

internal readonly record struct PixelSize(int Width, int Height);

internal static class WindowGeometry
{
    internal static int Pixels(double dips, uint dpi) => checked((int)Math.Ceiling(dips * Math.Max(96u, dpi) / 96d));

    internal static PixelSize Client(int width, int height, uint dpi, int workWidth, int workHeight,
        int frameWidth, int frameHeight) => new(
        Math.Clamp(Pixels(width, dpi), 1, Math.Max(1, workWidth - frameWidth)),
        Math.Clamp(Pixels(height, dpi), 1, Math.Max(1, workHeight - frameHeight)));

    internal static PixelSize Minimum(uint dpi, int workWidth, int workHeight, int frameWidth, int frameHeight) => new(
        Math.Min(workWidth, Pixels(540, dpi) + frameWidth),
        Math.Min(workHeight, Pixels(320, dpi) + frameHeight));
}
