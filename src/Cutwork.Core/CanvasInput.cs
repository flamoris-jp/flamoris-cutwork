namespace Flamoris.Cutwork.Core;

[Flags]
public enum CanvasModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Space = 8,
}

public enum CanvasPointerButton
{
    None,
    Left,
    Middle,
    Right,
}

public enum CanvasToolKey
{
    Enter,
    Escape,
}

[Flags]
public enum CanvasInputEffects
{
    None = 0,
    Handled = 1,
    ViewportChanged = 2,
    ToolOverlayChanged = 4,
    CapturePointer = 8,
    ReleasePointer = 16,
}

public readonly record struct CanvasPointerInput(
    ViewportPoint Position,
    CanvasPointerButton Button,
    int ClickCount,
    CanvasModifiers Modifiers);

/// <summary>
/// The narrow input surface implemented by the current production tool. It receives
/// document coordinates only; WPF coordinates and viewport mutation stay in the router.
/// </summary>
public interface ICanvasToolInput
{
    void Activate();
    void Deactivate();
    CanvasInputEffects PointerDown(DocumentPoint point, int clickCount, CanvasModifiers modifiers);
    CanvasInputEffects PointerMove(DocumentPoint? point, CanvasModifiers modifiers);
    CanvasInputEffects PointerUp(DocumentPoint? point, CanvasPointerButton button, CanvasModifiers modifiers);
    CanvasInputEffects Wheel(int steps, CanvasModifiers modifiers);
    CanvasInputEffects KeyDown(CanvasToolKey key, CanvasModifiers modifiers);
    CanvasInputEffects LostPointerCapture();
    void Cancel();
}

/// <summary>
/// Optional bounded continuation surface for tools whose current pointer event left sampled
/// authoring work pending. The UI pumps one slice at a time through CanvasInputRouter.
/// </summary>
public interface ICanvasDeferredWork
{
    bool HasPendingWork { get; }
    CanvasInputEffects ProcessPendingWork();
}
