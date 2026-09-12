namespace Flamoris.Cutwork.Core;

/// <summary>Integer document pixels, half-open bounds. Default is empty.</summary>
public readonly record struct DocumentRect
{
    public DocumentRect(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        _ = checked(x + width);
        _ = checked(y + height);
        X = x; Y = y; Width = width; Height = height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width == 0 || Height == 0;
    public static DocumentRect FromSize(PixelSize size) => new(0, 0, size.Width, size.Height);
    public bool Contains(DocumentRect other) => !other.IsEmpty && other.X >= X && other.Y >= Y
        && other.Right <= Right && other.Bottom <= Bottom;
    public DocumentRect Intersect(DocumentRect other)
    {
        var x = Math.Max(X, other.X); var y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(Right, other.Right) - x),
            Math.Max(0, Math.Min(Bottom, other.Bottom) - y));
    }
    public DocumentRect Union(DocumentRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        var x = Math.Min(X, other.X); var y = Math.Min(Y, other.Y);
        return new(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }
}
