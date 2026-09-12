namespace Flamoris.Cutwork.Core;

/// <summary>Intent, not a snapshot. Only EditorSession may apply commands.</summary>
public abstract class EditCommand
{
    private protected EditCommand() { }
    internal abstract AppliedEdit? Apply(CutworkDocument document);
}

internal sealed record AppliedEdit(Action Undo, Action Redo, Layer Layer,
    DocumentRect Dirty, DocumentRect Holes, long RetainedBytes)
{
    internal void Publish(CutworkDocument document) => document.Publish(Dirty, Holes, [Layer]);
}

public sealed class RenameLayer(Guid layerId, string name) : EditCommand
{
    internal override AppliedEdit? Apply(CutworkDocument document)
    {
        ArgumentNullException.ThrowIfNull(name);
        var layer = document.GetLayer(layerId);
        var before = layer.Name;
        if (before == name) return null;
        layer.Name = name;
        return new(() => layer.Name = before, () => layer.Name = name,
            layer, default, default, 128L + (before.Length + name.Length) * 2L);
    }
}

public sealed class SetLayerVisibility(Guid layerId, bool visible) : EditCommand
{
    internal override AppliedEdit? Apply(CutworkDocument document)
    {
        var layer = document.GetLayer(layerId);
        var before = layer.Visible;
        if (before == visible) return null;
        layer.Visible = visible;
        // Visibility never changes authored Part geometry.
        return new(() => layer.Visible = before, () => layer.Visible = visible,
            layer, layer.Bounds, default, 128);
    }
}

public sealed class AddLayer(Layer layer, int? index = null) : EditCommand
{
    internal override AppliedEdit Apply(CutworkDocument document)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var target = index ?? (layer.Kind == LayerKind.Part ? 0 : document.Layers.Count);
        document.Insert(layer, target);
        return new(() => document.Remove(layer), () => document.Insert(layer, target), layer,
            layer.Bounds, layer is PartLayer ? layer.Bounds : default, layer.RetainedBytes);
    }
}

public sealed class DeleteLayer(Guid layerId) : EditCommand
{
    internal override AppliedEdit Apply(CutworkDocument document)
    {
        var layer = document.GetLayer(layerId);
        var index = document.IndexOf(layer);
        document.Remove(layer);
        return new(() => document.Insert(layer, index), () => document.Remove(layer), layer,
            layer.Bounds, layer is PartLayer ? layer.Bounds : default, layer.RetainedBytes);
    }
}

public sealed class ReorderLayer(Guid layerId, int targetIndex) : EditCommand
{
    internal override AppliedEdit? Apply(CutworkDocument document)
    {
        var layer = document.GetLayer(layerId);
        var before = document.IndexOf(layer);
        // Validate even an attempted no-op on Base.
        if (layer == document.Base) throw new EditException(EditError.BaseFixed);
        if (!document.CanReorder(layerId, targetIndex)) throw new EditException(EditError.BandCrossing);
        if (before == targetIndex) return null;
        document.Reorder(layer, targetIndex);
        return new(() => document.Reorder(layer, before), () => document.Reorder(layer, targetIndex),
            layer, layer.Bounds, default, 128);
    }
}

public sealed class MaskPatch : EditCommand
{
    private readonly Guid _layerId;
    private readonly DocumentRect _region;
    private readonly byte[] _after;
    public MaskPatch(Guid layerId, DocumentRect region, ReadOnlySpan<byte> after)
    {
        _layerId = layerId; _region = region; _after = after.ToArray();
    }
    internal override AppliedEdit? Apply(CutworkDocument document)
    {
        if (document.GetLayer(_layerId) is not PartLayer layer) throw new EditException(EditError.InvalidPatch);
        if (!document.Contains(_region)) throw new EditException(EditError.InvalidPatch);
        var beforeBounds = layer.Bounds;
        var afterBounds = beforeBounds.Union(_region);
        var before = layer.CopyMaskWithTransparentOutside(_region);
        if (before.Length != _after.Length) throw new EditException(EditError.InvalidPatch);
        if (before.AsSpan().SequenceEqual(_after) && beforeBounds == afterBounds) return null;
        var after = _after;
        var region = _region;
        layer.ResizeMask(afterBounds);
        layer.WriteMask(region, after);
        return new(() =>
            {
                layer.WriteMask(region, before);
                layer.ResizeMask(beforeBounds);
            },
            () =>
            {
                layer.ResizeMask(afterBounds);
                layer.WriteMask(region, after);
            },
            layer, region, region, 128L + before.LongLength + after.LongLength);
    }
}

public sealed class RasterPatch : EditCommand
{
    private readonly Guid _layerId;
    private readonly DocumentRect _region;
    private readonly byte[] _after;
    public RasterPatch(Guid layerId, DocumentRect region, ReadOnlySpan<byte> after)
    {
        _layerId = layerId; _region = region; _after = after.ToArray();
    }
    internal override AppliedEdit? Apply(CutworkDocument document)
    {
        if (document.GetLayer(_layerId) is not RasterLayer layer) throw new EditException(EditError.InvalidPatch);
        if (!document.Contains(_region)) throw new EditException(EditError.InvalidPatch);
        var beforeBounds = layer.Bounds;
        var canGrow = layer is RepairLayer;
        var afterBounds = canGrow ? beforeBounds.Union(_region) : beforeBounds;
        var before = canGrow
            ? layer.CopyPixelsWithTransparentOutside(_region)
            : layer.CopyPixels(_region);
        if (before.Length != _after.Length) throw new EditException(EditError.InvalidPatch);
        if (before.AsSpan().SequenceEqual(_after) && beforeBounds == afterBounds) return null;
        var after = _after;
        var region = _region;
        if (canGrow) layer.ResizePixels(afterBounds);
        layer.WritePixels(region, after);
        return new(() =>
            {
                layer.WritePixels(region, before);
                if (canGrow) layer.ResizePixels(beforeBounds);
            },
            () =>
            {
                if (canGrow) layer.ResizePixels(afterBounds);
                layer.WritePixels(region, after);
            },
            layer, region, default, 128L + before.LongLength + after.LongLength);
    }
}

public sealed class SetPatchTransform(Guid layerId, PatchTransform transform) : EditCommand
{
    internal override AppliedEdit? Apply(CutworkDocument document)
    {
        if (document.GetLayer(layerId) is not PatchLayer layer) throw new EditException(EditError.InvalidLayer);
        var before = layer.Transform;
        if (before == transform) return null;
        var beforeBounds = layer.Bounds;
        try
        {
            layer.SetTransform(transform);
            if (!document.Contains(layer.Bounds)) throw new EditException(EditError.InvalidLayer);
        }
        catch
        {
            layer.SetTransform(before);
            throw;
        }
        var dirty = beforeBounds.Union(layer.Bounds);
        return new(() => layer.SetTransform(before), () => layer.SetTransform(transform),
            layer, dirty, default, 192);
    }
}
