using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LayerDomainTests
{
    [TestMethod]
    public void NewDocumentHasExactlyOneStableBase()
    {
        var document = new CutworkDocument(new OriginalAsset("image", new PixelSize(2, 2), 8, new byte[16]));
        Assert.AreEqual(1, document.Layers.Count);
        Assert.AreSame(document.Base, document.Layers[0]);
        Assert.AreNotEqual(Guid.Empty, document.Id);
        Assert.AreNotEqual(Guid.Empty, document.Base.Id);
        Assert.IsFalse(document.CanDelete(document.Base.Id));
        Assert.IsFalse(document.CanReorder(document.Base.Id, 0));
    }

    [TestMethod]
    public void LayerInputsAndReturnedPatchesDoNotExposeAuthoredArrays()
    {
        var bounds = new DocumentRect(1, 1, 1, 1);
        var mask = new byte[] { 255 };
        var layer = new PartLayer(bounds, mask);
        mask[0] = 0;
        layer.CopyMask(bounds)[0] = 0;
        Assert.AreEqual((byte)255, layer.MaskAt(1, 1));
        Assert.AreEqual((byte)0, layer.MaskAt(0, 0));
    }

    [TestMethod]
    public void RectUnionAndIntersectionAreHalfOpen()
    {
        var a = new DocumentRect(2, 3, 4, 5);
        var b = new DocumentRect(5, 6, 4, 3);
        Assert.AreEqual(new DocumentRect(2, 3, 7, 6), a.Union(b));
        Assert.AreEqual(new DocumentRect(5, 6, 1, 2), a.Intersect(b));
        Assert.IsTrue(a.Intersect(new DocumentRect(6, 3, 2, 2)).IsEmpty);
    }

    [TestMethod]
    public void PixelReadsRejectRowWrappingAtTheRightEdge()
    {
        var original = new OriginalAsset("image", new PixelSize(2, 2), 8, new byte[16]);
        var raster = new RepairLayer(new DocumentRect(1, 1, 2, 2), new byte[16]);
        try { original.PixelAt(2, 0); Assert.Fail("Expected bounds rejection."); }
        catch (ArgumentOutOfRangeException) { }
        try { raster.PixelAt(3, 1); Assert.Fail("Expected bounds rejection."); }
        catch (ArgumentOutOfRangeException) { }
    }
}
