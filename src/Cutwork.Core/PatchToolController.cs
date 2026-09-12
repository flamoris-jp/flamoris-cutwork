namespace Flamoris.Cutwork.Core;

public enum PatchToolState { ChoosingSource, Placing, Transforming }
public enum PatchToolMessage { Ready, DrawingSource, NeedsThreePoints, InvalidSource, Placing, Cancelled, Committed }
public readonly record struct PatchToolStatus(PatchToolMessage Message, int Value = 0);

public sealed record FrozenPatchSource(
    DocumentRect SourceBounds,
    ReadOnlyMemory<byte> StraightBgra,
    IReadOnlyList<DocumentPoint> SourcePolygon);

public interface IPatchSourceSampler
{
    FrozenPatchSource Freeze(OriginalAsset original, IReadOnlyList<DocumentPoint> sourcePolygon);
}

public sealed record PatchToolSnapshot(
    PatchToolState State,
    IReadOnlyList<DocumentPoint> SourceFence,
    DocumentPoint? HoverPoint,
    FrozenPatchSource? Source,
    PatchTransform? Transform,
    bool IsDragging,
    PatchToolStatus Status);

/// <summary>One uncommitted source → place/transform → commit Patch workflow.</summary>
public sealed class PatchToolController : ICanvasToolInput
{
    private readonly EditorSession _session;
    private readonly IPatchSourceSampler _sourceSampler;
    private readonly List<DocumentPoint> _sourceFence = [];
    private FrozenPatchSource? _source;
    private PatchTransform? _transform;
    private DocumentPoint? _hover;
    private DocumentPoint? _dragStart;
    private PatchTransform _dragTransform;

