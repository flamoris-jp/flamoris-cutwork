using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.App.Rendering;

public sealed class WriteableBitmapSurface
{
    public WriteableBitmap? Bitmap { get; private set; }

    public long Generation { get; private set; }
    public DocumentRect LastUpdatedRegion { get; private set; }
    public long TransferredPixelCount { get; private set; }

    public void Clear() => Bitmap = null;

    public void Initialize(PixelSize dimensions)
    {
        Bitmap = new WriteableBitmap(dimensions.Width, dimensions.Height, 96, 96, PixelFormats.Pbgra32, null);
        Generation++;
    }

    public void Apply(CompositeUpdate update)
    {
        var bitmap = Bitmap ?? throw new InvalidOperationException("Surface is not initialized.");
        var region = update.Region;
        if (region.IsEmpty) return;
        if (!new DocumentRect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight).Contains(region) ||
            update.PremultipliedBgra.Length != checked(region.Width * region.Height * 4))
            throw new ArgumentException("Invalid presentation region.", nameof(update));
        // Packed ROI source with an explicit destination. No full-frame copy or bitmap replacement.
        bitmap.WritePixels(new Int32Rect(0, 0, region.Width, region.Height),
            update.PremultipliedBgra, region.Width * 4, region.X, region.Y);
        LastUpdatedRegion = region;
        TransferredPixelCount += (long)region.Width * region.Height;
    }

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
        LastUpdatedRegion = DocumentRect.FromSize(dimensions);
        TransferredPixelCount += (long)dimensions.Width * dimensions.Height;
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
        CompositeCache.Multiply(color, alpha);
}
