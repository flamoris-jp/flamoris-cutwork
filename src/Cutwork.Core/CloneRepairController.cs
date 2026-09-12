namespace Flamoris.Cutwork.Core;

public enum CloneRepairState { Idle, Painting }
public enum CloneRepairMessage { Ready, SourceRequired, SourceSet, Painting, Cancelled, Committed }

public readonly record struct CloneRepairStatus(CloneRepairMessage Message);

public sealed record CloneRepairSnapshot(
    CloneRepairState State,
    DocumentPoint? HoverPoint,
    DocumentPoint? SourceAnchor,
    DocumentPoint? DestinationAnchor,
    DocumentPoint? StrokeOffset,
    double Radius,
    CloneRepairStatus Status);

/// <summary>Point-source Clone Repair. Source and stroke geometry are document-space only.</summary>
public sealed class CloneRepairController : ICanvasToolInput
{
    public const double MinimumRadius = 0.5;
    public const double MaximumRadius = 512;
    public const double WheelStep = 1;

    private readonly EditorSession _session;
    private readonly ICloneRepairKernel _kernel;
    private EditTransaction? _transaction;
    private RepairLayer? _repair;
    private StrokeSampler? _sampler;
    private DocumentPoint? _hover;
    private DocumentPoint? _sourceAnchor;
    private DocumentPoint? _destinationAnchor;
    private DocumentPoint? _strokeOffset;

    public CloneRepairController(EditorSession session, ICloneRepairKernel kernel)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
    }

    public bool IsActive { get; private set; }
    public CloneRepairState State { get; private set; }
    public double Radius { get; private set; } = 12;
    public CloneRepairStatus Status { get; private set; } = new(CloneRepairMessage.Ready);
    public event EventHandler? Changed;

    public void SetRadius(double radius)
    {
        if (!double.IsFinite(radius) || radius < MinimumRadius || radius > MaximumRadius)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (Radius == radius) return;
        Radius = radius;
        NotifyChanged();
    }

    public void Activate()
    {
        IsActive = true;
        Status = new(_sourceAnchor is null ? CloneRepairMessage.Ready : CloneRepairMessage.SourceSet);
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
        if (State != CloneRepairState.Idle || _session.Document is not { } document)
            return CanvasInputEffects.None;

        _hover = point;
        if (modifiers.HasFlag(CanvasModifiers.Alt))
        {
            _sourceAnchor = point;
            Status = new(CloneRepairMessage.SourceSet);
            NotifyChanged();
            return CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged;
        }
        if (_sourceAnchor is not { } source)
        {
            Status = new(CloneRepairMessage.SourceRequired);
            NotifyChanged();
            return CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged;
        }

        _transaction = _session.BeginTransaction();
        _repair = _session.SelectedLayerId is { } selected
            && document.GetLayer(selected) is RepairLayer existing ? existing : null;
        if (_repair is null)
        {
            var x = Math.Clamp((int)Math.Floor(point.X), 0, document.Dimensions.Width - 1);
            var y = Math.Clamp((int)Math.Floor(point.Y), 0, document.Dimensions.Height - 1);
            _repair = new RepairLayer(new DocumentRect(x, y, 1, 1), new byte[4]);
            _transaction.Apply(new AddLayer(_repair));
        }

        _destinationAnchor = point;
        _strokeOffset = new(source.X - point.X, source.Y - point.Y);
        _sampler = new StrokeSampler(Radius);
        State = CloneRepairState.Painting;
        Status = new(CloneRepairMessage.Painting);
        ApplySamples(_sampler.Begin(point));
        NotifyChanged();
        return CanvasInputEffects.Handled | CanvasInputEffects.CapturePointer
            | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects PointerMove(DocumentPoint? point, CanvasModifiers modifiers)
    {
        var changed = _hover != point;
        _hover = point;
        if (State == CloneRepairState.Painting && point is { } current && _sampler is not null)
            ApplySamples(_sampler.Add(current));
        if (changed) NotifyChanged();
        return changed ? CanvasInputEffects.ToolOverlayChanged : CanvasInputEffects.None;
    }

    public CanvasInputEffects PointerUp(DocumentPoint? point, CanvasPointerButton button, CanvasModifiers modifiers)
    {
        if (State != CloneRepairState.Painting || button != CanvasPointerButton.Left)
            return CanvasInputEffects.None;
        if (point is { } current && _sampler is not null) ApplySamples(_sampler.Add(current));
        _transaction!.Commit(_repair!.Id);
        ClearStroke();
        _hover = point;
        Status = new(CloneRepairMessage.Committed);
        NotifyChanged();
        return CanvasInputEffects.Handled | CanvasInputEffects.ReleasePointer
            | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects Wheel(int steps, CanvasModifiers modifiers)
    {
        if (steps == 0) return CanvasInputEffects.None;
        SetRadius(Math.Clamp(Radius - steps * WheelStep, MinimumRadius, MaximumRadius));
        return CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects KeyDown(CanvasToolKey key, CanvasModifiers modifiers)
    {
        if (key != CanvasToolKey.Escape || State == CloneRepairState.Idle) return CanvasInputEffects.None;
        Cancel();
        return CanvasInputEffects.Handled | CanvasInputEffects.ReleasePointer
            | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects LostPointerCapture()
    {
        if (State == CloneRepairState.Idle) return CanvasInputEffects.None;
        Cancel();
        return CanvasInputEffects.ReleasePointer | CanvasInputEffects.ToolOverlayChanged;
    }

    public void Cancel()
    {
        if (State == CloneRepairState.Idle) return;
        _transaction?.Cancel();
        ClearStroke();
        Status = new(CloneRepairMessage.Cancelled);
        NotifyChanged();
    }

    public CloneRepairSnapshot Snapshot() => new(State, _hover, _sourceAnchor,
        _destinationAnchor, _strokeOffset, Radius, Status);

    private void ApplySamples(IReadOnlyList<DocumentPoint> samples)
    {
        if (samples.Count == 0 || _repair is null || _transaction is null || _strokeOffset is not { } offset)
            return;
        try
        {
            var patch = _kernel.CreatePatch(_session.Document!.Original, _repair, samples, offset, Radius);
            if (!patch.Region.IsEmpty)
                _transaction.Apply(new RasterPatch(_repair.Id, patch.Region, patch.StraightBgra.Span));
        }
        catch
        {
            _transaction.Cancel();
            ClearStroke();
            Status = new(CloneRepairMessage.Cancelled);
            NotifyChanged();
            throw;
        }
    }

    private void ClearStroke()
    {
        _transaction = null;
        _repair = null;
        _sampler = null;
        _destinationAnchor = null;
        _strokeOffset = null;
        State = CloneRepairState.Idle;
    }

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