    public PatchToolController(EditorSession session, IPatchSourceSampler sourceSampler)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _sourceSampler = sourceSampler ?? throw new ArgumentNullException(nameof(sourceSampler));
    }

    public bool IsActive { get; private set; }
    public PatchToolState State { get; private set; } = PatchToolState.ChoosingSource;
    public PatchToolStatus Status { get; private set; } = new(PatchToolMessage.Ready);
    public event EventHandler? Changed;

    public void Activate()
    {
        IsActive = true;
        Status = new(PatchToolMessage.Ready);
        NotifyChanged();
    }

    public void Deactivate()
    {
        Cancel();
        IsActive = false;
        NotifyChanged();
    }

    public CanvasInputEffects PointerDown(DocumentPoint point, int clickCount, CanvasModifiers modifiers)
    {
        if (_session.Document is null) return CanvasInputEffects.None;
        if (State == PatchToolState.ChoosingSource)
        {
            if (_sourceFence.Count == 0 || _sourceFence[^1] != point) _sourceFence.Add(point);
            _hover = null;
            Status = new(PatchToolMessage.DrawingSource, _sourceFence.Count);
            if (clickCount >= 2) FinalizeSource(); else NotifyChanged();
            return CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged;
        }

        _dragStart = point;
        _dragTransform = _transform!.Value;
        State = PatchToolState.Transforming;
        NotifyChanged();
        return CanvasInputEffects.Handled | CanvasInputEffects.CapturePointer | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects PointerMove(DocumentPoint? point, CanvasModifiers modifiers)
    {
        if (State == PatchToolState.ChoosingSource)
        {
            if (_hover == point) return CanvasInputEffects.None;
            _hover = point;
            NotifyChanged();
            return CanvasInputEffects.ToolOverlayChanged;
        }
        if (_dragStart is not { } start || point is not { } current) return CanvasInputEffects.None;
        SetPendingTransform(new PatchTransform(
            _dragTransform.CenterX + current.X - start.X,
            _dragTransform.CenterY + current.Y - start.Y,
            _dragTransform.Scale,
            _dragTransform.RotationDegrees));
        return CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects PointerUp(DocumentPoint? point, CanvasPointerButton button, CanvasModifiers modifiers)
    {
        if (_dragStart is null || button != CanvasPointerButton.Left) return CanvasInputEffects.None;
        if (point is { } current) PointerMove(current, modifiers);
        _dragStart = null;
        NotifyChanged();
        return CanvasInputEffects.Handled | CanvasInputEffects.ReleasePointer | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects Wheel(int steps, CanvasModifiers modifiers) => CanvasInputEffects.None;

    public CanvasInputEffects KeyDown(CanvasToolKey key, CanvasModifiers modifiers)
    {
        if (key == CanvasToolKey.Escape && HasPending)
        {
            Cancel();
            return CanvasInputEffects.Handled | CanvasInputEffects.ReleasePointer | CanvasInputEffects.ToolOverlayChanged;
        }
        if (key != CanvasToolKey.Enter) return CanvasInputEffects.None;
        if (State == PatchToolState.ChoosingSource)
        {
            FinalizeSource();
            return CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged;
        }
        Commit();
        return CanvasInputEffects.Handled | CanvasInputEffects.ReleasePointer | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects LostPointerCapture()
    {
        if (_dragStart is null) return CanvasInputEffects.None;
        _dragStart = null;
        NotifyChanged();
        return CanvasInputEffects.ReleasePointer | CanvasInputEffects.ToolOverlayChanged;
    }

    public bool FinalizeSource()
    {
        if (State != PatchToolState.ChoosingSource) return false;
        if (_sourceFence.Count < 3)
        {
            Status = new(PatchToolMessage.NeedsThreePoints, _sourceFence.Count);
            NotifyChanged();
            return false;
        }
        try { _source = _sourceSampler.Freeze(_session.Document!.Original, _sourceFence.ToArray()); }
        catch (ArgumentException)
        {
            Status = new(PatchToolMessage.InvalidSource);
            NotifyChanged();
            return false;
        }
        var bounds = _source.SourceBounds;
        _transform = new PatchTransform(bounds.X + bounds.Width / 2.0, bounds.Y + bounds.Height / 2.0);
        _hover = null;
        State = PatchToolState.Placing;
        Status = new(PatchToolMessage.Placing);
        NotifyChanged();
        return true;
    }

    public bool SetPendingTransform(PatchTransform transform)
    {
        if (_source is null || State == PatchToolState.ChoosingSource) return false;
        var document = _session.Document ?? throw new EditException(EditError.NoDocument);
        DocumentRect bounds;
        try
        {
            bounds = PatchLayer.CalculateBounds(
                new PixelSize(_source.SourceBounds.Width, _source.SourceBounds.Height), transform);
        }
        catch (EditException) { return false; }
        if (!DocumentRect.FromSize(document.Dimensions).Contains(bounds)) return false;
        if (_transform == transform) return false;
        _transform = transform;
        State = PatchToolState.Transforming;
        Status = new(PatchToolMessage.Placing);
        NotifyChanged();
        return true;
    }

    public PatchLayer? Commit()
    {
        if (_source is null || _transform is null || State == PatchToolState.ChoosingSource) return null;
        var patch = new PatchLayer(_source.SourceBounds, _source.StraightBgra.Span,
            _transform.Value, _source.SourcePolygon);
        using var transaction = _session.BeginTransaction();
        transaction.Apply(new AddLayer(patch));
        transaction.Commit(patch.Id);
        ResetPending();
        Status = new(PatchToolMessage.Committed);
        NotifyChanged();
        return patch;
    }

    public void Cancel()
    {
        if (!HasPending) return;
        ResetPending();
        Status = new(PatchToolMessage.Cancelled);
        NotifyChanged();
    }

    public PatchToolSnapshot Snapshot() => new(
        State, _sourceFence.ToArray(), _hover, _source, _transform, _dragStart is not null, Status);

    private bool HasPending => _sourceFence.Count > 0 || _source is not null || _dragStart is not null;

    private void ResetPending()
    {
        _sourceFence.Clear();
        _source = null;
        _transform = null;
        _hover = null;
        _dragStart = null;
        State = PatchToolState.ChoosingSource;
    }

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
