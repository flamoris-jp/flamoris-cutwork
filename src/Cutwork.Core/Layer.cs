namespace Flamoris.Cutwork.Core;

public enum LayerKind { Base, Part, Patch, Repair }

public abstract class Layer
{
    private readonly DocumentRect _bounds;

    private protected Layer(LayerKind kind, DocumentRect bounds, string name, Guid id)
    {
        if (bounds.IsEmpty) throw new ArgumentException("Empty layer bounds.", nameof(bounds));
        if (id == Guid.Empty) throw new ArgumentException("Empty layer identity.", nameof(id));
        Id = id; Kind = kind; _bounds = bounds;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
    public Guid Id { get; }
    public LayerKind Kind { get; }
    public virtual DocumentRect Bounds => _bounds;
    public string Name { get; internal set; }
    public string? SemanticName { get; internal set; }
    public bool Visible { get; internal set; } = true;
    public long Revision { get; internal set; }
    internal Guid? OwnerDocumentId { get; set; }
    internal virtual long RetainedBytes => 128L + Name.Length * 2L + (SemanticName?.Length ?? 0) * 2L;
}

public sealed class BaseLayer : Layer
{
    internal BaseLayer(Guid id, PixelSize dimensions)
        : base(LayerKind.Base, DocumentRect.FromSize(dimensions), "", id) { }
}

public sealed class PartLayer : Layer
{
    private byte[] _mask;
    private DocumentRect _maskBounds;
    public PartLayer(DocumentRect bounds, ReadOnlySpan<byte> mask, string name = "")
        : this(Guid.NewGuid(), bounds, mask, name, -1)
    {
    }
    internal PartLayer(Guid id, DocumentRect bounds, ReadOnlySpan<byte> mask, string name = "",
        int partOrder = -1)
        : base(LayerKind.Part, bounds, name, id)
    {
        if (partOrder < -1) throw new ArgumentOutOfRangeException(nameof(partOrder));
        if (mask.Length != checked(bounds.Width * bounds.Height))
            throw new ArgumentException("Mask size mismatch.", nameof(mask));
        _mask = mask.ToArray();
        _maskBounds = bounds;
        PartOrder = partOrder;
    }
    /// <summary>Semantic creation order. It is independent from compositor stack position.</summary>
    public int PartOrder { get; internal set; }
    public override DocumentRect Bounds => _maskBounds;
    public byte MaskAt(int x, int y) => x < Bounds.X || y < Bounds.Y || x >= Bounds.Right || y >= Bounds.Bottom
        ? (byte)0 : _mask[(y - Bounds.Y) * Bounds.Width + x - Bounds.X];
    public byte[] CopyMask(DocumentRect region) => PixelRegion.Copy(_mask, Bounds, region, 1);
    internal byte[] CopyMaskWithTransparentOutside(DocumentRect region)
    {
        var result = new byte[checked(region.Width * region.Height)];
        var overlap = Bounds.Intersect(region);
        if (overlap.IsEmpty) return result;
        for (var row = 0; row < overlap.Height; row++)
            _mask.AsSpan((overlap.Y - Bounds.Y + row) * Bounds.Width + overlap.X - Bounds.X,
                overlap.Width).CopyTo(result.AsSpan(
                (overlap.Y - region.Y + row) * region.Width + overlap.X - region.X, overlap.Width));
        return result;
    }
    internal void WriteMask(DocumentRect region, byte[] bytes) => PixelRegion.Write(_mask, Bounds, region, bytes, 1);
    internal void ResizeMask(DocumentRect bounds)
    {
        if (bounds.IsEmpty) throw new EditException(EditError.InvalidPatch);
        if (bounds == Bounds) return;
        var resized = new byte[checked(bounds.Width * bounds.Height)];
        var overlap = Bounds.Intersect(bounds);
        if (!overlap.IsEmpty)
        {
            for (var row = 0; row < overlap.Height; row++)
                _mask.AsSpan((overlap.Y - Bounds.Y + row) * Bounds.Width + overlap.X - Bounds.X,
                    overlap.Width).CopyTo(resized.AsSpan(
                    (overlap.Y - bounds.Y + row) * bounds.Width + overlap.X - bounds.X, overlap.Width));
        }
        _mask = resized;
        _maskBounds = bounds;
    }
    internal override long RetainedBytes => base.RetainedBytes + _mask.LongLength;
}

public abstract class RasterLayer : Layer
{
    private byte[] _pixels;
    private DocumentRect _pixelBounds;
    private protected RasterLayer(LayerKind kind, DocumentRect bounds, ReadOnlySpan<byte> bgra,
        string name, Guid id)
        : base(kind, bounds, name, id)
    {
        if (bgra.Length != checked(bounds.Width * bounds.Height * 4))
            throw new ArgumentException("Raster size mismatch.", nameof(bgra));
        _pixels = bgra.ToArray();
        _pixelBounds = bounds;
    }
    protected DocumentRect PixelBounds => _pixelBounds;
    public ReadOnlySpan<byte> PixelAt(int x, int y) => PixelAtRaster(x, y);
    protected ReadOnlySpan<byte> PixelAtRaster(int x, int y)
    {
        if (x < PixelBounds.X || y < PixelBounds.Y || x >= PixelBounds.Right || y >= PixelBounds.Bottom)
            throw new ArgumentOutOfRangeException(nameof(x));
        return _pixels.AsSpan(checked(((y - PixelBounds.Y) * PixelBounds.Width + x - PixelBounds.X) * 4), 4);
    }
    public byte[] CopyPixels(DocumentRect region) => PixelRegion.Copy(_pixels, PixelBounds, region, 4);
    public byte[] CopyPixelsWithTransparentOutside(DocumentRect region)
    {
        var result = new byte[checked(region.Width * region.Height * 4)];
        var overlap = PixelBounds.Intersect(region);
        if (overlap.IsEmpty) return result;
        for (var row = 0; row < overlap.Height; row++)
            _pixels.AsSpan(checked(((overlap.Y - PixelBounds.Y + row) * PixelBounds.Width
                + overlap.X - PixelBounds.X) * 4), overlap.Width * 4).CopyTo(result.AsSpan(
                checked(((overlap.Y - region.Y + row) * region.Width + overlap.X - region.X) * 4),
                overlap.Width * 4));
        return result;
    }
    internal void WritePixels(DocumentRect region, byte[] bytes) => PixelRegion.Write(_pixels, PixelBounds, region, bytes, 4);
    internal void ResizePixels(DocumentRect bounds)
    {
        if (bounds.IsEmpty) throw new EditException(EditError.InvalidPatch);
        if (bounds == PixelBounds) return;
        var resized = new byte[checked(bounds.Width * bounds.Height * 4)];
        var overlap = PixelBounds.Intersect(bounds);
        if (!overlap.IsEmpty)
        {
            for (var row = 0; row < overlap.Height; row++)
                _pixels.AsSpan(checked(((overlap.Y - PixelBounds.Y + row) * PixelBounds.Width
                    + overlap.X - PixelBounds.X) * 4), overlap.Width * 4).CopyTo(resized.AsSpan(
                    checked(((overlap.Y - bounds.Y + row) * bounds.Width + overlap.X - bounds.X) * 4),
                    overlap.Width * 4));
        }
        _pixels = resized;
        _pixelBounds = bounds;
    }
    internal override long RetainedBytes => base.RetainedBytes + _pixels.LongLength;
}

public readonly record struct PatchTransform
{
    public const double MinimumScale = 0.01;
    public const double MaximumScale = 10.0;

    public PatchTransform(double centerX, double centerY, double scale = 1, double rotationDegrees = 0)
    {
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY)
            || !double.IsFinite(scale) || scale < MinimumScale || scale > MaximumScale
            || !double.IsFinite(rotationDegrees)) throw new ArgumentOutOfRangeException(nameof(scale));
        CenterX = centerX;
        CenterY = centerY;
        Scale = scale;
        RotationDegrees = NormalizeDegrees(rotationDegrees);
    }

    public double CenterX { get; }
    public double CenterY { get; }
    public double Scale { get; }
    public double RotationDegrees { get; }

    private static double NormalizeDegrees(double value)
    {
        value %= 360;
        return value <= -180 ? value + 360 : value > 180 ? value - 360 : value;
    }
}

