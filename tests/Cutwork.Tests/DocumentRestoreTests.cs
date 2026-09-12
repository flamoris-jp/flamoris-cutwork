using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class DocumentRestoreTests
{
    [TestMethod]
    public void RestorePreservesDocumentLayerIdentityOrderAndAuthoredState()
    {
        var documentId = Guid.NewGuid();
        var partId = Guid.NewGuid();
        var baseId = Guid.NewGuid();
        var patchId = Guid.NewGuid();
        var repairId = Guid.NewGuid();
        var original = Original();
        var transform = new PatchTransform(3, 2, 1.25, 15);
        var layers = new LayerRestoreState[]
        {
            new PartLayerRestoreState(partId, "part", "eye_left", false,
                new(1, 1, 2, 2), new byte[] { 0, 64, 128, 255 }),
            new BaseLayerRestoreState(baseId, "base", null, false),
            new PatchLayerRestoreState(patchId, "patch", "underpaint", true,
                new(1, 1, 2, 2), Pixels(2, 2, 21), transform,
                [new(1, 1), new(3, 1), new(3, 3)]),
            new RepairLayerRestoreState(repairId, "repair", null, true,
                new(0, 0, 2, 1), Pixels(2, 1, 73)),
        };

        var document = CutworkDocument.Restore(documentId, original, layers);

        Assert.AreEqual(documentId, document.Id);
        CollectionAssert.AreEqual(new[] { partId, baseId, patchId, repairId },
            document.Layers.Select(layer => layer.Id).ToArray());
        Assert.AreEqual(baseId, document.Base.Id);
        Assert.IsFalse(document.Base.Visible);
        var part = (PartLayer)document.GetLayer(partId);
        CollectionAssert.AreEqual(new byte[] { 0, 64, 128, 255 }, part.CopyMask(part.Bounds));
        Assert.AreEqual("eye_left", part.SemanticName);
        Assert.IsFalse(part.Visible);
        var patch = (PatchLayer)document.GetLayer(patchId);
        Assert.AreEqual(transform, patch.Transform);
        Assert.AreEqual(new DocumentRect(1, 1, 2, 2), patch.SourceBounds);
        CollectionAssert.AreEqual(Pixels(2, 2, 21), patch.CopySourcePixels());
        CollectionAssert.AreEqual(Pixels(2, 1, 73),
            ((RepairLayer)document.GetLayer(repairId)).CopyPixels(new(0, 0, 2, 1)));
        Assert.AreEqual(0L, document.Revision);
    }

    [TestMethod]
    public void RestoreRejectsDuplicateIdsAndBandCrossing()
    {
        var id = Guid.NewGuid();
        AssertEditFailure(() => CutworkDocument.Restore(Guid.NewGuid(), Original(),
        [
            new BaseLayerRestoreState(id, "", null, true),
            new RepairLayerRestoreState(id, "", null, true, new(0, 0, 1, 1), new byte[4]),
        ]));
        AssertEditFailure(() => CutworkDocument.Restore(Guid.NewGuid(), Original(),
        [
            new BaseLayerRestoreState(Guid.NewGuid(), "", null, true),
            new PartLayerRestoreState(Guid.NewGuid(), "", null, true, new(0, 0, 1, 1), new byte[1]),
        ]));
    }

    [TestMethod]
    public void NormalConstructionStillGeneratesUniqueNonemptyIdentities()
    {
        var first = new CutworkDocument(Original());
        var second = new CutworkDocument(Original());
        Assert.AreNotEqual(Guid.Empty, first.Id);
        Assert.AreNotEqual(Guid.Empty, first.Base.Id);
        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreNotEqual(first.Base.Id, second.Base.Id);
    }

    private static OriginalAsset Original() =>
        new("restore", new(6, 5), 24, new byte[6 * 5 * 4]);

    private static byte[] Pixels(int width, int height, byte seed) =>
        Enumerable.Range(0, width * height * 4).Select(value => (byte)(seed + value)).ToArray();

    private static void AssertEditFailure(Action action)
    {
        try { action(); Assert.Fail("Expected EditException."); }
        catch (EditException) { }
    }
}
