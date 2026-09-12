namespace Flamoris.Cutwork.Core;

public enum LayerKind { Base, Part, Patch, Repair }

public abstract class Layer
{
    private protected Layer(LayerKind kind, DocumentRect bounds, string name)
    {
        if (bounds.IsEmpty) throw new ArgumentException("Empty layer bounds.", nameof(bounds));
        Id = Guid.NewGuid(); Kind = kind; Bounds = bounds;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
    public Guid Id { get; }
    public LayerKind Kind { get; }
    public DocumentRect Bounds { get; }
    public string Name { get; internal set; }
    public string? SemanticName { get; internal set; }
    public bool Visible { get; internal set; } = true;
    public long Revision { get; internal set; }
    internal Guid? OwnerDocumentId { get; set; }
    internal virtual long RetainedBytes => 128L + Name.Length * 2L + (SemanticName?.Length ?? 0) * 2L;
}

public sealed class BaseLayer : Layer
{
    internal BaseLayer(PixelSize dimensions) : base(LayerKind.Base, DocumentRect.FromSize(dimensions), "") { }
}

public sealed class PartLayer : Layer
{
    private readonly byte[] _mask;
    public PartLayer(DocumentRect bounds, ReadOnlySpan<byte> mask, string name = "")
        : base(LayerKind.Part, bounds, name)
    {
        if (mask.Length != checked(bounds.Width * bounds.Height))
            throw new ArgumentException("Mask size mismatch.", nameof(mask));
        _mask = mask.ToArray();
    }
    public byte MaskAt(int x, int y) => x < Bounds.X || y < Bounds.Y || x >= Bounds.Right || y >= Bounds.Bottom
        ? (byte)0 : _mask[(y - Bounds.Y) * Bounds.Width + x - Bounds.X];
    public byte[] CopyMask(DocumentRect region) => PixelRegion.Copy(_mask, Bounds, region, 1);
    internal void WriteMask(DocumentRect region, byte[] bytes) => PixelRegion.Write(_mask, Bounds, region, bytes, 1);
    internal override long RetainedBytes => base.RetainedBytes + _mask.LongLength;
}

public abstract class RasterLayer : Layer
{
    private readonly byte[] _pixels;
    private protected RasterLayer(LayerKind kind, DocumentRect bounds, ReadOnlySpan<byte> bgra, string name)
        : base(kind, bounds, name)
    {
        if (bgra.Length != checked(bounds.Width * bounds.Height * 4))
            throw new ArgumentException("Raster size mismatch.", nameof(bgra));
        _pixels = bgra.ToArray();
    }
    public ReadOnlySpan<byte> PixelAt(int x, int y)
    {
        if (x < Bounds.X || y < Bounds.Y || x >= Bounds.Right || y >= Bounds.Bottom)
            throw new ArgumentOutOfRangeException(nameof(x));
        return _pixels.AsSpan(checked(((y - Bounds.Y) * Bounds.Width + x - Bounds.X) * 4), 4);
    }
    public byte[] CopyPixels(DocumentRect region) => PixelRegion.Copy(_pixels, Bounds, region, 4);
    internal void WritePixels(DocumentRect region, byte[] bytes) => PixelRegion.Write(_pixels, Bounds, region, bytes, 4);
    internal override long RetainedBytes => base.RetainedBytes + _pixels.LongLength;
}

public sealed class PatchLayer(DocumentRect bounds, ReadOnlySpan<byte> bgra, string name = "")
    : RasterLayer(LayerKind.Patch, bounds, bgra, name);

public sealed class RepairLayer(DocumentRect bounds, ReadOnlySpan<byte> bgra, string name = "")
    : RasterLayer(LayerKind.Repair, bounds, bgra, name);

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
