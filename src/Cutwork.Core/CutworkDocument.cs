namespace Flamoris.Cutwork.Core;

public sealed class CutworkDocument
{
    private readonly List<Layer> _layers = [];
    private object? _sessionOwner;
    private int _nextPartOrder;

    public CutworkDocument(OriginalAsset original)
        : this(original, Guid.NewGuid(), Guid.NewGuid())
    {
    }

    private CutworkDocument(OriginalAsset original, Guid id, Guid baseId)
    {
        Original = original ?? throw new ArgumentNullException(nameof(original));
        if (id == Guid.Empty || baseId == Guid.Empty) throw new EditException(EditError.InvalidLayer);
        Id = id;
        Dimensions = original.Dimensions;
        Base = new BaseLayer(baseId, Dimensions) { OwnerDocumentId = Id };
        _layers.Add(Base);
        Layers = _layers.AsReadOnly();
    }

    public static CutworkDocument Restore(Guid id, OriginalAsset original,
        IReadOnlyList<LayerRestoreState> layers)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(layers);
        if (id == Guid.Empty || layers.Count == 0) throw new EditException(EditError.InvalidLayer);
        var baseStates = layers.OfType<BaseLayerRestoreState>().ToArray();
        if (baseStates.Length != 1) throw new EditException(EditError.InvalidLayer);
        var baseIndex = layers.ToList().IndexOf(baseStates[0]);
        if (layers.Take(baseIndex).Any(layer => layer is not PartLayerRestoreState)
            || layers.Skip(baseIndex + 1).Any(layer => layer is not PatchLayerRestoreState
                && layer is not RepairLayerRestoreState))
            throw new EditException(EditError.BandCrossing);
        if (layers.Any(layer => layer.Id == Guid.Empty || layer.Id == id)
            || layers.Select(layer => layer.Id).Distinct().Count() != layers.Count)
            throw new EditException(EditError.InvalidLayer);

        var partStates = layers.OfType<PartLayerRestoreState>().ToArray();
        var hasExplicitPartOrder = partStates.Any(part => part.PartOrder.HasValue);
        if (hasExplicitPartOrder && partStates.Any(part => !part.PartOrder.HasValue))
            throw new EditException(EditError.InvalidLayer);
        var partOrders = hasExplicitPartOrder
            ? partStates.ToDictionary(part => part.Id, part => part.PartOrder!.Value)
            // v1 stored Parts newest-first in the compositor band. Reverse that once to
            // recover the closest deterministic creation order without using it thereafter.
            : partStates.Select((part, index) => (part.Id, Order: partStates.Length - index - 1))
                .ToDictionary(item => item.Id, item => item.Order);
        if (partOrders.Values.Any(order => order is < 0 or >= int.MaxValue)
            || partOrders.Values.Distinct().Count() != partOrders.Count)
            throw new EditException(EditError.InvalidLayer);
        var partIds = partStates.Select(part => part.Id).ToHashSet();
        if (layers.OfType<RepairLayerRestoreState>().Any(repair =>
                repair.OwnerPartId == Guid.Empty
                || repair.OwnerPartId is { } owner && !partIds.Contains(owner)))
            throw new EditException(EditError.InvalidLayer);

