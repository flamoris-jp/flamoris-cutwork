using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging;

/// <summary>Freezes a polygon fragment from immutable Original into local straight BGRA.</summary>
public sealed class PatchSourceSampler : IPatchSourceSampler
{
    public FrozenPatchSource Freeze(OriginalAsset original, IReadOnlyList<DocumentPoint> sourcePolygon)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(sourcePolygon);
        if (sourcePolygon.Count < 3) throw new ArgumentException("A source polygon needs three points.", nameof(sourcePolygon));
        var polygon = sourcePolygon.ToArray();
        var bounds = PolygonMaskRasterizer.GetClippedBounds(polygon, original.Dimensions);
        if (bounds.IsEmpty) throw new ArgumentException("Source polygon does not cover Original.", nameof(sourcePolygon));
        var mask = PolygonMaskRasterizer.Rasterize(polygon, bounds);
        if (!mask.Any(value => value != 0)) throw new ArgumentException("Source polygon contains no pixels.", nameof(sourcePolygon));
        var pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width; x++)
        {
            var alphaMask = mask[y * bounds.Width + x];
            if (alphaMask == 0) continue;
            var source = original.PixelAt(bounds.X + x, bounds.Y + y);
            var target = pixels.AsSpan((y * bounds.Width + x) * 4, 4);
            source.CopyTo(target);
            target[3] = CompositeCache.Multiply(target[3], alphaMask);
        }
        return new FrozenPatchSource(bounds, pixels, polygon);
    }
}
