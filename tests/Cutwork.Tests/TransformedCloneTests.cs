using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class TransformedCloneTests
{
    private static EditorSession Open()
    {
        var pixels=Enumerable.Range(0,64*64).SelectMany(i=>new byte[]{(byte)(i%64*4),(byte)(i/64*4),80,255}).ToArray();
        var s=new EditorSession();s.Open(new(new("synthetic",new(64,64),256,pixels)));return s;
    }
    private static RepairLayer Target(EditorSession s)
    {var r=new RepairLayer(new(0,0,64,64),new byte[64*64*4]);s.Execute(new AddLayer(r));return r;}
    private static void Apply(EditorSession s,RepairLayer r,ICloneStroke stroke,params DocumentPoint[] points)
    {var p=stroke.Apply(r,points);if(!p.Region.IsEmpty&&p.HasChanges)s.Execute(new RasterPatch(r.Id,p.Region,p.StraightBgra.Span));}

    [TestMethod]
    public void ScaleFourSamplesQuarterFootprintAndClockwiseRotation()
    {
        var m=new CloneStrokeMapping(CloneSamplingMode.Fixed,new(20.5,20.5),new(40.5,40.5),4);
        Assert.AreEqual(new DocumentPoint(22.5,20.5),m.SourceFor(new(48.5,40.5),new(40.5,40.5)));
        m=m with {SourceRotationDegrees=90};
        var p=m.SourceFor(new(48.5,40.5),new(40.5,40.5));
        Assert.AreEqual(20.5,p.X,1e-10);Assert.AreEqual(18.5,p.Y,1e-10);
        m=m with {Mode=CloneSamplingMode.Offset};
        Assert.AreEqual(m.SourceFor(new(48.5,40.5),new(40.5,40.5)),m.SourceFor(new(48.5,40.5),new(45.5,45.5)));
    }

    [TestMethod]
    public void OpacityDoesNotAccumulateWithinStrokeAndBatchesMatch()
    {
        var s=Open();var r=Target(s);
        var m=new CloneStrokeMapping(CloneSamplingMode.Fixed,new(10.5,10.5),new(40.5,40.5),Opacity:.5);
        var stroke=new TransformedCloneStroke(s.Document!,m,3,new(0,0,64,64));
        Apply(s,r,stroke,new DocumentPoint(40.5,40.5));var first=r.CopyPixels(r.Bounds);
        Apply(s,r,stroke,new DocumentPoint(40.5,40.5));CollectionAssert.AreEqual(first,r.CopyPixels(r.Bounds));
        Assert.AreEqual((byte)128,r.PixelAt(40,40)[3]);
        var other=Target(s);var single=new TransformedCloneStroke(s.Document!,m,3,new(0,0,64,64));
        Apply(s,other,single,new DocumentPoint(40.5,40.5),new DocumentPoint(40.5,40.5));CollectionAssert.AreEqual(first,other.CopyPixels(other.Bounds));
        var next=new TransformedCloneStroke(s.Document!,m,3,new(0,0,64,64));
        Apply(s,r,next,new DocumentPoint(40.5,40.5));Assert.IsTrue(r.PixelAt(40,40)[3]>128);
    }

    [TestMethod]
    public void FrozenCompositeDoesNotRecursivelySampleAndNextStrokeSeesRepair()
    {
        var s=Open();var r=Target(s);var original=s.Document!.Original.CopyPixelBytes();
        var part=new PartLayer(new(20,9,2,3),Enumerable.Repeat((byte)255,6).ToArray());
        s.Execute(new AddLayer(part),new SetLayerVisibility(part.Id,false));
        var m=new CloneStrokeMapping(CloneSamplingMode.Offset,new(10.5,10.5),new(20.5,10.5),SourceKind:CloneSourceKind.CompositeAtStrokeStart);
        var frozen=new TransformedCloneStroke(s.Document,m,1,new(0,0,64,64));
        Apply(s,r,frozen,new DocumentPoint(20.5,10.5));
        Apply(s,r,frozen,new DocumentPoint(30.5,10.5));
        Assert.AreEqual((byte)0,r.PixelAt(30,10)[3]); // source at x=20 was a transparent hole at stroke start
        m=m with{Mode=CloneSamplingMode.Fixed,SourceAnchor=new(20.5,10.5),DestinationAnchor=new(40.5,10.5)};
        var next=new TransformedCloneStroke(s.Document,m,1,new(0,0,64,64));Apply(s,r,next,new DocumentPoint(40.5,10.5));
        Assert.AreEqual((byte)40,r.PixelAt(40,10)[0]);
        CollectionAssert.AreEqual(original,s.Document.Original.CopyPixelBytes());
    }

    [TestMethod]
    public void ZeroOpacityIsNoOpSoftEdgeAndTransparentSourceDoesNotErase()
    {
        var s=Open();var r=Target(s);
        var m=new CloneStrokeMapping(CloneSamplingMode.Fixed,new(10.5,10.5),new(30.5,30.5),Hardness:0,Opacity:0);
        var stroke=new TransformedCloneStroke(s.Document!,m,5,new(0,0,64,64));
        Assert.IsFalse(stroke.Apply(r,[new(30.5,30.5)]).HasChanges);
        m=m with{Opacity=1};stroke=new(s.Document!,m,5,new(0,0,64,64));Apply(s,r,stroke,new DocumentPoint(30.5,30.5));
        Assert.AreEqual((byte)255,r.PixelAt(30,30)[3]);Assert.IsTrue(r.PixelAt(34,30)[3]<100);
        var transparent=new CutworkDocument(new("transparent",new(64,64),256,new byte[64*64*4]));
        stroke=new(transparent,m,5,new(0,0,64,64));var before=r.CopyPixels(r.Bounds);Apply(s,r,stroke,new DocumentPoint(30.5,30.5));
        CollectionAssert.AreEqual(before,r.CopyPixels(r.Bounds));
    }

    [TestMethod]
    public void LegacyDefaultMatchesKernelAndUndoRedoPreservePixels()
    {
        var s=Open();var r=Target(s);var k=new CloneRepairKernel();
        var m=new CloneStrokeMapping(CloneSamplingMode.Fixed,new(10.5,10.5),new(30.5,30.5));
        Assert.IsTrue(m.IsLegacy);Assert.IsNull(k.BeginStroke(s.Document!,m,3));
        var p=k.CreatePatch(s.Document!.Original,r,[new(30.5,30.5)],m,3);
        s.Execute(new RasterPatch(r.Id,p.Region,p.StraightBgra.Span));var after=r.CopyPixels(r.Bounds);
        s.Undo();Assert.AreEqual((byte)0,r.PixelAt(30,30)[3]);s.Redo();CollectionAssert.AreEqual(after,r.CopyPixels(r.Bounds));
    }

    [TestMethod]
    public void SnapshotKeepsIdentityAndPixelsWithoutMutableLayerSharing()
    {
        var s=Open();var r=Target(s);var copy=DocumentSnapshot.Capture(s.Document!);
        Assert.AreEqual(s.Document!.Id,copy.Id);Assert.AreSame(s.Document.Original,copy.Original);
        Assert.AreEqual(r.Id,copy.Layers.OfType<RepairLayer>().Single().Id);
        s.Execute(new RasterPatch(r.Id,new(0,0,1,1),new byte[]{1,2,3,255}));
        Assert.AreEqual((byte)0,copy.Layers.OfType<RepairLayer>().Single().PixelAt(0,0)[3]);
    }
}
