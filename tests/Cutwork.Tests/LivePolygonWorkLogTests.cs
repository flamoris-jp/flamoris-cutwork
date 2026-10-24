using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Mcp;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LivePolygonWorkLogTests
{
    private static JsonElement Value(McpResult r){Assert.IsFalse(r.IsError,r.Error);return r.Value!.Value;}
    [TestMethod]
    public async Task LargeClothingPreviewIsPureThenCreateUsesOneUndoAndPreservesOriginal()
    {
        var s=new EditorSession();s.Open(new(new("clothing",new(1024,1536),4096,new byte[1024*1536*4])));
        using var m=await McpCoreHarness.CreateAsync(s);
        var fence=new[]{new{x=20,y=20},new{x=1000,y=20},new{x=1000,y=1500},new{x=20,y=1500}};
        var before=s.Document!.Revision;var selected=s.SelectedLayerId;
        var preview=Value(await m.CallAsync("part_polygon_preview",new{fence,maxEdge=128}));
        Assert.AreEqual((1000-20+1)*(1500-20+1),preview.GetProperty("pixelCount").GetInt32());
        Assert.AreEqual(before,s.Document.Revision);Assert.AreEqual(selected,s.SelectedLayerId);Assert.AreEqual(0,s.UndoCount);
        Value(await m.CallAsync("part_polygon_create",new{fence,name="服"}));
        Assert.AreEqual(1,s.UndoCount);var part=s.Document.Layers.OfType<PartLayer>().Single();var mask=part.CopyMask(part.Bounds);
        Assert.IsTrue(mask.Length>LiveLimits.SurfacePixels);Assert.IsTrue(mask.All(x=>x==255));
        Value(await m.CallAsync("undo",new{}));Assert.AreEqual(1,s.Document.Layers.Count);
        Value(await m.CallAsync("redo",new{}));CollectionAssert.AreEqual(mask,part.CopyMask(part.Bounds));
        Assert.IsTrue(s.Document.Original.CopyPixelBytes().All(x=>x==0));
        Value(await m.CallAsync("edit",new{operations=new[]{new{type="layer.visible",target=part.Id.ToString(),visible=false}}}));
        Assert.IsFalse(part.Visible);Value(await m.CallAsync("undo",new{}));Assert.IsTrue(part.Visible);
        Value(await m.CallAsync("edit",new{operations=new[]{new{type="mask.stroke",target=part.Id.ToString(),
            points=new[]{new{x=100.5,y=100.5}},radius=2,polarity="Erase"}}}));
        Assert.AreEqual((byte)0,part.MaskAt(100,100));
    }
    [TestMethod]
    public async Task LogReadAppendIdempotencyAndReadonlyDenial()
    {
        var s=LiveMcpTests.Open();using var m=await McpCoreHarness.CreateAsync(s);
        string requestId=Guid.NewGuid().ToString("D");var request=new{requestId,decision="頬の赤みを丸くする",observation="まだ肌色に境界がある"};
        var entry=Value(await m.CallAsync("work_log_append",request)).GetProperty("entry");
        Assert.AreEqual(0,s.UndoCount);Assert.IsTrue(s.IsDirty);
        Value(await m.CallAsync("work_log_append",request));Assert.AreEqual(1,s.Document!.WorkLog.Count);
        Assert.AreEqual("work_log_request_conflict",(await m.CallAsync("work_log_append",new{requestId,decision="different",observation="x"})).Error);
        var page=Value(await m.CallAsync("work_log_read",new{afterSequence=0,count=20}));Assert.AreEqual(1,page.GetProperty("total").GetInt32());
        Assert.AreEqual(entry.GetProperty("id").GetString(),page.GetProperty("entries")[0].GetProperty("id").GetString());
        var revision=s.Document.Revision;
        await m.ReenableAsync(McpPermission.ReadOnly);
        Value(await m.CallAsync("work_log_read",new{afterSequence=0,count=20}));
        Assert.IsTrue((await m.CallAsync("work_log_append",request)).IsError);Assert.AreEqual(revision,s.Document.Revision);
        Assert.IsTrue((await m.CallAsync("part_polygon_create",new{fence=new[]{new{x=1,y=1},new{x=20,y=1},new{x=20,y=20}},name="denied"})).IsError);
    }
    [TestMethod]
    public async Task ReadPagesAreBoundedAndStaleGuardCannotAppend()
    {
        var s=LiveMcpTests.Open();using var m=await McpCoreHarness.CreateAsync(s);long revision=s.Document!.Revision;
        for(int i=0;i<5;i++)Value(await m.CallAsync("work_log_append",new{requestId=Guid.NewGuid().ToString("D"),decision=new string('a',8000),observation=new string('b',8000)}));
        var read=Value(await m.CallAsync("work_log_read",new{afterSequence=0,count=20}));
        Assert.IsTrue(read.GetProperty("returnedBytes").GetInt32()<=65536);Assert.IsTrue(read.GetProperty("entries").GetArrayLength()<5);
        var r=await m.CallAsync("work_log_append",new{requestId=Guid.NewGuid().ToString("D"),decision="stale",observation="stale"},expectedRevision:revision);
        Assert.AreEqual(McpErrors.StaleRevision,r.Error);Assert.AreEqual(5,s.Document.WorkLog.Count);
    }
}
