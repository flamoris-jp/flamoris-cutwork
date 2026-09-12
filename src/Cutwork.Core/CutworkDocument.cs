namespace Flamoris.Cutwork.Core;

public sealed class CutworkDocument
{
    public CutworkDocument(OriginalAsset original)
    {
        Original = original ?? throw new ArgumentNullException(nameof(original));
        Id = Guid.NewGuid();
        Dimensions = original.Dimensions;
    }

    public Guid Id { get; }

    public PixelSize Dimensions { get; }

    public OriginalAsset Original { get; }
}
