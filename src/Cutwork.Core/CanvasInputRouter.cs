namespace Flamoris.Cutwork.Core;

/// <summary>
/// Central authority for canvas navigation and active-tool input precedence.
/// </summary>
public sealed class CanvasInputRouter
{
    private readonly EditorSession _session;
    private ICanvasToolInput? _activeTool;
    private ViewportPoint _lastPanPoint;
    private CanvasPointerButton _panButton;

    public CanvasInputRouter(EditorSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public ICanvasToolInput? ActiveTool => _activeTool;
    public bool IsPanning => _panButton != CanvasPointerButton.None;

    public void SetActiveTool(ICanvasToolInput? tool)
    {
        if (ReferenceEquals(_activeTool, tool)) return;
        _activeTool?.Deactivate();
        _activeTool = tool;
        _activeTool?.Activate();
    }

    public CanvasInputEffects PointerDown(CanvasPointerInput input)
    {
        if (_session.Document is null) return CanvasInputEffects.None;

        var panButton = input.Button == CanvasPointerButton.Middle
            ? CanvasPointerButton.Middle
            : input.Button == CanvasPointerButton.Left && input.Modifiers.HasFlag(CanvasModifiers.Space)
                ? CanvasPointerButton.Left
                : CanvasPointerButton.None;
        if (panButton != CanvasPointerButton.None)
        {
            _lastPanPoint = input.Position;
            _panButton = panButton;
            return CanvasInputEffects.Handled | CanvasInputEffects.CapturePointer;
        }

        if (input.Button != CanvasPointerButton.Left || _activeTool is null)
            return CanvasInputEffects.None;

        var point = ToDocumentPoint(input.Position);
        return point is null
            ? CanvasInputEffects.None
            : _activeTool.PointerDown(point.Value, Math.Max(1, input.ClickCount), input.Modifiers)
                | CanvasInputEffects.Handled;
    }

    public CanvasInputEffects PointerMove(ViewportPoint position, CanvasModifiers modifiers)
    {
        if (IsPanning)
        {
            _session.Viewport.PanBy(position.X - _lastPanPoint.X, position.Y - _lastPanPoint.Y);
            _lastPanPoint = position;
            return CanvasInputEffects.Handled | CanvasInputEffects.ViewportChanged;
        }

        return _activeTool?.PointerMove(ToDocumentPoint(position), modifiers) ?? CanvasInputEffects.None;
    }

    public CanvasInputEffects PointerUp(CanvasPointerInput input)
    {
        if (IsPanning && input.Button == _panButton)
        {
            _panButton = CanvasPointerButton.None;
            return CanvasInputEffects.Handled | CanvasInputEffects.ReleasePointer;
        }

        return _activeTool?.PointerUp(ToDocumentPoint(input.Position), input.Button, input.Modifiers)
            ?? CanvasInputEffects.None;
    }

    public CanvasInputEffects LostPointerCapture()
    {
        if (!IsPanning) return CanvasInputEffects.None;
        _panButton = CanvasPointerButton.None;
        return CanvasInputEffects.ReleasePointer;
    }

    public CanvasInputEffects Wheel(ViewportPoint position, int delta, CanvasModifiers modifiers)
    {
        if (_session.Document is null || delta == 0) return CanvasInputEffects.None;
        var steps = Math.Sign(delta) * Math.Max(1, (int)Math.Round(Math.Abs(delta) / 120.0));

        // Ctrl+wheel is globally reserved for viewport zoom. A tool may consume
        // plain wheel; otherwise plain wheel retains normal canvas zoom.
        if (!modifiers.HasFlag(CanvasModifiers.Control) && _activeTool is not null)
        {
            var result = _activeTool.Wheel(steps, modifiers);
            if (result.HasFlag(CanvasInputEffects.Handled)) return result;
        }

        _session.Viewport.ZoomAt(position, Math.Pow(1.0015, delta));
        return CanvasInputEffects.Handled | CanvasInputEffects.ViewportChanged;
    }

    public CanvasInputEffects KeyDown(CanvasToolKey key, CanvasModifiers modifiers) =>
        _activeTool?.KeyDown(key, modifiers) ?? CanvasInputEffects.None;

    public void CancelActiveTool() => _activeTool?.Cancel();

    public DocumentPoint? ToDocumentPoint(ViewportPoint position)
    {
        if (_session.Document is not { } document) return null;
        var point = _session.Viewport.ViewportToDocument(position);
        return point.X >= 0 && point.Y >= 0
            && point.X < document.Dimensions.Width && point.Y < document.Dimensions.Height
                ? point
                : null;
    }
}
