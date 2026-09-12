namespace Flamoris.Cutwork.Core;

public enum MaskPolarity { Add, Erase }
public enum MaskBrushState { Idle, Painting }
public enum MaskBrushMessage { Ready, SelectPart, Painting, Cancelled, Committed }

public readonly record struct MaskBrushStatus(MaskBrushMessage Message);

public sealed record MaskBrushSnapshot(
    MaskBrushState State,
    DocumentPoint? HoverPoint,
    double Radius,
    MaskPolarity PrimaryPolarity,
    MaskPolarity EffectivePolarity,
    MaskBrushStatus Status);

/// <summary>Unified Part-mask brush. One captured drag owns one transaction.</summary>
public sealed class MaskBrushController : ICanvasToolInput
{
    public const double MinimumRadius = 0.5;
    public const double MaximumRadius = 512;

    private readonly EditorSession _session;
    private EditTransaction? _transaction;
    private PartLayer? _part;
    private StrokeSampler? _sampler;
    private DocumentPoint? _hover;
    private CanvasModifiers _modifiers;

    public MaskBrushController(EditorSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public bool IsActive { get; private set; }
    public MaskBrushState State { get; private set; }
    public double Radius { get; private set; } = 12;
    public MaskPolarity PrimaryPolarity { get; private set; } = MaskPolarity.Add;
    public MaskBrushStatus Status { get; private set; } = new(MaskBrushMessage.Ready);
    public event EventHandler? Changed;

    public void SetRadius(double radius)
    {
        if (!double.IsFinite(radius) || radius < MinimumRadius || radius > MaximumRadius)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (Radius == radius) return;
        Radius = radius;
        NotifyChanged();
    }

    public void SetPrimaryPolarity(MaskPolarity polarity)
    {
        if (!Enum.IsDefined(polarity)) throw new ArgumentOutOfRangeException(nameof(polarity));
        if (PrimaryPolarity == polarity) return;
        PrimaryPolarity = polarity;
        NotifyChanged();
    }

    public void Activate()
    {
        IsActive = true;
        Status = new(MaskBrushMessage.Ready);
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
        if (State != MaskBrushState.Idle || _session.Document is not { } document) return CanvasInputEffects.None;
        if (_session.SelectedLayerId is not { } selected || document.GetLayer(selected) is not PartLayer part)
        {
            Status = new(MaskBrushMessage.SelectPart);
            _hover = point;
            _modifiers = modifiers;
            NotifyChanged();
            return CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged;
        }

        _part = part;
        _transaction = _session.BeginTransaction();
        _sampler = new StrokeSampler(Radius);
        _hover = point;
        _modifiers = modifiers;
        State = MaskBrushState.Painting;
        Status = new(MaskBrushMessage.Painting);
        ApplySamples(_sampler.Begin(point), EffectivePolarity(modifiers));
        NotifyChanged();
        return CanvasInputEffects.Handled | CanvasInputEffects.CapturePointer | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects PointerMove(DocumentPoint? point, CanvasModifiers modifiers)
    {
        var changed = _hover != point || _modifiers != modifiers;
        _hover = point;
        _modifiers = modifiers;
        if (State == MaskBrushState.Painting && point is { } current && _sampler is not null)
            ApplySamples(_sampler.Add(current), EffectivePolarity(modifiers));
        if (changed) NotifyChanged();
        return changed ? CanvasInputEffects.ToolOverlayChanged : CanvasInputEffects.None;
    }

    public CanvasInputEffects PointerUp(DocumentPoint? point, CanvasPointerButton button, CanvasModifiers modifiers)
    {
        if (State != MaskBrushState.Painting || button != CanvasPointerButton.Left)
            return CanvasInputEffects.None;
        if (point is { } current && _sampler is not null)
            ApplySamples(_sampler.Add(current), EffectivePolarity(modifiers));
        _transaction!.Commit();
        ClearStroke();
        _hover = point;
        _modifiers = modifiers;
        Status = new(MaskBrushMessage.Committed);
        NotifyChanged();
        return CanvasInputEffects.Handled | CanvasInputEffects.ReleasePointer | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects Wheel(int steps, CanvasModifiers modifiers) => CanvasInputEffects.None;

    public CanvasInputEffects KeyDown(CanvasToolKey key, CanvasModifiers modifiers)
    {
        if (key != CanvasToolKey.Escape || State == MaskBrushState.Idle) return CanvasInputEffects.None;
        Cancel();
        return CanvasInputEffects.Handled | CanvasInputEffects.ReleasePointer | CanvasInputEffects.ToolOverlayChanged;
    }

    public CanvasInputEffects LostPointerCapture()
    {
        if (State == MaskBrushState.Idle) return CanvasInputEffects.None;
        Cancel();
        return CanvasInputEffects.ReleasePointer | CanvasInputEffects.ToolOverlayChanged;
    }

    public void Cancel()
    {
        if (State == MaskBrushState.Idle) return;
        _transaction?.Cancel();
        ClearStroke();
        Status = new(MaskBrushMessage.Cancelled);
        NotifyChanged();
    }

    public MaskBrushSnapshot Snapshot() => new(
        State, _hover, Radius, PrimaryPolarity, EffectivePolarity(_modifiers), Status);

    private void ApplySamples(IReadOnlyList<DocumentPoint> samples, MaskPolarity polarity)
    {
        if (samples.Count == 0 || _part is null || _transaction is null) return;
        var part = _part;
        var transaction = _transaction;
        try
        {
            foreach (var batch in StrokeSampler.Batch(samples))
                transaction.ApplyBatch(() => ApplySampleBatch(transaction, part, batch, polarity));
        }
        catch
        {
            transaction.Cancel();
            ClearStroke();
            Status = new(MaskBrushMessage.Cancelled);
            NotifyChanged();
            throw;
        }
    }

    private void ApplySampleBatch(EditTransaction transaction, PartLayer part,
        IReadOnlyList<DocumentPoint> samples, MaskPolarity polarity)
    {
        var region = default(DocumentRect);
        foreach (var sample in samples)
        {
            var dab = DabBounds(sample);
            region = region.Union(polarity == MaskPolarity.Add ? dab : dab.Intersect(part.Bounds));
        }
        if (region.IsEmpty) return;

        // A disconnected click must not inflate a compact Part into a near-full-frame rectangle.
        // Continuous sampled strokes naturally overlap the current local surface as they cross its edge.
        if (polarity == MaskPolarity.Add && region.Intersect(part.Bounds).IsEmpty) return;

        var after = part.CopyMaskWithTransparentOutside(region);
        var value = polarity == MaskPolarity.Add ? (byte)255 : (byte)0;
        var radiusSquared = Radius * Radius;
        for (var y = region.Y; y < region.Bottom; y++)
        for (var x = region.X; x < region.Right; x++)
        {
            var centerX = x + 0.5;
            var centerY = y + 0.5;
            if (!samples.Any(sample =>
                    (centerX - sample.X) * (centerX - sample.X)
                    + (centerY - sample.Y) * (centerY - sample.Y) <= radiusSquared)) continue;
            after[(y - region.Y) * region.Width + x - region.X] = value;
        }
        transaction.Apply(new MaskPatch(part.Id, region, after));
    }

    private DocumentRect DabBounds(DocumentPoint point)
    {
        var document = _session.Document!;
        var left = Math.Clamp((int)Math.Floor(point.X - Radius), 0, document.Dimensions.Width);
        var top = Math.Clamp((int)Math.Floor(point.Y - Radius), 0, document.Dimensions.Height);
        var right = Math.Clamp((int)Math.Ceiling(point.X + Radius), 0, document.Dimensions.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(point.Y + Radius), 0, document.Dimensions.Height);
        return right <= left || bottom <= top ? default : new(left, top, right - left, bottom - top);
    }

    private MaskPolarity EffectivePolarity(CanvasModifiers modifiers) =>
        modifiers.HasFlag(CanvasModifiers.Alt)
            ? PrimaryPolarity == MaskPolarity.Add ? MaskPolarity.Erase : MaskPolarity.Add
            : PrimaryPolarity;

    private void ClearStroke()
    {
        _transaction = null;
        _part = null;
        _sampler = null;
        State = MaskBrushState.Idle;
    }

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
