using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class EditHistoryTests
{
    internal static EditorSession Open(long budget = 128 * 1024 * 1024)
    {
        var session = new EditorSession(budget);
        session.Open(new CutworkDocument(new OriginalAsset("fixture", new PixelSize(4, 3), 16, new byte[48])));
        return session;
    }
    internal static PartLayer Part(int x = 0) => new(new DocumentRect(x, 0, 2, 2), new byte[] { 255, 255, 255, 255 });
    internal static RepairLayer Repair() => new(new DocumentRect(0, 0, 4, 3), new byte[48]);
    internal static void Reject(EditError error, Action action)
    {
        try { action(); Assert.Fail("Expected a rejected edit."); }
        catch (EditException exception) { Assert.AreEqual(error, exception.Error); }
    }

    [TestMethod]
    public void BaseDeleteReorderAndDuplicateBaseAreRejected()
    {
        var s = Open(); var b = s.Document!.Base;
        Reject(EditError.BaseFixed, () => s.Execute(new DeleteLayer(b.Id)));
        Reject(EditError.BaseFixed, () => s.Execute(new ReorderLayer(b.Id, 0)));
        Reject(EditError.BaseFixed, () => s.Execute(new AddLayer(b)));
        Assert.AreEqual(1, s.Document.Layers.Count);
        Assert.AreEqual(0, s.UndoCount);
        Assert.IsFalse(s.IsDirty);
    }

    [TestMethod]
    public void BandsPermitInternalReorderAndRejectCrossing()
    {
        var s = Open(); var a = Part(); var b = Part(1); var repair = Repair();
        var patch = new PatchLayer(new DocumentRect(0, 0, 1, 1), new byte[4]);
        s.Execute(new AddLayer(a), new AddLayer(b), new AddLayer(repair), new AddLayer(patch));
        s.Execute(new ReorderLayer(a.Id, 0), new ReorderLayer(patch.Id, 3));
        CollectionAssert.AreEqual(new[] { a.Id, b.Id, s.Document!.Base.Id, patch.Id, repair.Id },
            s.Document.Layers.Select(layer => layer.Id).ToArray());
        Reject(EditError.BandCrossing, () => s.Execute(new ReorderLayer(a.Id, 3)));
        Reject(EditError.BandCrossing, () => s.Execute(new ReorderLayer(repair.Id, 0)));
    }

    [TestMethod]
    public void StructuralTransactionRestoresIdentityOrderAndSelection()
    {
        var s = Open(); var a = Part(); var b = Part(1);
        s.Execute(new AddLayer(a), new AddLayer(b));
        Assert.AreEqual(1, s.UndoCount);
        s.SelectLayer(a.Id);
        s.Execute(new DeleteLayer(a.Id), new ReorderLayer(b.Id, 0));
        Assert.AreEqual(s.Document!.Base.Id, s.SelectedLayerId);
        s.Undo();
        Assert.AreSame(a, s.Document.GetLayer(a.Id));
        Assert.AreEqual(a.Id, s.SelectedLayerId);
        s.Redo();
        Assert.AreEqual(2, s.Document.Layers.Count);
        s.Undo(); s.Undo();
        Assert.AreEqual(1, s.Document.Layers.Count);
        s.Redo();
        CollectionAssert.AreEqual(new[] { b.Id, a.Id, s.Document.Base.Id },
            s.Document.Layers.Select(layer => layer.Id).ToArray());
    }

    [TestMethod]
    public void ValueTransactionAndSavedStateUseStateIdentityNotCacheRevision()
    {
        var s = Open(); var b = s.Document!.Base;
        s.Execute(new RenameLayer(b.Id, "base name"), new SetLayerVisibility(b.Id, false));
        s.MarkSaved(); var saved = s.CurrentRevision; var cacheRevision = s.Document.Revision;
        s.Execute(new RenameLayer(b.Id, "later"));
        Assert.IsTrue(s.IsDirty);
        s.Undo();
        Assert.IsFalse(s.IsDirty);
        Assert.AreEqual(saved, s.CurrentRevision);
        Assert.IsTrue(s.Document.Revision > cacheRevision);
        s.Undo(); Assert.IsTrue(s.IsDirty);
        Assert.AreEqual("", b.Name); Assert.IsTrue(b.Visible);
        s.Redo(); Assert.IsFalse(s.IsDirty);
        Assert.AreEqual("base name", b.Name); Assert.IsFalse(b.Visible);
        s.Undo();
        s.Execute(new RenameLayer(b.Id, "new branch"));
        Assert.IsTrue(s.IsDirty); Assert.IsFalse(s.CanRedo);
        Assert.AreNotEqual(saved, s.CurrentRevision);
    }

    [TestMethod]
    public void MaskAndRasterPatchesRestoreExactRoiBytesOnUndoAndRedo()
    {
        var s = Open(); var part = Part(); var repair = Repair();
        s.Execute(new AddLayer(part), new AddLayer(repair));
        var original = s.Document!.Original.CopyPixelBytes();
        var maskBefore = part.CopyMask(part.Bounds); var rasterBefore = repair.CopyPixels(repair.Bounds);
        var roi = new DocumentRect(1, 1, 1, 1);
        var rasterAfter = new byte[] { 23, 45, 67, 128 };
        s.Execute(new MaskPatch(part.Id, roi, new byte[] { 91 }), new RasterPatch(repair.Id, roi, rasterAfter));
        rasterAfter[0] = 99;
        Assert.AreEqual((byte)91, part.MaskAt(1, 1));
        CollectionAssert.AreEqual(new byte[] { 23, 45, 67, 128 }, repair.CopyPixels(roi));
        var expectedMask = part.CopyMask(part.Bounds); var expectedRaster = repair.CopyPixels(repair.Bounds);
        s.Undo();
        CollectionAssert.AreEqual(maskBefore, part.CopyMask(part.Bounds));
        CollectionAssert.AreEqual(rasterBefore, repair.CopyPixels(repair.Bounds));
        s.Redo();
        CollectionAssert.AreEqual(expectedMask, part.CopyMask(part.Bounds));
        CollectionAssert.AreEqual(expectedRaster, repair.CopyPixels(repair.Bounds));
        CollectionAssert.AreEqual(original, s.Document.Original.CopyPixelBytes());
    }

    [TestMethod]
    public void FailedCompositeTransactionRestoresEarlierEditsAndPreservesRedo()
    {
        var s = Open(); var b = s.Document!.Base;
        s.Execute(new RenameLayer(b.Id, "saved redo")); s.Undo();
        Reject(EditError.BaseFixed, () => s.Execute(new RenameLayer(b.Id, "temporary"), new DeleteLayer(b.Id)));
        Assert.AreEqual("", b.Name); Assert.IsFalse(s.IsDirty); Assert.IsTrue(s.CanRedo);
        s.Redo(); Assert.AreEqual("saved redo", b.Name);
    }

    [TestMethod]
    public void GestureCancelRestoresOverlappingPatchesAndCommitCreatesOneEntry()
    {
        var s = Open(); var part = Part(); s.Execute(new AddLayer(part)); s.MarkSaved();
        var before = part.CopyMask(part.Bounds); var roi = new DocumentRect(0, 0, 1, 1);
        using (var gesture = s.BeginTransaction())
        {
            gesture.Apply(new MaskPatch(part.Id, roi, new byte[] { 12 }));
            gesture.Apply(new MaskPatch(part.Id, roi, new byte[] { 30 }));
            Assert.IsTrue(s.IsDirty); Assert.IsFalse(s.CanUndo);
        }
        CollectionAssert.AreEqual(before, part.CopyMask(part.Bounds)); Assert.IsFalse(s.IsDirty);
        using (var gesture = s.BeginTransaction())
        {
            gesture.Apply(new MaskPatch(part.Id, roi, new byte[] { 12 }));
            gesture.Apply(new MaskPatch(part.Id, roi, new byte[] { 30 }));
            gesture.Commit();
        }
        Assert.AreEqual(2, s.UndoCount); s.Undo();
        CollectionAssert.AreEqual(before, part.CopyMask(part.Bounds));
        s.Redo(); Assert.AreEqual((byte)30, part.MaskAt(0, 0));
    }

    [TestMethod]
    public void BudgetEvictsWholeEntriesAndRejectsOversizedGesture()
    {
        var s = Open(300); var b = s.Document!.Base;
        s.Execute(new RenameLayer(b.Id, "first"));
        s.Execute(new RenameLayer(b.Id, "second"));
        s.Execute(new RenameLayer(b.Id, "third"));
        Assert.IsTrue(s.HistoryBytes <= 300); Assert.IsTrue(s.UndoCount < 3);
        var count = s.UndoCount;
        Reject(EditError.HistoryBudgetExceeded, () => s.Execute(new RenameLayer(b.Id, new string('x', 200))));
        Assert.AreEqual("third", b.Name); Assert.AreEqual(count, s.UndoCount);
    }

    [TestMethod]
    public void PreviewAndViewportAreNotAuthoredChanges()
    {
        var s = Open(); s.Execute(new AddLayer(Part())); s.MarkSaved();
        var revision = s.Document!.Revision; var state = s.CurrentRevision; var undo = s.UndoCount;
        s.SetPreviewSource(PreviewSource.Original); s.SetPreviewSource(PreviewSource.Composite);
        s.Viewport.PanBy(10, 20); s.Viewport.ZoomAt(new ViewportPoint(5, 8), 2);
        Assert.AreEqual(revision, s.Document.Revision); Assert.AreEqual(state, s.CurrentRevision);
        Assert.AreEqual(undo, s.UndoCount); Assert.IsFalse(s.IsDirty);
    }

    [TestMethod]
    public void NoOpDoesNotDirtyAdvanceRevisionOrClearRedo()
    {
        var s = Open(); var b = s.Document!.Base;
        s.Execute(new RenameLayer(b.Id, "redo")); s.Undo();
        var revision = s.Document.Revision;
        s.Execute(new RenameLayer(b.Id, ""), new SetLayerVisibility(b.Id, true));
        Assert.AreEqual(revision, s.Document.Revision);
        Assert.IsFalse(s.IsDirty); Assert.IsTrue(s.CanRedo);
    }

    [TestMethod]
    public void InvalidPatchRejectsWithoutModifyingTarget()
    {
        var s = Open(); var part = Part(); s.Execute(new AddLayer(part));
        var before = part.CopyMask(part.Bounds); var revision = s.Document!.Revision;
        Reject(EditError.InvalidPatch, () => s.Execute(new MaskPatch(part.Id, new DocumentRect(3, 2, 1, 1), new byte[] { 1 })));
        Reject(EditError.InvalidPatch, () => s.Execute(new MaskPatch(part.Id, part.Bounds, new byte[] { 1 })));
        CollectionAssert.AreEqual(before, part.CopyMask(part.Bounds));
        Assert.AreEqual(revision, s.Document.Revision);
    }

    [TestMethod]
    public void DocumentAndLayerCannotHaveParallelEditingOwners()
    {
        var s = Open(); var other = Open(); var layer = Part();
        s.Execute(new AddLayer(layer));
        Reject(EditError.InvalidLayer, () => other.Open(s.Document!));
        Reject(EditError.InvalidLayer, () => other.Execute(new AddLayer(layer)));
        Reject(EditError.InvalidLayer, () => s.Execute(new AddLayer(layer)));
        Assert.AreEqual(2, s.Document!.Layers.Count); Assert.AreEqual(1, other.Document!.Layers.Count);
    }

    [TestMethod]
    public void OnePixelHistoryCostIsIndependentOfSurfaceSize()
    {
        var s = Open(); var repair = Repair(); s.Execute(new AddLayer(repair));
        var before = s.HistoryBytes;
        s.Execute(new RasterPatch(repair.Id, new DocumentRect(2, 1, 1, 1), new byte[] { 1, 2, 3, 4 }));
        Assert.AreEqual(128L + 8, s.HistoryBytes - before);
    }
}
