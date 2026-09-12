namespace Flamoris.Cutwork.Core;

public sealed class CutworkDocument
{
    private readonly List<Layer> _layers = [];
    private object? _sessionOwner;

    public CutworkDocument(OriginalAsset original)
    {
        Original = original ?? throw new ArgumentNullException(nameof(original));
        Id = Guid.NewGuid();
        Dimensions = original.Dimensions;
        Base = new BaseLayer(Dimensions) { OwnerDocumentId = Id };
        _layers.Add(Base);
        Layers = _layers.AsReadOnly();
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
    internal void Insert(Layer layer, int index)
    {
        if (layer is BaseLayer) throw new EditException(EditError.BaseFixed);
        if (_layers.Any(item => item.Id == layer.Id) ||
            (layer.OwnerDocumentId is { } owner && owner != Id) ||
            !DocumentRect.FromSize(Dimensions).Contains(layer.Bounds)) throw new EditException(EditError.InvalidLayer);
        var divider = _layers.IndexOf(Base);
        if (index < 0 || index > _layers.Count ||
            (layer.Kind == LayerKind.Part ? index > divider : index <= divider))
            throw new EditException(EditError.BandCrossing);
        layer.OwnerDocumentId = Id;
        _layers.Insert(index, layer);
    }
    internal void Remove(Layer layer)
    {
        if (layer == Base) throw new EditException(EditError.BaseFixed);
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
}

public sealed class DocumentChange(long revision, DocumentRect dirtyRegion, DocumentRect holeRegion) : EventArgs
{
    public long Revision { get; } = revision;
    public DocumentRect DirtyRegion { get; } = dirtyRegion;
    public DocumentRect HoleRegion { get; } = holeRegion;
}
