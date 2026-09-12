using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class RepairFinishingTests
{
    [TestMethod]
    public void OnlySelectedRepairLayerIsEligible()
    {
        var (session, repair, tool) = Create(RepairFinishingKind.Blur);
        Assert.IsTrue(tool.PointerDown(new(4.5, 4.5), 1, CanvasModifiers.None)
            .HasFlag(CanvasInputEffects.CapturePointer));
        tool.Cancel();

        var part = new PartLayer(new(1, 1, 3, 3), new byte[9]);
        var patch = new PatchLayer(new(1, 1, 3, 3), new byte[36]);
        session.Execute(new AddLayer(part));
        session.Execute(new AddLayer(patch));
        session.Open(session.Document!);
        foreach (var rejected in new Layer[] { session.Document!.Base, part, patch })
        {
            session.SelectLayer(rejected.Id);
            var before = repair.CopyPixels(repair.Bounds);
            var revision = session.CurrentRevision;
            var effect = tool.PointerDown(new(4.5, 4.5), 1, CanvasModifiers.None);
            Assert.IsFalse(effect.HasFlag(CanvasInputEffects.CapturePointer));
            Assert.AreEqual(RepairFinishingMessage.SelectRepair, tool.Message);
            CollectionAssert.AreEqual(before, repair.CopyPixels(repair.Bounds));
            Assert.AreEqual(revision, session.CurrentRevision);
            Assert.AreEqual(0, session.UndoCount);
        }
    }

    [TestMethod]
    public void BlurUsesCircularFootprintAndOnePixelReadHalo()
    {
        var pixels = new byte[5 * 5 * 4];
        SetPixel(pixels, 5, 2, 2, 255, 255, 255, 255);
        var repair = new RepairLayer(new(0, 0, 5, 5), pixels);
        var result = new RepairFinishingKernel().Blur(repair, new(2.5, 2.5), .75, 1, new(5, 5));

        Assert.AreEqual(new DocumentRect(1, 1, 3, 3), result.Region);
        AssertPixel(result, 2, 2, 28, 28, 28, 28);
        AssertPixel(result, 1, 1, 0, 0, 0, 0);
        AssertPixel(result, 3, 3, 0, 0, 0, 0);

        var adjacent = new RepairFinishingKernel().Blur(repair, new(1.5, 2.5), .75, 1, new(5, 5));
        AssertPixel(adjacent, 1, 2, 28, 28, 28, 28);
    }

    [TestMethod]
    public void BlurIsDeterministicLocalAndLeavesOriginalAndOtherLayerUntouched()
    {
        var first = Create(RepairFinishingKind.Blur);
        var second = Create(RepairFinishingKind.Blur);
        var original = first.Session.Document!.Original.CopyPixelBytes();
        var unrelated = new RepairLayer(new(0, 0, 12, 10), Solid(12, 10, 77));
        first.Session.Execute(new AddLayer(unrelated));
        first.Session.Open(first.Session.Document);
        first.Session.SelectLayer(first.Repair.Id);
        first.Tool.SetRadius(1);
        second.Tool.SetRadius(1);
        var unrelatedBefore = unrelated.CopyPixels(unrelated.Bounds);
        var outsideBefore = first.Repair.PixelAt(0, 0).ToArray();

        Stroke(first.Tool, new(5.5, 5.5), new(7.5, 5.5));
        Stroke(second.Tool, new(5.5, 5.5), new(7.5, 5.5));

        CollectionAssert.AreEqual(first.Repair.CopyPixels(first.Repair.Bounds),
            second.Repair.CopyPixels(second.Repair.Bounds));
        CollectionAssert.AreEqual(outsideBefore, first.Repair.PixelAt(0, 0).ToArray());
        CollectionAssert.AreEqual(unrelatedBefore, unrelated.CopyPixels(unrelated.Bounds));
        CollectionAssert.AreEqual(original, first.Session.Document.Original.CopyPixelBytes());
    }

    [TestMethod]
    public void SmudgeFollowsDirectionAndReversingDirectionChangesResult()
    {
        var pixels = HorizontalGradient(6, 3);
        var forward = new RepairLayer(new(0, 0, 6, 3), pixels);
        var reverse = new RepairLayer(new(0, 0, 6, 3), pixels);
        var kernel = new RepairFinishingKernel();

        var forwardPatch = kernel.Smudge(forward, new(1.5, 1.5), new(2.5, 1.5), .75, 1, new(6, 3));
        var reversePatch = kernel.Smudge(reverse, new(2.5, 1.5), new(1.5, 1.5), .75, 1, new(6, 3));

        AssertPixel(forwardPatch, 2, 1, 20, 20, 20, 255);
        AssertPixel(reversePatch, 1, 1, 40, 40, 40, 255);
        CollectionAssert.AreNotEqual(forwardPatch.StraightBgra.ToArray(), reversePatch.StraightBgra.ToArray());

        var subpixel = kernel.Smudge(forward, new(2, 1.5), new(2.5, 1.5), .75, 1, new(6, 3));
        AssertPixel(subpixel, 2, 1, 30, 30, 30, 255);
    }

    [TestMethod]
    [DataRow(RepairFinishingKind.Blur)]
    [DataRow(RepairFinishingKind.Smudge)]
    public void SamplingIsStableAcrossSparseAndDensePointerEvents(RepairFinishingKind kind)
    {
        var sparse = Create(kind);
        var dense = Create(kind);
        sparse.Tool.SetRadius(1);
        dense.Tool.SetRadius(1);

        Stroke(sparse.Tool, new(2.5, 5.5), new(8.5, 5.5));
        dense.Tool.PointerDown(new(2.5, 5.5), 1, CanvasModifiers.None);
        for (var x = 3; x <= 8; x++) dense.Tool.PointerMove(new(x + .5, 5.5), CanvasModifiers.None);
        dense.Tool.PointerUp(new(8.5, 5.5), CanvasPointerButton.Left, CanvasModifiers.None);

        CollectionAssert.AreEqual(sparse.Repair.CopyPixels(sparse.Repair.Bounds),
            dense.Repair.CopyPixels(dense.Repair.Bounds));
    }

    [TestMethod]
    public void SmudgeStrokeIsLocalAndLeavesOriginalAndUnrelatedLayerUntouched()
    {
        var (session, repair, tool) = Create(RepairFinishingKind.Smudge);
        var unrelated = new RepairLayer(new(0, 0, 12, 10), Solid(12, 10, 91));
        session.Execute(new AddLayer(unrelated));
        session.Open(session.Document!);
        session.SelectLayer(repair.Id);
        tool.SetRadius(1);
        tool.SetStrength(.75);
        var original = session.Document!.Original.CopyPixelBytes();
        var unrelatedBefore = unrelated.CopyPixels(unrelated.Bounds);
        var outsideBefore = repair.PixelAt(0, 0).ToArray();

        Stroke(tool, new(3.5, 5.5), new(7.5, 5.5));

        CollectionAssert.AreEqual(outsideBefore, repair.PixelAt(0, 0).ToArray());
        CollectionAssert.AreEqual(unrelatedBefore, unrelated.CopyPixels(unrelated.Bounds));
        CollectionAssert.AreEqual(original, session.Document.Original.CopyPixelBytes());
        Assert.AreEqual(1, session.UndoCount);
    }

    [TestMethod]
    public void RadiusStrengthAndWheelStayWithinDocumentSpaceBounds()
    {
        var (session, _, tool) = Create(RepairFinishingKind.Blur);
        AssertOutOfRange(() => tool.SetRadius(.49));
        AssertOutOfRange(() => tool.SetStrength(0));
        AssertOutOfRange(() => tool.SetStrength(1.01));
        tool.SetRadius(12);
        tool.SetStrength(.35);
        var router = new CanvasInputRouter(session);
        router.SetActiveTool(tool);
        session.Viewport.Fit(session.Document!.Dimensions, new(200, 200));
        var zoom = session.Viewport.Zoom;

        router.Wheel(new(100, 100), 120, CanvasModifiers.None);
        Assert.AreEqual(11, tool.Radius);
        Assert.AreEqual(zoom, session.Viewport.Zoom);
        router.Wheel(new(100, 100), -120, CanvasModifiers.None);
        Assert.AreEqual(12, tool.Radius);
        router.Wheel(new(100, 100), 120, CanvasModifiers.Control);
        Assert.AreEqual(12, tool.Radius);
        Assert.AreNotEqual(zoom, session.Viewport.Zoom);
        Assert.AreEqual(.35, tool.Strength);

        tool.SetRadius(RepairFinishingController.MinimumRadius);
        tool.Wheel(1, CanvasModifiers.None);
        Assert.AreEqual(RepairFinishingController.MinimumRadius, tool.Radius);
        tool.SetRadius(RepairFinishingController.MaximumRadius);
        tool.Wheel(-1, CanvasModifiers.None);
        Assert.AreEqual(RepairFinishingController.MaximumRadius, tool.Radius);
    }

    [TestMethod]
    public void OneStrokeIsOneTransactionAndUndoRedoRestoreExactBytes()
    {
        var (session, repair, tool) = Create(RepairFinishingKind.Blur);
        tool.SetRadius(1);
        var before = repair.CopyPixels(repair.Bounds);
        Stroke(tool, new(.5, 4.5), new(4.5, 4.5));
        var after = repair.CopyPixels(repair.Bounds);

        Assert.AreEqual(1, session.UndoCount);
        CollectionAssert.AreNotEqual(before, after);
        session.Undo();
        CollectionAssert.AreEqual(before, repair.CopyPixels(repair.Bounds));
        session.Redo();
        CollectionAssert.AreEqual(after, repair.CopyPixels(repair.Bounds));
        Assert.AreEqual(1, session.UndoCount);
    }

    [TestMethod]
    public void EscapeAndLostCaptureRestoreExactBytesWithoutHistory()
    {
        VerifyCancel(tool => tool.KeyDown(CanvasToolKey.Escape, CanvasModifiers.None));
        VerifyCancel(tool => tool.LostPointerCapture());
    }

    [TestMethod]
    public void LocalDirtyAndHistoryCostScaleWithStrokeRoi()
    {
        var (session, repair, tool) = Create(RepairFinishingKind.Blur, 1000, 800,
            new DocumentRect(480, 380, 40, 40));
        tool.SetRadius(2);
        var dirty = default(DocumentRect);
        session.Document!.Changed += (_, change) => dirty = dirty.Union(change.DirtyRegion);
        using var cache = new CompositeCache(session.Document);
        cache.RenderPending();
        var compositedBefore = cache.CompositedPixelCount;

        Stroke(tool, new(495.5, 395.5), new(503.5, 395.5));
        var update = cache.RenderPending();

        Assert.IsTrue(repair.Bounds.Contains(dirty));
        Assert.IsTrue(dirty.Width < 20 && dirty.Height < 10, $"Unexpected dirty region: {dirty}");
        Assert.AreEqual(dirty, update!.Region);
        Assert.IsTrue(cache.CompositedPixelCount - compositedBefore < 200);
        Assert.IsTrue(session.HistoryBytes < 20_000,
            $"Expected ROI history, retained {session.HistoryBytes} bytes.");
    }

    [TestMethod]
    public void HoverRadiusAndViewportChangesAreOverlayOnlyState()
    {
        var (session, repair, tool) = Create(RepairFinishingKind.Smudge);
        var bytes = repair.CopyPixels(repair.Bounds);
        var revision = session.CurrentRevision;
        tool.SetRadius(7);
        tool.PointerMove(new(5, 4), CanvasModifiers.None);
        session.Viewport.Fit(session.Document!.Dimensions, new(240, 200));
        var diameter = tool.Radius * 2 * session.Viewport.Projection.ScaleX;
        session.Viewport.ZoomAt(new(100, 80), 2);

        Assert.AreEqual(diameter * 2, tool.Radius * 2 * session.Viewport.Projection.ScaleX, 1e-9);
        Assert.AreEqual(revision, session.CurrentRevision);
        Assert.AreEqual(0, session.UndoCount);
        CollectionAssert.AreEqual(bytes, repair.CopyPixels(repair.Bounds));
    }

    [TestMethod]
    [DataRow(RepairFinishingKind.Blur)]
    [DataRow(RepairFinishingKind.Smudge)]
    public void LongSparseDiagonalCoalescesKernelWorkIntoBoundedPublications(
        RepairFinishingKind kind)
    {
        var kernel = new RecordingFinishingKernel();
        var (session, _, tool) = Create(kind, 128, 128, kernel: kernel);
        tool.SetRadius(1);
        tool.PointerDown(new(4.5, 4.5), 1, CanvasModifiers.None);
        kernel.CallCount = 0;
        var publications = 0;
        session.Document!.Changed += (_, _) => publications++;

        tool.PointerMove(new(123.5, 123.5), CanvasModifiers.None);
        Assert.AreEqual(StrokeSampler.MaximumBatchSamples, kernel.CallCount);
        tool.PointerUp(new(123.5, 123.5), CanvasPointerButton.Left, CanvasModifiers.None);
        Assert.AreEqual(StrokeSampler.MaximumBatchSamples * 2, kernel.CallCount);
        while (tool.HasPendingWork)
        {
            var before = kernel.CallCount;
            tool.ProcessPendingWork();
            Assert.IsTrue(kernel.CallCount - before <= StrokeSampler.MaximumBatchSamples);
        }

        Assert.IsTrue(kernel.CallCount > StrokeSampler.MaximumBatchSamples);
        Assert.IsTrue(publications < kernel.CallCount,
            $"Expected batched publication, saw {publications} for {kernel.CallCount} kernel calls.");
        Assert.IsTrue(publications <= (kernel.CallCount + StrokeSampler.MaximumBatchSamples - 1)
            / StrokeSampler.MaximumBatchSamples + 1);
        Assert.AreEqual(1, session.UndoCount);
    }

    private static void VerifyCancel(Func<RepairFinishingController, CanvasInputEffects> cancel)
    {
        var (session, repair, tool) = Create(RepairFinishingKind.Blur);
        tool.SetRadius(1);
        var before = repair.CopyPixels(repair.Bounds);
        session.MarkSaved();
        tool.PointerDown(new(.5, 4.5), 1, CanvasModifiers.None);
        tool.PointerMove(new(10.5, 4.5), CanvasModifiers.None);
        Assert.IsTrue(tool.HasPendingWork);
        Assert.IsTrue(session.IsDirty);
        cancel(tool);

        Assert.AreEqual(RepairFinishingState.Idle, tool.State);
        CollectionAssert.AreEqual(before, repair.CopyPixels(repair.Bounds));
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsFalse(session.IsDirty);
    }

    private static void Stroke(RepairFinishingController tool, DocumentPoint from, DocumentPoint to)
    {
        tool.PointerDown(from, 1, CanvasModifiers.None);
        tool.PointerMove(to, CanvasModifiers.None);
        tool.PointerUp(to, CanvasPointerButton.Left, CanvasModifiers.None);
        DrainPending(tool);
    }

    private static void DrainPending(ICanvasDeferredWork tool)
    {
        var slices = 0;
        while (tool.HasPendingWork)
        {
            tool.ProcessPendingWork();
            Assert.IsTrue(++slices < 10_000, "Deferred tool work did not converge.");
        }
    }

    private static void AssertOutOfRange(Action action)
    {
        try
        {
            action();
            Assert.Fail("Expected ArgumentOutOfRangeException.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private static (EditorSession Session, RepairLayer Repair, RepairFinishingController Tool) Create(
        RepairFinishingKind kind, int width = 12, int height = 10,
        DocumentRect? repairBounds = null, IRepairFinishingKernel? kernel = null)
    {
        var bounds = repairBounds ?? new DocumentRect(0, 0, width, height);
        var originalPixels = HorizontalGradient(width, height);
        var repairPixels = HorizontalGradient(bounds.Width, bounds.Height);
        var session = new EditorSession();
        session.Open(new CutworkDocument(new OriginalAsset("finishing-fixture", new(width, height),
            width * 4, originalPixels)));
        var repair = new RepairLayer(bounds, repairPixels);
        session.Execute(new AddLayer(repair));
        session.Open(session.Document!);
        session.MarkSaved();
        session.SelectLayer(repair.Id);
        var tool = new RepairFinishingController(session, kernel ?? new RepairFinishingKernel(), kind);
        tool.Activate();
        return (session, repair, tool);
    }

    private static byte[] HorizontalGradient(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++) SetPixel(pixels, width, x, y,
            (byte)(x * 20 % 251), (byte)(x * 20 % 251), (byte)(x * 20 % 251), 255);
        return pixels;
    }

    private static byte[] Solid(int width, int height, byte value) =>
        Enumerable.Repeat(new[] { value, value, value, (byte)255 }, width * height)
            .SelectMany(pixel => pixel).ToArray();

    private static void SetPixel(byte[] pixels, int width, int x, int y,
        byte b, byte g, byte r, byte a)
    {
        var offset = (y * width + x) * 4;
        pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r; pixels[offset + 3] = a;
    }

    private static void AssertPixel(RepairFinishingPatch patch, int x, int y,
        byte b, byte g, byte r, byte a)
    {
        var offset = ((y - patch.Region.Y) * patch.Region.Width + x - patch.Region.X) * 4;
        CollectionAssert.AreEqual(new[] { b, g, r, a },
            patch.StraightBgra.Span.Slice(offset, 4).ToArray());
    }

    private sealed class RecordingFinishingKernel : IRepairFinishingKernel
    {
        public int CallCount { get; set; }

        public RepairFinishingPatch Blur(RepairLayer target, DocumentPoint center,
            double radius, double strength, PixelSize documentSize) => Patch(target, center);

        public RepairFinishingPatch Smudge(RepairLayer target, DocumentPoint from, DocumentPoint to,
            double radius, double strength, PixelSize documentSize) => Patch(target, to);

        private RepairFinishingPatch Patch(RepairLayer target, DocumentPoint point)
        {
            CallCount++;
            var x = Math.Clamp((int)Math.Floor(point.X), target.Bounds.X, target.Bounds.Right - 1);
            var y = Math.Clamp((int)Math.Floor(point.Y), target.Bounds.Y, target.Bounds.Bottom - 1);
            var region = new DocumentRect(x, y, 1, 1);
            var pixels = target.CopyPixels(region);
            pixels[0] ^= 1;
            return new(region, pixels);
        }
    }
}