public sealed class PatchLayer : RasterLayer
{
    private readonly DocumentPoint[] _sourcePolygon;
    private PatchTransform _transform;

    public PatchLayer(DocumentRect sourceBounds, ReadOnlySpan<byte> bgra, string name = "")
        : this(sourceBounds, bgra,
            new PatchTransform(sourceBounds.X + sourceBounds.Width / 2.0,
                sourceBounds.Y + sourceBounds.Height / 2.0), [], name) { }

    public PatchLayer(DocumentRect sourceBounds, ReadOnlySpan<byte> bgra, PatchTransform transform,
        IReadOnlyList<DocumentPoint>? sourcePolygon = null, string name = "")
        : this(Guid.NewGuid(), sourceBounds, bgra, transform, sourcePolygon, name)
    {
    }

    internal PatchLayer(Guid id, DocumentRect sourceBounds, ReadOnlySpan<byte> bgra,
        PatchTransform transform, IReadOnlyList<DocumentPoint>? sourcePolygon = null, string name = "")
        : base(LayerKind.Patch, sourceBounds, bgra, name, id)
    {
        _transform = transform;
        _sourcePolygon = sourcePolygon?.ToArray() ?? [];
        _ = Bounds;
    }

    public PixelSize SourceSize => new(PixelBounds.Width, PixelBounds.Height);
    public DocumentRect SourceBounds => PixelBounds;
    public PatchTransform Transform => _transform;
    public IReadOnlyList<DocumentPoint> SourcePolygon => Array.AsReadOnly(_sourcePolygon);
    public override DocumentRect Bounds => CalculateBounds(SourceSize, _transform);
    public byte[] CopySourcePixels() => CopyPixels(PixelBounds);

