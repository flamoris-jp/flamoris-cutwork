namespace Flamoris.Cutwork.Core;

public enum RepairFinishingKind { Blur, Smudge }
public enum RepairFinishingState { Idle, Painting }
public enum RepairFinishingMessage { Ready, SelectRepair, Painting, Cancelled, Committed }

public readonly record struct RepairFinishingPatch(
    DocumentRect Region,
    ReadOnlyMemory<byte> StraightBgra,
    bool HasChanges = true);

public interface IRepairFinishingKernel
{
    RepairFinishingPatch Blur(RepairLayer target, DocumentPoint center,
        double radius, double strength, PixelSize documentSize);
    RepairFinishingPatch Smudge(RepairLayer target, DocumentPoint from, DocumentPoint to,
        double radius, double strength, PixelSize documentSize);
}

public sealed record RepairFinishingSnapshot(
    RepairFinishingKind Kind,
    RepairFinishingState State,
    DocumentPoint? HoverPoint,
    double Radius,
    double Strength,
    RepairFinishingMessage Message);

/// <summary>Shared Blur/Smudge gesture authority over selected Repair pixels.</summary>
public sealed class RepairFinishingController : ICanvasToolInput, ICanvasDeferredWork
{
    public const double MinimumRadius = 0.5;
    public const double MaximumRadius = 512;
    public const double MinimumStrength = 0.01;
    public const double MaximumStrength = 1;
    public const double WheelStep = 1;

    private readonly EditorSession _session;
    private readonly IRepairFinishingKernel _kernel;
    private EditTransaction? _transaction;
    private RepairLayer? _target;
    private StrokeSampler? _sampler;
    private DocumentPoint? _lastSample;
    private DocumentPoint? _hover;
    private bool _finishPending;
    private DocumentPoint? _finishPoint;

