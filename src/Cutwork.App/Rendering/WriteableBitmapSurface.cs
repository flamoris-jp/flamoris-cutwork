using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.App.Rendering;

public sealed class WriteableBitmapSurface
{
    public WriteableBitmap? Bitmap { get; private set; }

    public long Generation { get; private set; }

    public void PresentOriginal(OriginalAsset original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var dimensions = original.Dimensions;
        var bitmap = new WriteableBitmap(
            dimensions.Width,
            dimensions.Height,
            96,
            96,
            PixelFormats.Pbgra32,
            null);
        var displayPixels = original.CopyPixelBytes();
        PremultiplyInPlace(displayPixels);
        bitmap.WritePixels(
            new Int32Rect(0, 0, dimensions.Width, dimensions.Height),
            displayPixels,
            original.Stride,
            0);
        Bitmap = bitmap;
        Generation++;
    }

    private static void PremultiplyInPlace(Span<byte> bgra32)
    {
        for (var offset = 0; offset < bgra32.Length; offset += 4)
        {
            var alpha = bgra32[offset + 3];
            bgra32[offset] = Premultiply(bgra32[offset], alpha);
            bgra32[offset + 1] = Premultiply(bgra32[offset + 1], alpha);
            bgra32[offset + 2] = Premultiply(bgra32[offset + 2], alpha);
        }
    }

    private static byte Premultiply(byte color, byte alpha) =>
        (byte)((color * alpha + 127) / 255);
}
