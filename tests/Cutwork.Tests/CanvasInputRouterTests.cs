using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class CanvasInputRouterTests
{
    [TestMethod]
    public void PointerIsConvertedOnceToDocumentSpaceBeforeToolRouting()
    {
        var (session, router, tool) = CreateRouter();
        session.Viewport.ZoomAt(new ViewportPoint(0, 0), 2);
        session.Viewport.PanBy(10, 20);

        var result = router.PointerDown(new(
            new ViewportPoint(34, 52), CanvasPointerButton.Left, 1, CanvasModifiers.None));

        Assert.IsTrue(result.HasFlag(CanvasInputEffects.Handled));
        Assert.AreEqual(new DocumentPoint(12, 16), tool.LastPoint);
    }

    [TestMethod]
    public void MiddleDragPansWithoutReachingTool()
    {
        var (session, router, tool) = CreateRouter();
        var down = router.PointerDown(new(
            new ViewportPoint(20, 30), CanvasPointerButton.Middle, 1, CanvasModifiers.None));
        var move = router.PointerMove(new ViewportPoint(33, 25), CanvasModifiers.None);
        var up = router.PointerUp(new(
            new ViewportPoint(33, 25), CanvasPointerButton.Middle, 1, CanvasModifiers.None));

        Assert.IsTrue(down.HasFlag(CanvasInputEffects.CapturePointer));
        Assert.IsTrue(move.HasFlag(CanvasInputEffects.ViewportChanged));
        Assert.IsTrue(up.HasFlag(CanvasInputEffects.ReleasePointer));
        Assert.AreEqual(13, session.Viewport.PanX);
        Assert.AreEqual(-5, session.Viewport.PanY);
        Assert.AreEqual(0, tool.PointerDownCount);
    }

    [TestMethod]
    public void PlainWheelMapsPhysicalForwardToPositiveToolStepsAndControlWheelAlwaysZooms()
    {
        var (session, router, tool) = CreateRouter();
        tool.HandleWheel = true;

        // WPF reports the physical forward rotation as a positive delta.
        var plain = router.Wheel(new ViewportPoint(50, 50), 120, CanvasModifiers.None);
        var zoomBefore = session.Viewport.Zoom;
        var control = router.Wheel(new ViewportPoint(50, 50), 120, CanvasModifiers.Control);

        Assert.IsTrue(plain.HasFlag(CanvasInputEffects.ToolOverlayChanged));
        Assert.AreEqual(1, tool.WheelCount);
        Assert.AreEqual(1, tool.LastWheelSteps);
        Assert.IsTrue(control.HasFlag(CanvasInputEffects.ViewportChanged));
        Assert.IsTrue(session.Viewport.Zoom > zoomBefore);
        Assert.AreEqual(1, tool.WheelCount);
    }

    private static (EditorSession Session, CanvasInputRouter Router, RecordingTool Tool) CreateRouter()
    {
        var session = new EditorSession();
        session.Open(new CutworkDocument(new OriginalAsset(
            "input.png", new PixelSize(100, 80), 400, new byte[100 * 80 * 4])));
        var router = new CanvasInputRouter(session);
        var tool = new RecordingTool();
        router.SetActiveTool(tool);
        return (session, router, tool);
    }

    private sealed class RecordingTool : ICanvasToolInput
    {
        public bool HandleWheel { get; set; }
        public int PointerDownCount { get; private set; }
        public int WheelCount { get; private set; }
        public int LastWheelSteps { get; private set; }
        public DocumentPoint? LastPoint { get; private set; }
        public void Activate() { }
        public void Deactivate() { }
        public CanvasInputEffects PointerDown(DocumentPoint point, int clickCount, CanvasModifiers modifiers)
        {
            PointerDownCount++;
            LastPoint = point;
            return CanvasInputEffects.ToolOverlayChanged;
        }
        public CanvasInputEffects PointerMove(DocumentPoint? point, CanvasModifiers modifiers) => CanvasInputEffects.None;
        public CanvasInputEffects PointerUp(DocumentPoint? point, CanvasPointerButton button, CanvasModifiers modifiers) => CanvasInputEffects.None;
        public CanvasInputEffects Wheel(int steps, CanvasModifiers modifiers)
        {
            WheelCount++;
            LastWheelSteps = steps;
            return HandleWheel ? CanvasInputEffects.Handled | CanvasInputEffects.ToolOverlayChanged : CanvasInputEffects.None;
        }
        public CanvasInputEffects KeyDown(CanvasToolKey key, CanvasModifiers modifiers) => CanvasInputEffects.None;
        public CanvasInputEffects LostPointerCapture() => CanvasInputEffects.None;
        public void Cancel() { }
    }
}
