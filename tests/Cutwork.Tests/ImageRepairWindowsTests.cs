using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Imaging.Persistence;
using Flamoris.Cutwork.App.Generation;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class ImageRepairWindowsTests
{
    [TestMethod]
    public void PngRequiresExactDimensionsAndExplicitResizePreservesCoverage()
    {
        var pixels=Enumerable.Repeat(new byte[]{10,20,30,255},64*64).SelectMany(x=>x).ToArray();
        byte[] png=RepairBitmaps.Png(pixels,64,64);
        CollectionAssert.AreEqual(pixels,RepairBitmaps.Decode(png,64,64));
        Assert.ThrowsExactly<InvalidOperationException>(()=>RepairBitmaps.Decode(png,128,64));
        var resized=RepairBitmaps.Resize(pixels,64,64,72,80);
        var restored=RepairBitmaps.Resize(resized,72,80,64,64);CollectionAssert.AreEqual(pixels,restored);
    }
    [TestMethod]
    public void GeneratedRepairRoundTripsAsOrdinaryOwnedRaster()
    {
        var s=new EditorSession();var original=new OriginalAsset("synthetic",new PixelSize(2,1),8,new byte[]{10,20,30,255,40,50,60,255});
        s.Open(new CutworkDocument(original));var part=new PartLayer(new DocumentRect(0,0,1,1),new byte[]{255});s.Execute(new AddLayer(part));
        var request=new GeneratedRepair(s,part.Id);request.SetCandidate(new byte[]{200,100,50,255,1,2,3,255});
        Guid repair=request.Commit(s,"AI");s.Execute(new SetLayerVisibility(part.Id,false));
        using var before=new CompositeCache(s.Document!);before.RenderPending();
        using var stream=new MemoryStream();var codec=new FlimgArchiveCodec();codec.Write(stream,s.Document!);stream.Position=0;
        var loaded=codec.Read(stream);using var after=new CompositeCache(loaded);after.RenderPending();
        CollectionAssert.AreEqual(before.CopyPixels(),after.CopyPixels());
        CollectionAssert.AreEqual(original.CopyPixelBytes(),loaded.Original.CopyPixelBytes());
        Assert.AreEqual(part.Id,((RepairLayer)loaded.GetLayer(repair)).OwnerPartId);
    }
}
