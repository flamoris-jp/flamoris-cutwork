namespace Flamoris.Cutwork.Core;

public enum PartToolState
{
    Idle,
    DrawingFence,
    FittingPreview,
}

public enum PartToolMessage
{
    Ready,
    DrawingFence,
    NeedsThreePoints,
    FenceTooSmall,
    FittingPreview,
    Cancelled,
    Committed,
}

public readonly record struct PartToolStatus(PartToolMessage Message, int Value = 0);

public sealed record PartToolSnapshot(
    PartToolState State,
    IReadOnlyList<DocumentPoint> Fence,
    DocumentPoint? HoverPoint,
    DocumentRect MaskBounds,
    ReadOnlyMemory<byte> Mask,
    long MaskRevision,
    int Step,
    int RemainingPixels,
    int FencePixels,
    PartToolStatus Status);

public interface IPartBoundaryFitter
{
    IPartFittingSession Create(OriginalAsset original, IReadOnlyList<DocumentPoint> fence);
}

public interface IPartFittingSession
{
    DocumentRect Bounds { get; }
    int PolygonPixelCount { get; }
    int Step { get; }
    int CurrentKeepPixels { get; }
    byte[] CopyCurrentMask();
    byte[] Adjust(int wheelSteps);
}

/// <summary>
/// One production Part workflow: rough fence, deterministic fitting preview,
/// then one authored transaction. All pending state is owned here, not by Document.
/// </summary>
public sealed class PartToolController : ICanvasToolInput
{
    public const int MinimumFencePixels = 16;

    private readonly EditorSession _session;
    private readonly IPartBoundaryFitter _fitterFactory;
    private readonly List<DocumentPoint> _fence = [];
    private IPartFittingSession? _fitting;
    private byte[]? _mask;
    private DocumentPoint? _hover;
    private long _maskRevision;

    public PartToolController(EditorSession session, IPartBoundaryFitter fitterFactory)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _fitterFactory = fitterFactory ?? throw new ArgumentNullException(nameof(fitterFactory));
    }

    public bool IsActive { get; private set; }
    public PartToolState State { get; private set; }
    public PartToolStatus Status { get; private set; } = new(PartToolMessage.Ready);
    public event EventHandler? Changed;

    public void Activate()
    {
        IsActive = true;
        Status = new(PartToolMessage.Ready);
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
        if (_session.Document is null || State == PartToolState.FittingPreview)
            return CanvasInputEffects.None;

        if (State == PartToolState.Idle)
        {
            _fence.Clear();
            State = PartToolState.DrawingFence;
        }
        if (_fence.Count == 0 || _fence[^1] != point) _fence.Add(point);
        _hover = null;
        Status = new(PartToolMessage.DrawingFence, _fence.Count);
        if (clickCount >= 2) FinalizeFence();
        else NotifyChanged();
        return CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects PointerMove(DocumentPoint? point, CanvasModifiers modifiers)
    {
        if (State != PartToolState.DrawingFence || _hover == point) return CanvasInputEffects.None;
        _hover = point;
        NotifyChanged();
        return CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects PointerUp(
        DocumentPoint? point, CanvasPointerButton button, CanvasModifiers modifiers) => CanvasInputEffects.None;

    public CanvasInputEffects Wheel(int steps, CanvasModifiers modifiers)
    {
        if (State != PartToolState.FittingPreview || _fitting is null) return CanvasInputEffects.None;
        var previousStep = _fitting.Step;
        _mask = _fitting.Adjust(steps);
        Status = new(PartToolMessage.FittingPreview);
        if (_fitting.Step != previousStep)
        {
            _maskRevision++;
            NotifyChanged();
        }
        return CanvasInputEffects.Handled
            | (_fitting.Step != previousStep ? CanvasInputEffects.ToolOverlayChanged : CanvasInputEffects.None);
    }

    public CanvasInputEffects KeyDown(CanvasToolKey key, CanvasModifiers modifiers)
    {
        if (key == CanvasToolKey.Escape && State != PartToolState.Idle)
        {
            Cancel();
            return CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged;
        }
        if (key != CanvasToolKey.Enter) return CanvasInputEffects.None;
        if (State == PartToolState.DrawingFence)
        {
            FinalizeFence();
            return CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged;
        }
        if (State == PartToolState.FittingPreview)
        {
            Commit();
            return CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged;
        }
        return CanvasInputEffects.None;
    }

    public bool FinalizeFence()
    {
        if (State != PartToolState.DrawingFence) return false;
        if (_fence.Count < 3)
        {
            Status = new(PartToolMessage.NeedsThreePoints, _fence.Count);
            NotifyChanged();
            return false;
        }

        var document = _session.Document ?? throw new EditException(EditError.NoDocument);
        IPartFittingSession fitting;
        try { fitting = _fitterFactory.Create(document.Original, _fence.ToArray()); }
        catch (ArgumentException)
        {
            Status = new(PartToolMessage.FenceTooSmall);
            NotifyChanged();
            return false;
        }
        if (fitting.PolygonPixelCount < MinimumFencePixels)
        {
            Status = new(PartToolMessage.FenceTooSmall, fitting.PolygonPixelCount);
            NotifyChanged();
            return false;
        }

        _fitting = fitting;
        _mask = fitting.CopyCurrentMask();
        _maskRevision++;
        _hover = null;
        State = PartToolState.FittingPreview;
        Status = new(PartToolMessage.FittingPreview);
        NotifyChanged();
        return true;
    }

    public PartLayer? Commit()
    {
        if (State != PartToolState.FittingPreview || _fitting is null || _mask is null) return null;
        var part = new PartLayer(_fitting.Bounds, _mask);
        using var transaction = _session.BeginTransaction();
        transaction.Apply(new AddLayer(part));
        transaction.Commit(part.Id);
        ClearPending();
        Status = new(PartToolMessage.Committed);
        NotifyChanged();
        return part;
    }

    public void Cancel()
    {
        if (State == PartToolState.Idle && _fence.Count == 0 && _fitting is null) return;
        ClearPending();
        Status = new(PartToolMessage.Cancelled);
        NotifyChanged();
    }

    public PartToolSnapshot Snapshot() => new(
        State,
        _fence.ToArray(),
        _hover,
        _fitting?.Bounds ?? default,
        _mask ?? ReadOnlyMemory<byte>.Empty,
        _maskRevision,
        _fitting?.Step ?? 0,
        _fitting?.CurrentKeepPixels ?? 0,
        _fitting?.PolygonPixelCount ?? 0,
        Status);

    private void ClearPending()
    {
        _fence.Clear();
        _fitting = null;
        if (_mask is not null) _maskRevision++;
        _mask = null;
        _hover = null;
        State = PartToolState.Idle;
    }

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
