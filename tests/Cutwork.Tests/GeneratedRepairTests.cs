using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class GeneratedRepairTests
{
    private static (EditorSession Session,PartLayer Part) Fixture()
    {
        var s=new EditorSession();var pixels=Enumerable.Repeat(new byte[]{10,20,30,255},6).SelectMany(x=>x).ToArray();
        s.Open(new CutworkDocument(new OriginalAsset("synthetic",new PixelSize(3,2),12,pixels)));
        var part=new PartLayer(new DocumentRect(1,0,2,2),new byte[]{255,0,128,0},"eye");s.Execute(new AddLayer(part));s.MarkSaved();
        return(s,part);
    }
    private static byte[] Fill(GeneratedRepair r)=>Enumerable.Repeat(new byte[]{200,100,50,255},r.Region.Width*r.Region.Height).SelectMany(x=>x).ToArray();
    [TestMethod]
    public void CandidateIsPureAndClippedAndUsesOneUndoWithExactRedo()
    {
        var(s,p)=Fixture();var original=s.Document!.Original.CopyPixelBytes();long rev=s.Document.Revision;int history=s.UndoCount;
        var request=new GeneratedRepair(s,p.Id);request.SetCandidate(Fill(request));
        Assert.AreEqual(rev,s.Document.Revision);Assert.IsFalse(s.IsDirty);Assert.AreEqual(history,s.UndoCount);
        var layer=request.PreviewLayer("AI");var pixels=layer.CopyPixels(request.Region);
        CollectionAssert.AreEqual(new byte[]{0,0,0,0},pixels.AsSpan(0,4).ToArray());
        Assert.AreEqual((byte)255,pixels[3*4+7]); // soft-mask support remains opaque underpaint
        using var cache=new CompositeCache(s.Document);cache.RenderPending();var before=cache.CopyPixels();
        var expected=new byte[before.Length];
        for(int y=0;y<2;y++)for(int x=0;x<3;x++)
            CompositeCache.CompositePixel(s.Document,x,y,CompositeCache.HoleAt(s.Document,x,y),expected.AsSpan((y*3+x)*4,4),null,layer);
        Guid id=request.Commit(s,"AI");Assert.AreEqual(history+1,s.UndoCount);
        Assert.AreEqual(p.Id,((RepairLayer)s.Document.GetLayer(id)).OwnerPartId);cache.RenderPending();
        CollectionAssert.AreEqual(expected,cache.CopyPixels());
        CollectionAssert.AreEqual(original,s.Document.Original.CopyPixelBytes());
        s.Undo();cache.RenderPending();CollectionAssert.AreEqual(before,cache.CopyPixels());
        s.Redo();cache.RenderPending();CollectionAssert.AreEqual(expected,cache.CopyPixels());
        Assert.ThrowsExactly<InvalidOperationException>(()=>request.Commit(s,"again"));
    }
    [TestMethod]
    public void HiddenPreviewMatchesCommittedTopUnderpaintOverExistingRepair()
    {
        var(s,p)=Fixture();var region=new DocumentRect(0,0,3,2);
        s.Execute(new AddLayer(new RepairLayer(region,Enumerable.Repeat((byte)255,24).ToArray())));
        var r=new GeneratedRepair(s,p.Id);r.SetCandidate(Fill(r));var proposed=r.PreviewLayer("AI");
        var expected=new byte[24];long revision=s.Document!.Revision;
        for(int y=0;y<2;y++)for(int x=0;x<3;x++)
            CompositeCache.CompositePixel(s.Document,x,y,CompositeCache.HoleAt(s.Document,x,y),expected.AsSpan((y*3+x)*4,4),p.Id,proposed);
        Assert.IsTrue(p.Visible);Assert.AreEqual(revision,s.Document.Revision);
        r.Commit(s,"AI");s.Execute(new SetLayerVisibility(p.Id,false));using var cache=new CompositeCache(s.Document);cache.RenderPending();
        CollectionAssert.AreEqual(expected,cache.CopyPixels());
    }
    [TestMethod]
    public void EditUndoAndReplacementInvalidateOldResult()
    {
        var(s,p)=Fixture();var r=new GeneratedRepair(s,p.Id);r.SetCandidate(Fill(r));
        s.Execute(new RenameLayer(p.Id,"changed"));s.Undo();Assert.IsFalse(r.IsCurrent(s));
        Assert.ThrowsExactly<InvalidOperationException>(()=>r.Commit(s,"stale"));
        var fresh=new GeneratedRepair(s,p.Id);fresh.SetCandidate(Fill(fresh));
        s.Open(new CutworkDocument(s.Document!.Original));Assert.IsFalse(fresh.IsCurrent(s));
    }
    [TestMethod]
    public void InvalidOrTransparentResultNeverEntersDocument()
    {
        var(s,p)=Fixture();var r=new GeneratedRepair(s,p.Id);long revision=s.Document!.Revision;
        Assert.ThrowsExactly<ArgumentException>(()=>r.SetCandidate(new byte[4]));
        Assert.ThrowsExactly<InvalidOperationException>(()=>r.SetCandidate(new byte[24]));
        Assert.AreEqual(revision,s.Document.Revision);Assert.IsFalse(s.IsDirty);
        s.Execute(new DeleteLayer(p.Id));Assert.ThrowsExactly<InvalidOperationException>(()=>r.Commit(s,"stale"));
    }
    [TestMethod]
    public void ReferenceOnlyErasesSelectedMaskAndLimitRejectsBeforeLargeAllocation()
    {
        var(s,p)=Fixture();var r=new GeneratedRepair(s,p.Id);var reference=r.ReferencePixels();
        CollectionAssert.AreEqual(new byte[]{10,20,30,255},reference.AsSpan(0,4).ToArray());
        CollectionAssert.AreEqual(new byte[]{128,128,128,255},reference.AsSpan(4,4).ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(()=>new GeneratedRepair(s,s.Document!.Base.Id));
        var large=new EditorSession();large.Open(new CutworkDocument(new OriginalAsset("synthetic",new PixelSize(513,513),513*4,new byte[513*513*4])));
        var part=new PartLayer(new DocumentRect(0,0,513,513),new byte[513*513]);large.Execute(new AddLayer(part));
        Assert.ThrowsExactly<InvalidOperationException>(()=>new GeneratedRepair(large,part.Id));
    }
}
