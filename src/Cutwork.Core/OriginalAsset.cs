namespace Flamoris.Cutwork.Core;

public sealed class OriginalAsset
{
    private readonly byte[] _straightBgra32;

    public OriginalAsset(string sourceName, PixelSize dimensions, int stride, ReadOnlySpan<byte> pixels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (stride < checked(dimensions.Width * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }

        var requiredLength = checked(stride * dimensions.Height);
        if (pixels.Length != requiredLength)
        {
            throw new ArgumentException("Pixel data length does not match dimensions and stride.", nameof(pixels));
        }

        SourceName = sourceName;
        Dimensions = dimensions;
        Stride = stride;
        _straightBgra32 = pixels.ToArray();
    }

    public string SourceName { get; }

    public PixelSize Dimensions { get; }

    public int Stride { get; }

    public int ByteLength => _straightBgra32.Length;

    public byte[] CopyPixelBytes() => (byte[])_straightBgra32.Clone();

    public void CopyPixelBytesTo(Span<byte> destination)
    {
        if (destination.Length < _straightBgra32.Length)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        _straightBgra32.CopyTo(destination);
    }
}