    public RepairFinishingController(EditorSession session, IRepairFinishingKernel kernel,
        RepairFinishingKind kind)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
    }

    public RepairFinishingKind Kind { get; }
    public bool IsActive { get; private set; }
    public RepairFinishingState State { get; private set; }
    public double Radius { get; private set; } = 12;
    public double Strength { get; private set; } = 0.5;
    public RepairFinishingMessage Message { get; private set; } = RepairFinishingMessage.Ready;
    public bool HasPendingWork => State == RepairFinishingState.Painting
        && (_finishPending || _sampler is { HasPendingSamples: true });
    public event EventHandler? Changed;

    public void SetRadius(double radius)
    {
        if (!double.IsFinite(radius) || radius < MinimumRadius || radius > MaximumRadius)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (Radius == radius) return;
        Radius = radius;
        NotifyChanged();
    }

    public void SetStrength(double strength)
    {
        if (!double.IsFinite(strength) || strength < MinimumStrength || strength > MaximumStrength)
            throw new ArgumentOutOfRangeException(nameof(strength));
        if (Strength == strength) return;
        Strength = strength;
        NotifyChanged();
    }

    public void Activate()
    {
        IsActive = true;
        Message = RepairFinishingMessage.Ready;
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
        if (State != RepairFinishingState.Idle || _session.Document is not { } document)
            return CanvasInputEffects.None;
        _hover = point;
        if (_session.SelectedLayerId is not { } selected
            || document.GetLayer(selected) is not RepairLayer repair)
        {
            Message = RepairFinishingMessage.SelectRepair;
            NotifyChanged();
            return CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged;
        }

        _target = repair;
        _transaction = _session.BeginTransaction();
        _sampler = new StrokeSampler(Radius);
        _lastSample = point;
        State = RepairFinishingState.Painting;
        Message = RepairFinishingMessage.Painting;
        var first = _sampler.Begin(point);
        if (Kind == RepairFinishingKind.Blur) ApplySamples(first);
        NotifyChanged();
        return CanvasInputEffects.Handled | CanvasInputEffects.CapturePointer
            | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects PointerMove(DocumentPoint? point, CanvasModifiers modifiers)
    {
        var changed = _hover != point;
        _hover = point;
        if (State == RepairFinishingState.Painting && !_finishPending
            && point is { } current && _sampler is not null)
            ApplySamples(_sampler.Add(current));
        if (changed) NotifyChanged();
        return changed ? CanvasInputEffects.ToolOverlayChanged : CanvasInputEffects.None;
    }

    public CanvasInputEffects PointerUp(DocumentPoint? point, CanvasPointerButton button, CanvasModifiers modifiers)
    {
        if (State != RepairFinishingState.Painting || button != CanvasPointerButton.Left)
            return CanvasInputEffects.None;
        if (point is { } current && _sampler is not null)
        {
            ApplySamples(_sampler.Add(current));
        }
        if (_sampler is { HasPendingSamples: true })
        {
            _finishPending = true;
            _finishPoint = point;
            return CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged;
        }
        return CompletePointerUp(point);
    }

    public CanvasInputEffects ProcessPendingWork()
    {
        if (!HasPendingWork || _sampler is null) return CanvasInputEffects.None;
        if (_sampler.HasPendingSamples)
        {
            ApplySamples(_sampler.TakePendingBatch());
            return CanvasInputEffects.Handled;
        }
        return _finishPending ? CompletePointerUp(_finishPoint) : CanvasInputEffects.Handled;
    }

    private CanvasInputEffects CompletePointerUp(DocumentPoint? point)
    {
        if (point is { } current && _lastSample is { } last
            && DistanceSquared(last, current) > 1e-12)
            ApplySamples([current]);
        _transaction!.Commit(_target!.Id);
        ClearStroke();
        _hover = point;
        Message = RepairFinishingMessage.Committed;
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
        if (key != CanvasToolKey.Escape || State == RepairFinishingState.Idle)
            return CanvasInputEffects.None;
        Cancel();
        return CanvasInputEffects.Handled | CanvasInputEffects.ReleasePointer
            | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects LostPointerCapture()
    {
        if (State == RepairFinishingState.Idle) return CanvasInputEffects.None;
        Cancel();
        return CanvasInputEffects.ReleasePointer | CanvasInputEffects.ToolOverlayChanged;
    }

    public void Cancel()
    {
        if (State == RepairFinishingState.Idle) return;
        _transaction?.Cancel();
        ClearStroke();
        Message = RepairFinishingMessage.Cancelled;
        NotifyChanged();
    }

    public RepairFinishingSnapshot Snapshot() => new(Kind, State, _hover, Radius, Strength, Message);

    private void ApplySamples(IReadOnlyList<DocumentPoint> samples)
    {
        if (samples.Count > StrokeSampler.MaximumBatchSamples)
            throw new ArgumentOutOfRangeException(nameof(samples));
        if (samples.Count == 0 || _target is null || _transaction is null
            || _session.Document is not { } document) return;
        var target = _target;
        var transaction = _transaction;
        try
        {
            transaction.ApplyBatch(() =>
            {
                for (var index = 0; index < samples.Count; index++)
                {
                    var sample = samples[index];
                    var patch = Kind == RepairFinishingKind.Blur
                        ? _kernel.Blur(target, sample, Radius, Strength, document.Dimensions)
                        : _kernel.Smudge(target, _lastSample!.Value, sample,
                            Radius, Strength, document.Dimensions);
                    _lastSample = sample;
                    if (!patch.Region.IsEmpty && patch.HasChanges)
                        transaction.Apply(new RasterPatch(target.Id, patch.Region,
                            patch.StraightBgra.Span));
                }
            });
        }
        catch
        {
            transaction.Cancel();
            ClearStroke();
            Message = RepairFinishingMessage.Cancelled;
            NotifyChanged();
            throw;
        }
    }

    private void ClearStroke()
    {
        _transaction = null;
        _target = null;
        _sampler = null;
        _lastSample = null;
        _finishPending = false;
        _finishPoint = null;
        State = RepairFinishingState.Idle;
    }

    private static double DistanceSquared(DocumentPoint first, DocumentPoint second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return dx * dx + dy * dy;
    }

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
