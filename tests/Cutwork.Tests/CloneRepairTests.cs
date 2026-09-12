using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class CloneRepairTests
{
    [TestMethod]
    public void AltClickStoresDocumentSourceAndViewportChangesDoNotMoveIt()
    {
        var (session, tool) = CreateTool(12, 10);
        tool.PointerDown(new(2.25, 3.75), 1, CanvasModifiers.Alt);
        var source = tool.Snapshot().SourceAnchor;

        session.Viewport.Fit(session.Document!.Dimensions, new(600, 400));
        session.Viewport.PanBy(31, -17);
        session.Viewport.ZoomAt(new(100, 80), 2.5);

        Assert.AreEqual(new DocumentPoint(2.25, 3.75), source);
        Assert.AreEqual(source, tool.Snapshot().SourceAnchor);
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsFalse(session.IsDirty);
    }

    [TestMethod]
    public void SourceDoesNotCrossDocumentBoundary()
    {
        var (session, tool) = CreateTool(12, 10);
        tool.PointerDown(new(2, 3), 1, CanvasModifiers.Alt);
        session.Open(new CutworkDocument(Original(8, 8)));

        Assert.IsNull(tool.Snapshot().SourceAnchor);
        tool.PointerDown(new(4, 4), 1, CanvasModifiers.None);
        Assert.AreEqual(CloneRepairMessage.SourceRequired, tool.Status.Message);
        Assert.AreEqual(0, session.Document!.Layers.OfType<RepairLayer>().Count());
    }

    [TestMethod]
    public void StrokeOffsetIsFixedAndSecondStrokeRecomputesIt()
    {
        var session = OpenSession(20, 20);
        var kernel = new RecordingCloneKernel();
        var tool = new CloneRepairController(session, kernel);
        tool.Activate();
        tool.PointerDown(new(4, 5), 1, CanvasModifiers.Alt);

        tool.PointerDown(new(10, 12), 1, CanvasModifiers.None);
        tool.PointerMove(new(14, 15), CanvasModifiers.None);
        Assert.IsTrue(kernel.Offsets.All(value => value == new DocumentPoint(-6, -7)));
        tool.PointerUp(new(14, 15), CanvasPointerButton.Left, CanvasModifiers.None);

        kernel.Offsets.Clear();
        tool.PointerDown(new(8, 9), 1, CanvasModifiers.None);
        tool.PointerMove(new(11, 10), CanvasModifiers.None);
        Assert.IsTrue(kernel.Offsets.All(value => value == new DocumentPoint(-4, -4)));
        Assert.AreEqual(new DocumentPoint(4, 5), tool.Snapshot().SourceAnchor);
        tool.PointerUp(new(11, 10), CanvasPointerButton.Left, CanvasModifiers.None);
    }

    [TestMethod]
    public void KernelUsesCircularFootprintAndExactOriginalMapping()
    {
        var original = Original(7, 7);
        var repair = new RepairLayer(new(0, 0, 7, 7), new byte[7 * 7 * 4]);
        var result = new CloneRepairKernel().CreatePatch(original, repair,
            [new DocumentPoint(3.5, 3.5)], new(-2, -1), 1);

        Assert.AreEqual(new DocumentRect(2, 2, 3, 3), result.Region);
        AssertPixel(result, 3, 3, original.PixelAt(1, 2));
        AssertPixel(result, 2, 3, original.PixelAt(0, 2));
        AssertPixel(result, 3, 2, original.PixelAt(1, 1));
        AssertPixel(result, 2, 2, new byte[4]);
        AssertPixel(result, 4, 4, new byte[4]);
    }

    [TestMethod]
    public void KernelClipsSourceAndDestinationBoundaries()
    {
        var original = Original(5, 4);
        var repair = new RepairLayer(new(0, 0, 1, 1), new byte[4]);
        var result = new CloneRepairKernel().CreatePatch(original, repair,
            [new DocumentPoint(0.25, 0.25)], new(-1, -1), 2);

        Assert.AreEqual(new DocumentRect(0, 0, 3, 3), result.Region);
        AssertPixel(result, 0, 0, new byte[4]);
        AssertPixel(result, 1, 1, original.PixelAt(0, 0));
    }

    [TestMethod]
    public void KernelAlwaysReadsImmutableOriginalRatherThanEarlierRepairPixels()
    {
        var original = Original(6, 2);
        var existing = Enumerable.Repeat((byte)231, 6 * 2 * 4).ToArray();
        var repair = new RepairLayer(new(0, 0, 6, 2), existing);
        var beforeOriginal = original.CopyPixelBytes();
        var result = new CloneRepairKernel().CreatePatch(original, repair,
            [new DocumentPoint(3.5, 0.5), new DocumentPoint(4.5, 0.5)], new(-3, 0), 0.5);

        AssertPixel(result, 3, 0, original.PixelAt(0, 0));
        AssertPixel(result, 4, 0, original.PixelAt(1, 0));
        CollectionAssert.AreEqual(beforeOriginal, original.CopyPixelBytes());
    }

    [TestMethod]
    public void KernelRejectsAnUnbatchedSampleSet()
    {
        var original = Original(20, 20);
        var repair = new RepairLayer(new(0, 0, 20, 20), new byte[20 * 20 * 4]);
        var samples = Enumerable.Range(0, StrokeSampler.MaximumBatchSamples + 1)
            .Select(index => new DocumentPoint(index + .5, index + .5)).ToArray();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CloneRepairKernel().CreatePatch(original, repair, samples, new(0, 0), 1));
    }

    [TestMethod]
    public void WheelUsesCloneRadiusPrecedenceAndCtrlWheelOnlyZooms()
    {
        var (session, tool) = CreateTool(20, 20);
        var router = new CanvasInputRouter(session);
        router.SetActiveTool(tool);
        session.Viewport.Fit(session.Document!.Dimensions, new(200, 200));
        var zoom = session.Viewport.Zoom;

        router.Wheel(new(100, 100), 120, CanvasModifiers.None);
        Assert.AreEqual(11, tool.Radius);
        Assert.AreEqual(zoom, session.Viewport.Zoom);
        router.Wheel(new(100, 100), -120, CanvasModifiers.None);
        Assert.AreEqual(12, tool.Radius);
        Assert.AreEqual(zoom, session.Viewport.Zoom);
        router.Wheel(new(100, 100), 120, CanvasModifiers.Control);
        Assert.AreEqual(12, tool.Radius);
        Assert.AreNotEqual(zoom, session.Viewport.Zoom);

        tool.SetRadius(CloneRepairController.MinimumRadius);
        tool.Wheel(1, CanvasModifiers.None);
        Assert.AreEqual(CloneRepairController.MinimumRadius, tool.Radius);
        tool.SetRadius(CloneRepairController.MaximumRadius);
        tool.Wheel(-1, CanvasModifiers.None);
        Assert.AreEqual(CloneRepairController.MaximumRadius, tool.Radius);
    }

    [TestMethod]
    public void OneStrokeCreatesOneRepairAndUndoRedoRestoresIdentityBytesAndBounds()
    {
        var (session, tool) = CreateTool(24, 16);
        tool.SetRadius(1.5);
        tool.PointerDown(new(3.5, 3.5), 1, CanvasModifiers.Alt);
        tool.PointerDown(new(12.5, 8.5), 1, CanvasModifiers.None);
        tool.PointerMove(new(16.5, 8.5), CanvasModifiers.None);
        tool.PointerUp(new(16.5, 8.5), CanvasPointerButton.Left, CanvasModifiers.None);

        Assert.AreEqual(1, session.UndoCount);
        var repair = session.Document!.Layers.OfType<RepairLayer>().Single();
        var id = repair.Id;
        var bounds = repair.Bounds;
        var pixels = repair.CopyPixels(bounds);
        Assert.AreEqual(id, session.SelectedLayerId);

        session.Undo();
        Assert.IsFalse(session.Document.Layers.Any(layer => layer.Id == id));
        session.Redo();
        var restored = (RepairLayer)session.Document.GetLayer(id);
        Assert.AreEqual(bounds, restored.Bounds);
        CollectionAssert.AreEqual(pixels, restored.CopyPixels(bounds));
        Assert.AreEqual(1, session.UndoCount);
    }

    [TestMethod]
    public void EscapeAndLostCaptureRestoreExactExistingRepairBytesAndBounds()
    {
        VerifyCancellation((tool) => tool.KeyDown(CanvasToolKey.Escape, CanvasModifiers.None));
        VerifyCancellation((tool) => tool.LostPointerCapture());
    }

    [TestMethod]
    public void CancellingNewRepairLeavesNoLayerOrAuthoredRevision()
    {
        var (session, tool) = CreateTool(30, 20);
        session.MarkSaved();
        tool.PointerDown(new(3, 3), 1, CanvasModifiers.Alt);
        tool.PointerDown(new(15, 10), 1, CanvasModifiers.None);
        tool.PointerMove(new(18, 10), CanvasModifiers.None);
        tool.KeyDown(CanvasToolKey.Escape, CanvasModifiers.None);

        Assert.AreEqual(0, session.Document!.Layers.OfType<RepairLayer>().Count());
        Assert.AreEqual(0, session.UndoCount);
        Assert.AreEqual(session.SavedRevision, session.CurrentRevision);
        Assert.IsFalse(session.IsDirty);
    }

    [TestMethod]
    public void EmptyMinimumRadiusDabDoesNotCreateBlankRepairLayer()
    {
        var (session, tool) = CreateTool(10, 10);
        tool.SetRadius(CloneRepairController.MinimumRadius);
        tool.PointerDown(new(1, 1), 1, CanvasModifiers.Alt);
        tool.PointerDown(new(5, 5), 1, CanvasModifiers.None);
        tool.PointerUp(new(5, 5), CanvasPointerButton.Left, CanvasModifiers.None);

        Assert.AreEqual(0, session.Document!.Layers.OfType<RepairLayer>().Count());
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsFalse(session.IsDirty);
    }

    [TestMethod]
    public void ClonePublishesLocalDirtyRegionsAndHistoryScalesWithRoi()
    {
        var (session, tool) = CreateTool(1000, 800);
        tool.SetRadius(2);
        var dirty = default(DocumentRect);
        session.Document!.Changed += (_, change) => dirty = dirty.Union(change.DirtyRegion);
        tool.PointerDown(new(10.5, 10.5), 1, CanvasModifiers.Alt);
        tool.PointerDown(new(500.5, 400.5), 1, CanvasModifiers.None);
        tool.PointerMove(new(504.5, 400.5), CanvasModifiers.None);
        tool.PointerUp(new(504.5, 400.5), CanvasPointerButton.Left, CanvasModifiers.None);

        Assert.IsTrue(new DocumentRect(498, 398, 9, 5).Contains(dirty));
        Assert.IsTrue(session.HistoryBytes < 10_000,
            $"Expected ROI history, retained {session.HistoryBytes} bytes.");
    }

    [TestMethod]
    public void SourceAndCursorMovementAreOverlayOnly()
    {
        var (session, tool) = CreateTool(20, 20);
        var revision = session.Document!.Revision;
        var current = session.CurrentRevision;
        tool.PointerDown(new(4, 4), 1, CanvasModifiers.Alt);
        tool.PointerMove(new(8, 9), CanvasModifiers.None);
        tool.Wheel(1, CanvasModifiers.None);

        Assert.AreEqual(revision, session.Document.Revision);
        Assert.AreEqual(current, session.CurrentRevision);
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsFalse(session.IsDirty);
    }

    [TestMethod]
    public void ProjectedRadiusUsesDocumentZoom()
    {
        var session = OpenSession(10, 10);
        session.Viewport.Fit(session.Document!.Dimensions, new(100, 100));
        var radius = 7.0;
        var projectedDiameter = radius * 2 * session.Viewport.Projection.ScaleX;
        Assert.IsTrue(projectedDiameter > 0);
        session.Viewport.ZoomAt(new(50, 50), 2);
        Assert.AreEqual(projectedDiameter * 2,
            radius * 2 * session.Viewport.Projection.ScaleX, 1e-9);
    }

    [TestMethod]
    public void LongSparseDiagonalReachesKernelOnlyInBoundedBatches()
    {
        var session = OpenSession(256, 256);
        var kernel = new RecordingCloneKernel();
        var tool = new CloneRepairController(session, kernel);
        tool.Activate();
        tool.SetRadius(2);
        tool.PointerDown(new(4.5, 4.5), 1, CanvasModifiers.Alt);

        tool.PointerDown(new(12.5, 12.5), 1, CanvasModifiers.None);
        kernel.BatchSizes.Clear();
        tool.PointerMove(new(243.5, 243.5), CanvasModifiers.None);
        tool.PointerUp(new(243.5, 243.5), CanvasPointerButton.Left, CanvasModifiers.None);

        Assert.IsTrue(kernel.BatchSizes.Count > 1);
        Assert.IsTrue(kernel.BatchSizes.All(size => size is > 0
            && size <= StrokeSampler.MaximumBatchSamples));
        Assert.AreEqual(1, session.UndoCount);
    }

    [TestMethod]
    public void SparseAndDenseCloneInputProduceExactSameRepair()
    {
        var sparse = CreateTool(64, 64);
        var dense = CreateTool(64, 64);
        sparse.Tool.SetRadius(2);
        dense.Tool.SetRadius(2);
        sparse.Tool.PointerDown(new(3.5, 3.5), 1, CanvasModifiers.Alt);
        dense.Tool.PointerDown(new(3.5, 3.5), 1, CanvasModifiers.Alt);

        sparse.Tool.PointerDown(new(12.5, 12.5), 1, CanvasModifiers.None);
        sparse.Tool.PointerMove(new(52.5, 52.5), CanvasModifiers.None);
        sparse.Tool.PointerUp(new(52.5, 52.5), CanvasPointerButton.Left, CanvasModifiers.None);

        dense.Tool.PointerDown(new(12.5, 12.5), 1, CanvasModifiers.None);
        for (var coordinate = 16.5; coordinate < 52.5; coordinate += 4)
            dense.Tool.PointerMove(new(coordinate, coordinate), CanvasModifiers.None);
        dense.Tool.PointerUp(new(52.5, 52.5), CanvasPointerButton.Left, CanvasModifiers.None);

        var sparseRepair = sparse.Session.Document!.Layers.OfType<RepairLayer>().Single();
        var denseRepair = dense.Session.Document!.Layers.OfType<RepairLayer>().Single();
        Assert.AreEqual(sparseRepair.Bounds, denseRepair.Bounds);
        CollectionAssert.AreEqual(sparseRepair.CopyPixels(sparseRepair.Bounds),
            denseRepair.CopyPixels(denseRepair.Bounds));
        Assert.AreEqual(1, sparse.Session.UndoCount);
        Assert.AreEqual(1, dense.Session.UndoCount);
    }

    private static void VerifyCancellation(Func<CloneRepairController, CanvasInputEffects> cancel)
    {
        var session = OpenSession(30, 20);
        var initialBounds = new DocumentRect(10, 8, 2, 2);
        var initial = Enumerable.Range(0, 16).Select(value => (byte)(value + 1)).ToArray();
        var repair = new RepairLayer(initialBounds, initial);
        session.Execute(new AddLayer(repair));
        session.MarkSaved();
        session.SelectLayer(repair.Id);
        var history = session.UndoCount;
        var tool = new CloneRepairController(session, new CloneRepairKernel());
        tool.Activate();
        tool.SetRadius(3);
        tool.PointerDown(new(3, 3), 1, CanvasModifiers.Alt);
        tool.PointerDown(new(10.5, 8.5), 1, CanvasModifiers.None);
        tool.PointerMove(new(17.5, 8.5), CanvasModifiers.None);
        cancel(tool);

        Assert.AreEqual(CloneRepairState.Idle, tool.State);
        Assert.AreEqual(initialBounds, repair.Bounds);
        CollectionAssert.AreEqual(initial, repair.CopyPixels(initialBounds));
        Assert.AreEqual(history, session.UndoCount);
        Assert.IsFalse(session.IsDirty);
    }

    private static (EditorSession Session, CloneRepairController Tool) CreateTool(int width, int height)
    {
        var session = OpenSession(width, height);
        var tool = new CloneRepairController(session, new CloneRepairKernel());
        tool.Activate();
        return (session, tool);
    }

    private static EditorSession OpenSession(int width, int height)
    {
        var session = new EditorSession();
        session.Open(new CutworkDocument(Original(width, height)));
        return session;
    }

    private static OriginalAsset Original(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)(x % 251);
            pixels[offset + 1] = (byte)(y % 251);
            pixels[offset + 2] = (byte)((x * 17 + y * 31) % 251);
            pixels[offset + 3] = 255;
        }
        return new("clone-fixture", new(width, height), width * 4, pixels);
    }

    private static void AssertPixel(CloneRepairPatch patch, int x, int y, ReadOnlySpan<byte> expected)
    {
        var offset = ((y - patch.Region.Y) * patch.Region.Width + x - patch.Region.X) * 4;
        CollectionAssert.AreEqual(expected.ToArray(), patch.StraightBgra.Span.Slice(offset, 4).ToArray());
    }

    private sealed class RecordingCloneKernel : ICloneRepairKernel
    {
        public List<DocumentPoint> Offsets { get; } = [];
        public List<int> BatchSizes { get; } = [];
        public CloneRepairPatch CreatePatch(OriginalAsset original, RepairLayer target,
            IReadOnlyList<DocumentPoint> destinationSamples, DocumentPoint offset, double radius)
        {
            Offsets.Add(offset);
            BatchSizes.Add(destinationSamples.Count);
            var point = destinationSamples[0];
            var x = Math.Clamp((int)Math.Floor(point.X), 0, original.Dimensions.Width - 1);
            var y = Math.Clamp((int)Math.Floor(point.Y), 0, original.Dimensions.Height - 1);
            return new(new(x, y, 1, 1), original.PixelAt(x, y).ToArray());
        }
    }
}
