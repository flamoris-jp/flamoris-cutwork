using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class PartToolTests
{
    [TestMethod]
    public void WorkflowTransitionsThroughDrawingFittingCommitAndIdle()
    {
        var (session, tool) = CreateTool();
        tool.Activate();
        Assert.AreEqual(PartToolState.Idle, tool.State);

        AddRectangle(tool);
        Assert.AreEqual(PartToolState.DrawingFence, tool.State);
        Assert.IsTrue(tool.FinalizeFence());
        Assert.AreEqual(PartToolState.FittingPreview, tool.State);

        var part = tool.Commit();

        Assert.IsNotNull(part);
        Assert.AreEqual(PartToolState.Idle, tool.State);
        Assert.AreEqual(part.Id, session.SelectedLayerId);
    }

    [TestMethod]
    public void FewerThanThreePointsAreRejectedWithoutAuthoredChanges()
    {
        var (session, tool) = CreateTool();
        var document = session.Document!;
        tool.PointerDown(new(2, 2), 1, CanvasModifiers.None);
        tool.PointerDown(new(8, 2), 1, CanvasModifiers.None);

        Assert.IsFalse(tool.FinalizeFence());

        Assert.AreEqual(PartToolState.DrawingFence, tool.State);
        Assert.AreEqual(PartToolMessage.NeedsThreePoints, tool.Status.Message);
        Assert.AreEqual(1, document.Layers.Count);
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsFalse(session.IsDirty);
    }

    [TestMethod]
    public void CancelBeforeCommitLeavesDocumentHistoryAndSavedStateUntouched()
    {
        var (session, tool) = CreateTool();
        var document = session.Document!;
        session.MarkSaved();
        var documentRevision = document.Revision;
        var currentRevision = session.CurrentRevision;
        var savedRevision = session.SavedRevision;
        AddRectangle(tool);
        Assert.IsTrue(tool.FinalizeFence());

        tool.Cancel();

        Assert.AreEqual(PartToolState.Idle, tool.State);
        Assert.AreEqual(1, document.Layers.Count);
        Assert.AreEqual(documentRevision, document.Revision);
        Assert.AreEqual(currentRevision, session.CurrentRevision);
        Assert.AreEqual(savedRevision, session.SavedRevision);
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsFalse(session.IsDirty);
        Assert.AreEqual(0, tool.Snapshot().Fence.Count);
        Assert.IsTrue(tool.Snapshot().Mask.IsEmpty);
    }

    [TestMethod]
    public void RouterKeepsFenceGeometryIndependentFromZoomAndPan()
    {
        var (session, tool) = CreateTool();
        var router = new CanvasInputRouter(session);
        router.SetActiveTool(tool);
        session.Viewport.ZoomAt(new(0, 0), 2);
        session.Viewport.PanBy(10, 20);
        router.PointerDown(new(new(14, 24), CanvasPointerButton.Left, 1, CanvasModifiers.None));
        var authored = tool.Snapshot().Fence[0];

        session.Viewport.ZoomAt(new(14, 24), 1.5);
        session.Viewport.PanBy(-7, 3);

        Assert.AreEqual(new DocumentPoint(2, 2), authored);
        Assert.AreEqual(authored, tool.Snapshot().Fence[0]);
    }

    [TestMethod]
    public void EnterDoubleClickEscapeAndWheelUseOneControllerBoundary()
    {
        var (session, tool) = CreateTool();
        var router = new CanvasInputRouter(session);
        router.SetActiveTool(tool);
        router.PointerDown(new(new(2, 2), CanvasPointerButton.Left, 1, CanvasModifiers.None));
        router.PointerDown(new(new(10, 2), CanvasPointerButton.Left, 1, CanvasModifiers.None));
        router.PointerDown(new(new(10, 10), CanvasPointerButton.Left, 2, CanvasModifiers.None));
        Assert.AreEqual(PartToolState.FittingPreview, tool.State);
        var maskBefore = tool.Snapshot().Mask.ToArray();
        var zoomBefore = session.Viewport.Zoom;

        router.Wheel(new(5, 5), 120, CanvasModifiers.None);
        Assert.AreEqual(1, tool.Snapshot().Step);
        Assert.IsTrue(tool.Snapshot().Mask.ToArray().Count(value => value != 0)
            < maskBefore.Count(value => value != 0));
        router.Wheel(new(5, 5), -120, CanvasModifiers.None);
        CollectionAssert.AreEqual(maskBefore, tool.Snapshot().Mask.ToArray());
        router.Wheel(new(5, 5), 120, CanvasModifiers.Control);
        Assert.IsTrue(session.Viewport.Zoom > zoomBefore);

        router.KeyDown(CanvasToolKey.Escape, CanvasModifiers.None);
        Assert.AreEqual(PartToolState.Idle, tool.State);
        Assert.AreEqual(0, session.UndoCount);
    }

    [TestMethod]
    public void EnterFinalizesFenceAndThenCommitsOnePart()
    {
        var (session, tool) = CreateTool();
        var router = new CanvasInputRouter(session);
        router.SetActiveTool(tool);
        router.PointerDown(new(new(2, 2), CanvasPointerButton.Left, 1, CanvasModifiers.None));
        router.PointerDown(new(new(10, 2), CanvasPointerButton.Left, 1, CanvasModifiers.None));
        router.PointerDown(new(new(10, 10), CanvasPointerButton.Left, 1, CanvasModifiers.None));

        router.KeyDown(CanvasToolKey.Enter, CanvasModifiers.None);
        Assert.AreEqual(PartToolState.FittingPreview, tool.State);
        router.KeyDown(CanvasToolKey.Enter, CanvasModifiers.None);

        Assert.AreEqual(PartToolState.Idle, tool.State);
        Assert.AreEqual(1, session.Document!.Layers.OfType<PartLayer>().Count());
        Assert.AreEqual(1, session.UndoCount);
    }

    [TestMethod]
    public void CommitIsOneLocalTransactionAndUndoRedoPreserveIdentityMaskAndOriginal()
    {
        var (session, tool) = CreateTool(useProductionFitter: true);
        var document = session.Document!;
        var original = document.Original.CopyPixelBytes();
        DocumentChange? change = null;
        document.Changed += (_, value) => change = value;
        AddRectangle(tool);
        Assert.IsTrue(tool.FinalizeFence());
        tool.Wheel(1, CanvasModifiers.None);

        var part = tool.Commit()!;
        var expectedMask = part.CopyMask(part.Bounds);
        var expectedBounds = part.Bounds;

        Assert.AreEqual(2, document.Layers.Count);
        Assert.AreEqual(1, session.UndoCount);
        Assert.AreEqual(part.Id, session.SelectedLayerId);
        Assert.AreEqual(expectedBounds, change!.DirtyRegion);
        Assert.AreEqual(expectedBounds, change.HoleRegion);
        Assert.IsTrue(expectedBounds.Width < document.Dimensions.Width);
        CollectionAssert.AreEqual(original, document.Original.CopyPixelBytes());
        using (var cache = new CompositeCache(document))
        {
            cache.RenderPending();
            var holeMask = cache.CopyHoleMask();
            for (var y = expectedBounds.Y; y < expectedBounds.Bottom; y++)
                for (var x = expectedBounds.X; x < expectedBounds.Right; x++)
                    Assert.AreEqual(expectedMask[(y - expectedBounds.Y) * expectedBounds.Width + x - expectedBounds.X],
                        holeMask[y * document.Dimensions.Width + x]);

            session.Undo();
            cache.RenderPending();
            Assert.IsTrue(cache.CopyHoleMask().All(value => value == 0));
            session.Redo();
            cache.RenderPending();
            CollectionAssert.AreEqual(holeMask, cache.CopyHoleMask());
        }

        var restored = (PartLayer)document.GetLayer(part.Id);
        Assert.AreSame(part, restored);
        CollectionAssert.AreEqual(expectedMask, restored.CopyMask(restored.Bounds));
        Assert.AreEqual(part.Id, session.SelectedLayerId);
        CollectionAssert.AreEqual(original, document.Original.CopyPixelBytes());
    }

    [TestMethod]
    public void PendingPreviewDoesNotAdvanceDocumentOrHistory()
    {
        var (session, tool) = CreateTool();
        var document = session.Document!;
        var revision = document.Revision;
        AddRectangle(tool);
        tool.FinalizeFence();
        tool.Wheel(2, CanvasModifiers.None);

        Assert.AreEqual(revision, document.Revision);
        Assert.AreEqual(0, session.CurrentRevision);
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsFalse(session.IsDirty);
    }

    private static (EditorSession Session, PartToolController Tool) CreateTool(bool useProductionFitter = false)
    {
        var pixels = Enumerable.Repeat(new byte[] { 80, 100, 120, 255 }, 20 * 20)
            .SelectMany(pixel => pixel).ToArray();
        var session = new EditorSession();
        session.Open(new CutworkDocument(new OriginalAsset("fixture.png", new(20, 20), 80, pixels)));
        IPartBoundaryFitter fitter = useProductionFitter ? new GuriguriPartFitter() : new FixedFitter();
        return (session, new PartToolController(session, fitter));
    }

    private static void AddRectangle(PartToolController tool)
    {
        tool.PointerDown(new(2, 2), 1, CanvasModifiers.None);
        tool.PointerDown(new(10, 2), 1, CanvasModifiers.None);
        tool.PointerDown(new(10, 10), 1, CanvasModifiers.None);
        tool.PointerDown(new(2, 10), 1, CanvasModifiers.None);
    }

    private sealed class FixedFitter : IPartBoundaryFitter
    {
        public IPartFittingSession Create(OriginalAsset original, IReadOnlyList<DocumentPoint> fence) => new FixedSession();
    }

    private sealed class FixedSession : IPartFittingSession
    {
        private readonly byte[] _loose = Enumerable.Repeat((byte)255, 64).ToArray();
        public DocumentRect Bounds => new(2, 2, 8, 8);
        public int PolygonPixelCount => 64;
        public int Step { get; private set; }
        public int CurrentKeepPixels => Math.Max(16, 64 - Step * 8);
        public byte[] CopyCurrentMask()
        {
            var result = _loose.ToArray();
            Array.Clear(result, 0, result.Length - CurrentKeepPixels);
            return result;
        }
        public byte[] Adjust(int wheelSteps)
        {
            Step = Math.Max(0, Step + wheelSteps);
            return CopyCurrentMask();
        }
    }
}
