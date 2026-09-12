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
        bitmap.WritePixels(
            new Int32Rect(0, 0, dimensions.Width, dimensions.Height),
            original.CopyPixelBytes(),
            original.Stride,
            0);
        Bitmap = bitmap;
        Generation++;
    }
}
