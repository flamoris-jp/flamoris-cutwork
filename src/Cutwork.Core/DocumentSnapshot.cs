namespace Flamoris.Cutwork.Core;

/// <summary>Owned, immutable-at-capture pixel copies. Original is already immutable and shared.</summary>
public static class DocumentSnapshot
{
    public static long EstimatedBytes(CutworkDocument document) => document.WorkLogBytes+document.Layers.Sum(l =>
        l switch { PartLayer => (long)l.Bounds.Width*l.Bounds.Height,
            PatchLayer p => (long)p.SourceBounds.Width*p.SourceBounds.Height*4,
            RepairLayer => (long)l.Bounds.Width*l.Bounds.Height*4, _ => 0L });

    public static CutworkDocument Capture(CutworkDocument document) => CutworkDocument.Restore(
        document.Id,document.Original,document.Layers.Select<Layer,LayerRestoreState>(l=> l switch
        {
            BaseLayer => new BaseLayerRestoreState(l.Id,l.Name,l.SemanticName,l.Visible),
            PartLayer p => new PartLayerRestoreState(l.Id,l.Name,l.SemanticName,l.Visible,p.Bounds,p.CopyMask(p.Bounds),p.PartOrder),
            PatchLayer p => new PatchLayerRestoreState(l.Id,l.Name,l.SemanticName,l.Visible,p.SourceBounds,p.CopySourcePixels(),p.Transform,p.SourcePolygon),
            RepairLayer p => new RepairLayerRestoreState(l.Id,l.Name,l.SemanticName,l.Visible,p.Bounds,p.CopyPixels(p.Bounds),p.OwnerPartId),
            _ => throw new InvalidOperationException("Unsupported layer.")
        }).ToArray(),document.WorkLog.ToArray());
}
