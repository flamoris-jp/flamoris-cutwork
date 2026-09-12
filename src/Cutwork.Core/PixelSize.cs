namespace Flamoris.Cutwork.Core;

public readonly record struct PixelSize
{
    public PixelSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}
