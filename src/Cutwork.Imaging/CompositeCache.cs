using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging;

public sealed record CompositeUpdate(DocumentRect Region, byte[] PremultipliedBgra, long DocumentRevision);

/// <summary>One document projection. All pixel semantics live here, outside WPF.</summary>
public sealed class CompositeCache : IDisposable
{
    private readonly CutworkDocument _document;
    private readonly byte[] _pixels;
    private readonly byte[] _holes;
    private DocumentRect _dirty;
    private DocumentRect _holeDirty;
    private bool _disposed;

    public CompositeCache(CutworkDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _pixels = new byte[checked(document.Dimensions.Width * document.Dimensions.Height * 4)];
        _holes = new byte[checked(document.Dimensions.Width * document.Dimensions.Height)];
        _dirty = _holeDirty = DocumentRect.FromSize(document.Dimensions);
        document.Changed += DocumentChanged;
    }

    public DocumentRect PendingRegion => _dirty;
    public long CompositedPixelCount { get; private set; }
    public long HolePixelCount { get; private set; }
    public long Revision { get; private set; } = -1;

    private void DocumentChanged(object? sender, DocumentChange change)
    {
        _dirty = _dirty.Union(change.DirtyRegion);
        _holeDirty = _holeDirty.Union(change.HoleRegion);
    }

    /// <summary>Recompute and return only pending document pixels; null means no pixel transfer.</summary>
    public CompositeUpdate? RenderPending()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var region = _dirty;
        if (region.IsEmpty)
        {
            Revision = _document.Revision;
            return null;
        }

        var width = _document.Dimensions.Width;
        var parts = _document.Layers.OfType<PartLayer>().ToArray();
        for (var y = _holeDirty.Y; y < _holeDirty.Bottom; y++)
        for (var x = _holeDirty.X; x < _holeDirty.Right; x++)
        {
            byte union = 0;
            foreach (var part in parts) union = Math.Max(union, part.MaskAt(x, y));
            _holes[y * width + x] = union;
            HolePixelCount++;
        }

        var output = new byte[checked(region.Width * region.Height * 4)];
        for (var y = region.Y; y < region.Bottom; y++)
        for (var x = region.X; x < region.Right; x++)
        {
            var destination = _pixels.AsSpan((y * width + x) * 4, 4);
            destination.Clear();
            for (var index = _document.Layers.Count - 1; index >= 0; index--)
            {
                var layer = _document.Layers[index];
                if (!layer.Visible || x < layer.Bounds.X || y < layer.Bounds.Y ||
                    x >= layer.Bounds.Right || y >= layer.Bounds.Bottom) continue;
                switch (layer)
                {
                    case BaseLayer:
                        SourceOver(destination, _document.Original.PixelAt(x, y), (byte)(255 - _holes[y * width + x]));
                        break;
                    case PartLayer part:
                        SourceOver(destination, _document.Original.PixelAt(x, y), part.MaskAt(x, y));
                        break;
                    case PatchLayer patch:
                        var transformed = patch.SampleAt(x, y);
                        if (!transformed.IsEmpty) SourceOver(destination, transformed, 255);
                        break;
                    case RasterLayer raster:
                        SourceOver(destination, raster.PixelAt(x, y), 255);
                        break;
                }
            }
            destination.CopyTo(output.AsSpan(((y - region.Y) * region.Width + x - region.X) * 4, 4));
            CompositedPixelCount++;
        }
        _dirty = _holeDirty = default;
        Revision = _document.Revision;
        return new CompositeUpdate(region, output, Revision);
    }

    public byte[] CopyPixels()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (byte[])_pixels.Clone();
    }

    public byte[] CopyHoleMask()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (byte[])_holes.Clone();
    }

    private static void SourceOver(Span<byte> destination, ReadOnlySpan<byte> source, byte mask)
    {
        var alpha = Multiply(source[3], mask);
        var inverse = (byte)(255 - alpha);
        for (var channel = 0; channel < 3; channel++)
            destination[channel] = (byte)(Multiply(source[channel], alpha) + Multiply(destination[channel], inverse));
        destination[3] = (byte)(alpha + Multiply(destination[3], inverse));
    }

    public static byte Multiply(byte value, byte alpha) => (byte)((value * alpha + 127) / 255);

    public void Dispose()
    {
        if (_disposed) return;
        _document.Changed -= DocumentChanged;
        _disposed = true;
    }
}