    public ReadOnlySpan<byte> SampleAt(int documentX, int documentY)
    {
        var radians = _transform.RotationDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var dx = documentX + 0.5 - _transform.CenterX;
        var dy = documentY + 0.5 - _transform.CenterY;
        var localX = (cos * dx + sin * dy) / _transform.Scale + SourceSize.Width / 2.0;
        var localY = (-sin * dx + cos * dy) / _transform.Scale + SourceSize.Height / 2.0;
        var x = (int)Math.Floor(localX);
        var y = (int)Math.Floor(localY);
        return x < 0 || y < 0 || x >= SourceSize.Width || y >= SourceSize.Height
            ? ReadOnlySpan<byte>.Empty
            : PixelAtRaster(PixelBounds.X + x, PixelBounds.Y + y);
    }

    internal void SetTransform(PatchTransform transform)
    {
        _transform = transform;
        _ = Bounds;
    }

    public static DocumentRect CalculateBounds(PixelSize size, PatchTransform transform)
    {
        var radians = transform.RotationDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians) * transform.Scale;
        var sin = Math.Sin(radians) * transform.Scale;
        var halfWidth = size.Width / 2.0;
        var halfHeight = size.Height / 2.0;
        var corners = new[]
        {
            (-halfWidth, -halfHeight), (halfWidth, -halfHeight),
            (halfWidth, halfHeight), (-halfWidth, halfHeight),
        };
        var xs = corners.Select(point => transform.CenterX + point.Item1 * cos - point.Item2 * sin).ToArray();
        var ys = corners.Select(point => transform.CenterY + point.Item1 * sin + point.Item2 * cos).ToArray();
        var left = (int)Math.Floor(xs.Min());
        var top = (int)Math.Floor(ys.Min());
        var right = (int)Math.Ceiling(xs.Max());
        var bottom = (int)Math.Ceiling(ys.Max());
        if (left < 0 || top < 0 || right <= left || bottom <= top)
            throw new EditException(EditError.InvalidLayer);
        return new(left, top, right - left, bottom - top);
    }

    internal override long RetainedBytes => base.RetainedBytes + _sourcePolygon.LongLength * 16L + 64;
}

public sealed class RepairLayer : RasterLayer
{
    public RepairLayer(DocumentRect bounds, ReadOnlySpan<byte> bgra, string name = "",
        Guid? ownerPartId = null)
        : this(Guid.NewGuid(), bounds, bgra, name, ownerPartId) { }
    internal RepairLayer(Guid id, DocumentRect bounds, ReadOnlySpan<byte> bgra, string name = "",
        Guid? ownerPartId = null)
        : base(LayerKind.Repair, bounds, bgra, name, id)
    {
        if (ownerPartId == Guid.Empty) throw new ArgumentException("Empty Part identity.", nameof(ownerPartId));
        OwnerPartId = ownerPartId;
    }
    /// <summary>The semantic Part whose excluded region this underpaint repairs, when authored.</summary>
    public Guid? OwnerPartId { get; }
    public override DocumentRect Bounds => PixelBounds;
}

internal static class PixelRegion
{
    internal static byte[] Copy(byte[] data, DocumentRect bounds, DocumentRect region, int channels)
    {
        Validate(bounds, region);
        var result = new byte[checked(region.Width * region.Height * channels)];
        for (var row = 0; row < region.Height; row++)
            data.AsSpan(((region.Y - bounds.Y + row) * bounds.Width + region.X - bounds.X) * channels,
                region.Width * channels).CopyTo(result.AsSpan(row * region.Width * channels));
        return result;
    }
    internal static void Write(byte[] data, DocumentRect bounds, DocumentRect region, byte[] patch, int channels)
    {
        Validate(bounds, region);
        if (patch.Length != checked(region.Width * region.Height * channels))
            throw new EditException(EditError.InvalidPatch);
        for (var row = 0; row < region.Height; row++)
            patch.AsSpan(row * region.Width * channels, region.Width * channels).CopyTo(
                data.AsSpan(((region.Y - bounds.Y + row) * bounds.Width + region.X - bounds.X) * channels));
    }
    private static void Validate(DocumentRect bounds, DocumentRect region)
    {
        if (!bounds.Contains(region)) throw new EditException(EditError.InvalidPatch);
    }
}