        var document = new CutworkDocument(original, id, baseStates[0].Id);
        document._layers.Clear();
        foreach (var state in layers)
        {
            var layer = document.CreateRestoredLayer(state, partOrders);
            if (!document.Contains(layer.Bounds)) throw new EditException(EditError.InvalidLayer);
            layer.Name = state.Name ?? throw new EditException(EditError.InvalidLayer);
            layer.SemanticName = state.SemanticName;
            layer.Visible = state.Visible;
            layer.OwnerDocumentId = id;
            document._layers.Add(layer);
        }
        document._nextPartOrder = partOrders.Count == 0 ? 0 : checked(partOrders.Values.Max() + 1);
        return document;
    }

    public Guid Id { get; }

    public PixelSize Dimensions { get; }

    public OriginalAsset Original { get; }

    public BaseLayer Base { get; }
    public IReadOnlyList<Layer> Layers { get; }
    public long Revision { get; private set; }
    public event EventHandler<DocumentChange>? Changed;
    public Layer GetLayer(Guid id) => _layers.Find(layer => layer.Id == id)
        ?? throw new EditException(EditError.MissingLayer);

    public bool CanDelete(Guid id) => _layers.Any(layer => layer.Id == id && layer != Base);
    public bool CanReorder(Guid id, int targetIndex)
    {
        var index = _layers.FindIndex(layer => layer.Id == id);
        if (index < 0 || targetIndex < 0 || targetIndex >= _layers.Count || _layers[index] == Base) return false;
        var divider = _layers.IndexOf(Base);
        return index < divider ? targetIndex < divider : targetIndex > divider;
    }

    internal int IndexOf(Layer layer) => _layers.IndexOf(layer);
    internal IReadOnlyList<RepairLayer> OwnedRepairs(Guid partId) => _layers
        .OfType<RepairLayer>().Where(layer => layer.OwnerPartId == partId).ToArray();
    internal bool Contains(DocumentRect bounds) => DocumentRect.FromSize(Dimensions).Contains(bounds);
    internal void Insert(Layer layer, int index)
    {
        if (layer is BaseLayer) throw new EditException(EditError.BaseFixed);
        if (_layers.Any(item => item.Id == layer.Id) ||
            (layer.OwnerDocumentId is { } owner && owner != Id) ||
            !Contains(layer.Bounds)) throw new EditException(EditError.InvalidLayer);
        var divider = _layers.IndexOf(Base);
        if (index < 0 || index > _layers.Count ||
            (layer.Kind == LayerKind.Part ? index > divider : index <= divider))
            throw new EditException(EditError.BandCrossing);
        if (layer is RepairLayer { OwnerPartId: { } ownerPartId }
            && _layers.Find(item => item.Id == ownerPartId) is not PartLayer)
            throw new EditException(EditError.InvalidLayer);
        if (layer is PartLayer part)
        {
            var partOrder = part.PartOrder < 0 ? _nextPartOrder : part.PartOrder;
            if (partOrder >= int.MaxValue
                || _layers.OfType<PartLayer>().Any(item => item.PartOrder == partOrder))
                throw new EditException(EditError.InvalidLayer);
            part.PartOrder = partOrder;
            _nextPartOrder = Math.Max(_nextPartOrder, checked(partOrder + 1));
        }
        layer.OwnerDocumentId = Id;
        _layers.Insert(index, layer);
    }
    internal void Remove(Layer layer)
    {
        if (layer == Base) throw new EditException(EditError.BaseFixed);
        if (layer is PartLayer && _layers.OfType<RepairLayer>()
                .Any(repair => repair.OwnerPartId == layer.Id))
            throw new EditException(EditError.InvalidLayer);
        if (!_layers.Remove(layer)) throw new EditException(EditError.MissingLayer);
    }
    internal void Reorder(Layer layer, int target)
    {
        if (layer == Base) throw new EditException(EditError.BaseFixed);
        if (!CanReorder(layer.Id, target)) throw new EditException(EditError.BandCrossing);
        _layers.Remove(layer); _layers.Insert(target, layer);
    }
    internal void Publish(DocumentRect dirty, DocumentRect holes, IEnumerable<Layer> touched)
    {
        Revision++;
        foreach (var layer in touched.Distinct()) layer.Revision++;
        if (!holes.IsEmpty) Base.Revision++;
        Changed?.Invoke(this, new DocumentChange(Revision, dirty, holes));
    }
    internal void Claim(object session)
    {
        if (_sessionOwner is not null && !ReferenceEquals(_sessionOwner, session))
            throw new EditException(EditError.InvalidLayer);
        _sessionOwner = session;
    }
    internal void Release(object session)
    {
        if (ReferenceEquals(_sessionOwner, session)) _sessionOwner = null;
    }

    private Layer CreateRestoredLayer(LayerRestoreState state,
        IReadOnlyDictionary<Guid, int> partOrders) => state switch
    {
        BaseLayerRestoreState baseState when baseState.Id == Base.Id => Base,
        PartLayerRestoreState part => new PartLayer(part.Id, part.Bounds, part.Mask.Span,
            part.Name, partOrders[part.Id]),
        PatchLayerRestoreState patch => new PatchLayer(patch.Id, patch.SourceBounds,
            patch.Pixels.Span, patch.Transform, patch.SourcePolygon, patch.Name),
        RepairLayerRestoreState repair => new RepairLayer(repair.Id, repair.Bounds,
            repair.Pixels.Span, repair.Name, repair.OwnerPartId),
        _ => throw new EditException(EditError.InvalidLayer),
    };
}

public sealed class DocumentChange(long revision, DocumentRect dirtyRegion, DocumentRect holeRegion) : EventArgs
{
    public long Revision { get; } = revision;
    public DocumentRect DirtyRegion { get; } = dirtyRegion;
    public DocumentRect HoleRegion { get; } = holeRegion;
}
