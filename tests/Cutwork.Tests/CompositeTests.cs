using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class CompositeTests
{
    private static EditorSession OpenOpaque()
    {
        var s = new EditorSession();
        s.Open(new CutworkDocument(new OriginalAsset("fixture", new PixelSize(2, 1), 8,
            new byte[] { 10, 20, 30, 255, 50, 60, 70, 255 })));
        return s;
    }

    [TestMethod]
    public void HiddenPartLeavesHoleAndRevealsUnderpaint()
    {
        var s = OpenOpaque(); var original = s.Document!.Original.CopyPixelBytes();
        var roi = new DocumentRect(0, 0, 1, 1);
        var part = new PartLayer(roi, new byte[] { 255 });
        var repair = new RepairLayer(roi, new byte[] { 200, 100, 50, 255 });
        using var cache = new CompositeCache(s.Document);
        s.Execute(new AddLayer(part), new AddLayer(repair)); cache.RenderPending();
        CollectionAssert.AreEqual(original, cache.CopyPixels());
        var baseRevision = s.Document.Base.Revision; var holes = cache.HolePixelCount;
        s.Execute(new SetLayerVisibility(part.Id, false)); cache.RenderPending();
        CollectionAssert.AreEqual(new byte[] { 200, 100, 50, 255, 50, 60, 70, 255 }, cache.CopyPixels());
        Assert.AreEqual(baseRevision, s.Document.Base.Revision);
        Assert.AreEqual(holes, cache.HolePixelCount);
        s.Execute(new SetLayerVisibility(repair.Id, false)); cache.RenderPending();
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 50, 60, 70, 255 }, cache.CopyPixels());
        CollectionAssert.AreEqual(original, s.Document.Original.CopyPixelBytes());
        s.Execute(new DeleteLayer(part.Id)); cache.RenderPending();
        CollectionAssert.AreEqual(original, cache.CopyPixels());
    }

    [TestMethod]
    public void SourceOverIsDeterministicPremultipliedBottomToTop()
    {
        var s = OpenOpaque(); var roi = new DocumentRect(0, 0, 1, 1);
        var top = new PatchLayer(roi, new byte[] { 0, 0, 255, 128 });
        var bottom = new RepairLayer(roi, new byte[] { 255, 0, 0, 128 });
        s.Execute(new SetLayerVisibility(s.Document!.Base.Id, false), new AddLayer(top), new AddLayer(bottom));
        using var cache = new CompositeCache(s.Document); cache.RenderPending();
        CollectionAssert.AreEqual(new byte[] { 64, 0, 128, 192, 0, 0, 0, 0 }, cache.CopyPixels());
        s.Execute(new ReorderLayer(bottom.Id, 1)); cache.RenderPending();
        CollectionAssert.AreEqual(new byte[] { 128, 0, 64, 192, 0, 0, 0, 0 }, cache.CopyPixels());
        using var fresh = new CompositeCache(s.Document); fresh.RenderPending();
        CollectionAssert.AreEqual(fresh.CopyPixels(), cache.CopyPixels());
    }

    [TestMethod]
    public void SoftMaskUnionUsesMaximumRegardlessOfVisibility()
    {
        var s = OpenOpaque(); var roi = new DocumentRect(0, 0, 1, 1);
        var a = new PartLayer(roi, new byte[] { 128 }); var b = new PartLayer(roi, new byte[] { 64 });
        s.Execute(new AddLayer(a), new AddLayer(b), new SetLayerVisibility(a.Id, false), new SetLayerVisibility(b.Id, false));
        using var cache = new CompositeCache(s.Document!); cache.RenderPending();
        CollectionAssert.AreEqual(new byte[] { 5, 10, 15, 127, 50, 60, 70, 255 }, cache.CopyPixels());
    }

    [TestMethod]
    public void RoiChangeInvalidatesOnlyThatRegionAndBaseOnlyForMaskEdits()
    {
        var s = EditHistoryTests.Open(); var part = EditHistoryTests.Part(); var repair = EditHistoryTests.Repair();
        s.Execute(new AddLayer(part), new AddLayer(repair));
        using var cache = new CompositeCache(s.Document!); cache.RenderPending();
        var count = cache.CompositedPixelCount; var holes = cache.HolePixelCount;
        var baseRevision = s.Document!.Base.Revision; var repairRevision = repair.Revision;
        var roi = new DocumentRect(1, 1, 1, 1);
        s.Execute(new MaskPatch(part.Id, roi, new byte[] { 100 }));
        Assert.AreEqual(roi, cache.PendingRegion);
        Assert.IsTrue(s.Document.Base.Revision > baseRevision);
        Assert.AreEqual(repairRevision, repair.Revision);
        Assert.AreEqual(roi, cache.RenderPending()!.Region);
        Assert.AreEqual(count + 1, cache.CompositedPixelCount);
        Assert.AreEqual(holes + 1, cache.HolePixelCount);
        baseRevision = s.Document.Base.Revision;
        s.Execute(new RasterPatch(repair.Id, roi, new byte[] { 1, 2, 3, 255 })); cache.RenderPending();
        Assert.AreEqual(count + 2, cache.CompositedPixelCount);
        Assert.AreEqual(holes + 1, cache.HolePixelCount);
        Assert.AreEqual(baseRevision, s.Document.Base.Revision);
        s.Execute(new RenameLayer(part.Id, "metadata only"));
        Assert.IsNull(cache.RenderPending()); Assert.AreEqual(count + 2, cache.CompositedPixelCount);
    }

    [TestMethod]
    public void PendingChangesCoalesceAndUndoRedoMatchesFreshComposition()
    {
        var s = EditHistoryTests.Open(); var part = EditHistoryTests.Part(); var repair = EditHistoryTests.Repair();
        s.Execute(new AddLayer(part), new AddLayer(repair));
        using var cache = new CompositeCache(s.Document!); cache.RenderPending();
        s.Execute(new MaskPatch(part.Id, new DocumentRect(0, 0, 1, 1), new byte[] { 77 }),
            new RasterPatch(repair.Id, new DocumentRect(2, 1, 1, 1), new byte[] { 10, 20, 30, 200 }));
        Assert.AreEqual(new DocumentRect(0, 0, 3, 2), cache.PendingRegion);
        cache.RenderPending(); var after = cache.CopyPixels();
        s.Undo(); cache.RenderPending();
        s.Redo(); cache.RenderPending();
        CollectionAssert.AreEqual(after, cache.CopyPixels());
        using var fresh = new CompositeCache(s.Document!); fresh.RenderPending();
        CollectionAssert.AreEqual(fresh.CopyPixels(), cache.CopyPixels());
    }

    [TestMethod]
    public void CancelledLiveGestureInvalidatesAndRestoresComposite()
    {
        var s = OpenOpaque(); var part = new PartLayer(new DocumentRect(0, 0, 1, 1), new byte[] { 255 });
        s.Execute(new AddLayer(part), new SetLayerVisibility(part.Id, false));
        using var cache = new CompositeCache(s.Document!); cache.RenderPending(); var before = cache.CopyPixels();
        using (var gesture = s.BeginTransaction())
        {
            gesture.Apply(new MaskPatch(part.Id, part.Bounds, new byte[] { 0 })); cache.RenderPending();
            Assert.AreNotEqual(before[3], cache.CopyPixels()[3]);
        }
        cache.RenderPending(); CollectionAssert.AreEqual(before, cache.CopyPixels());
    }
}
