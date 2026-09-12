using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class MaskBrushTests
{
    [TestMethod]
    public void AddStrokeUsesCircularLocalFootprintAndOneHistoryEntry()
    {
        var (session, part, tool) = Create(maskValue: 0);
        tool.SetRadius(2);

        tool.PointerDown(new(5, 5), 1, CanvasModifiers.None);
        tool.PointerUp(new(5, 5), CanvasPointerButton.Left, CanvasModifiers.None);

        Assert.AreEqual(1, session.UndoCount);
        Assert.AreEqual((byte)255, part.MaskAt(4, 4));
        Assert.AreEqual((byte)255, part.MaskAt(5, 5));
        Assert.AreEqual((byte)0, part.MaskAt(3, 3));
        Assert.AreEqual((byte)0, part.MaskAt(7, 7));
    }

    [TestMethod]
    public void SparseStrokeHasNoHolesAndPublishesOnlyLocalDirtyRegion()
    {
        var (session, part, tool) = Create(maskValue: 0);
        tool.SetRadius(1);
        DocumentChange? latest = null;
        session.Document!.Changed += (_, change) => latest = change;

        tool.PointerDown(new(2, 5), 1, CanvasModifiers.None);
        tool.PointerMove(new(10, 5), CanvasModifiers.None);
        tool.PointerUp(new(10, 5), CanvasPointerButton.Left, CanvasModifiers.None);

        for (var x = 2; x < 10; x++) Assert.AreEqual((byte)255, part.MaskAt(x, 4));
        Assert.IsNotNull(latest);
        Assert.IsTrue(part.Bounds.Contains(latest.DirtyRegion));
        Assert.IsTrue(latest.DirtyRegion.Width < session.Document.Dimensions.Width);
    }

    [TestMethod]
    public void EraseAndTemporaryAltInverseRestorePrimaryAfterRelease()
    {
        var (_, part, tool) = Create(maskValue: 255);
        tool.SetRadius(1);
        tool.SetPrimaryPolarity(MaskPolarity.Erase);

        Stroke(tool, new(3, 3), CanvasModifiers.None);
        Stroke(tool, new(6, 3), CanvasModifiers.Alt);
        Stroke(tool, new(9, 3), CanvasModifiers.None);

        Assert.AreEqual((byte)0, part.MaskAt(2, 2));
        Assert.AreEqual((byte)255, part.MaskAt(5, 2));
        Assert.AreEqual((byte)0, part.MaskAt(8, 2));
        Assert.AreEqual(MaskPolarity.Erase, tool.PrimaryPolarity);
    }

    [TestMethod]
    public void CancelAndLostCaptureRestoreExactBeforeWithoutHistory()
    {
        var (session, part, tool) = Create(maskValue: 0);
        var before = part.CopyMask(part.Bounds);
        session.MarkSaved();

        tool.PointerDown(new(4, 4), 1, CanvasModifiers.None);
        tool.PointerMove(new(8, 4), CanvasModifiers.None);
        tool.LostPointerCapture();

        CollectionAssert.AreEqual(before, part.CopyMask(part.Bounds));
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsFalse(session.IsDirty);
        Assert.AreEqual(MaskBrushState.Idle, tool.State);
    }

    [TestMethod]
    public void UndoRedoRestoreExactMaskHoleAndOriginal()
    {
        var (session, part, tool) = Create(maskValue: 0);
        var original = session.Document!.Original.CopyPixelBytes();
        var before = part.CopyMask(part.Bounds);
        Stroke(tool, new(5, 5), CanvasModifiers.None);
        var after = part.CopyMask(part.Bounds);
        using var cache = new CompositeCache(session.Document);
        cache.RenderPending();
        var holes = cache.CopyHoleMask();

        session.Undo(); cache.RenderPending();
        CollectionAssert.AreEqual(before, part.CopyMask(part.Bounds));
        Assert.IsTrue(cache.CopyHoleMask().All(value => value == 0));
        session.Redo(); cache.RenderPending();
        CollectionAssert.AreEqual(after, part.CopyMask(part.Bounds));
        CollectionAssert.AreEqual(holes, cache.CopyHoleMask());
        CollectionAssert.AreEqual(original, session.Document.Original.CopyPixelBytes());
    }

    [TestMethod]
    public void NonPartSelectionDoesNotStartAStroke()
    {
        var (session, _, tool) = Create(maskValue: 0);
        session.SelectLayer(session.Document!.Base.Id);

        var effect = tool.PointerDown(new(5, 5), 1, CanvasModifiers.None);

        Assert.AreEqual(MaskBrushState.Idle, tool.State);
        Assert.AreEqual(MaskBrushMessage.SelectPart, tool.Status.Message);
        Assert.IsFalse(effect.HasFlag(CanvasInputEffects.CapturePointer));
        Assert.AreEqual(0, session.UndoCount);
    }

    [TestMethod]
    public void RadiusAndAuthoredMaskStayDocumentSpaceAcrossViewportChanges()
    {
        var (session, part, tool) = Create(maskValue: 0);
        tool.SetRadius(3);
        session.Viewport.ZoomAt(new(0, 0), 2);
        var projectedRadius = tool.Radius * session.Viewport.Projection.ScaleX;
        session.Viewport.PanBy(20, -5);
        session.Viewport.ZoomAt(new(20, 10), 2);

        Assert.AreEqual(6, projectedRadius);
        Assert.AreEqual(3, tool.Radius);
        Assert.IsTrue(part.CopyMask(part.Bounds).All(value => value == 0));
    }

    private static void Stroke(MaskBrushController tool, DocumentPoint point, CanvasModifiers modifiers)
    {
        tool.PointerDown(point, 1, modifiers);
        tool.PointerUp(point, CanvasPointerButton.Left, modifiers);
    }

    private static (EditorSession Session, PartLayer Part, MaskBrushController Tool) Create(byte maskValue)
    {
        var pixels = Enumerable.Repeat(new byte[] { 20, 40, 60, 255 }, 12 * 12).SelectMany(x => x).ToArray();
        var session = new EditorSession();
        session.Open(new CutworkDocument(new OriginalAsset("fixture.png", new(12, 12), 48, pixels)));
        var bounds = new DocumentRect(1, 1, 10, 10);
        var part = new PartLayer(bounds, Enumerable.Repeat(maskValue, 100).ToArray());
        session.Execute(new AddLayer(part));
        // Fixture setup is not part of the gesture history under test.
        session.Open(session.Document!);
        session.MarkSaved();
        session.SelectLayer(part.Id);
        var tool = new MaskBrushController(session);
        tool.Activate();
        return (session, part, tool);
    }
}
