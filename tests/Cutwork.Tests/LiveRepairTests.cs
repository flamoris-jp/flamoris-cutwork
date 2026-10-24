using System.Security.Cryptography;
using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Mcp;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LiveRepairTests
{
    private static object Stroke(Guid? target=null)=>new{type="clone.stroke",target=target?.ToString(),ownerPart=(string?)null,global=target is null,
        source=new{x=5.5,y=5.5},points=new[]{new{x=20.5,y=20.5},new{x=24.5,y=20.5}},radius=2,mode="Offset",
        sourceScale=4,sourceRotationDegrees=30,hardness=.5,opacity=.6,sourceKind="Original"};
    private static readonly object Roi=new{x=16,y=16,width=14,height=12};
    private static JsonElement Value(McpResult r){Assert.IsFalse(r.IsError,r.Error);return r.Value!.Value;}

    [TestMethod]
    public async Task PreviewIsPureAndCommitUsesExactStoredPixelsOnce()
    {
        var s=LiveMcpTests.Open();using var m=await McpCoreHarness.CreateAsync(s);
        var revision=s.Document!.Revision;var selection=s.SelectedLayerId;
        var response=Value(await m.CallAsync("clone_candidates",new{variants=new[]{Stroke()},roi=Roi,maxEdge=64}));
        Assert.AreEqual(revision,s.Document.Revision);Assert.AreEqual(0,s.UndoCount);Assert.AreEqual(selection,s.SelectedLayerId);Assert.IsFalse(s.IsDirty);
        var candidate=response.GetProperty("candidates")[0];string id=candidate.GetProperty("candidateId").GetString()!;
        Value(await m.CallAsync("repair_commit_candidate",new{candidateId=id}));
        var repair=s.Document.Layers.OfType<RepairLayer>().Single();var digest=Convert.ToHexString(SHA256.HashData(repair.CopyPixels(repair.Bounds))).ToLowerInvariant();
        Assert.AreEqual(candidate.GetProperty("pixelSha256").GetString(),digest);Assert.AreEqual(1,s.UndoCount);
        var repeated=await m.CallAsync("repair_commit_candidate",new{candidateId=id});Assert.AreEqual("stale_candidate",repeated.Error);
        var pixels=repair.CopyPixels(repair.Bounds);s.Undo();Assert.AreEqual(1,s.Document.Layers.Count);s.Redo();CollectionAssert.AreEqual(pixels,repair.CopyPixels(repair.Bounds));
    }

    [TestMethod]
    public async Task RevisionUndoAndGrantChangesInvalidateCandidates()
    {
        var s=LiveMcpTests.Open();using var m=await McpCoreHarness.CreateAsync(s);
        async Task<string> Candidate()=>Value(await m.CallAsync("clone_candidates",new{variants=new[]{Stroke()},roi=Roi,maxEdge=32}))
            .GetProperty("candidates")[0].GetProperty("candidateId").GetString()!;
        var id=await Candidate();s.Execute(new RenameLayer(s.Document!.Base.Id,"x"));s.Undo();
        Assert.AreEqual("stale_candidate",(await m.CallAsync("repair_commit_candidate",new{candidateId=id})).Error);
        id=await Candidate();await m.ReenableAsync();
        Assert.AreEqual("stale_candidate",(await m.CallAsync("repair_commit_candidate",new{candidateId=id})).Error);
    }

    [TestMethod]
    public async Task ReadOnlyCanPreviewAndCannotCommitOrSave()
    {
        var s=LiveMcpTests.Open();using var m=await McpCoreHarness.CreateAsync(s,McpPermission.ReadOnly);
        var value=Value(await m.CallAsync("clone_candidates",new{variants=new[]{Stroke()},roi=Roi,maxEdge=32}));
        string id=value.GetProperty("candidates")[0].GetProperty("candidateId").GetString()!;
        Assert.IsTrue((await m.CallAsync("repair_commit_candidate",new{candidateId=id})).IsError);Assert.AreEqual(0,s.UndoCount);
    }

    [TestMethod]
    public async Task InspectionReturnsActualMaskAndPatchBakeIsOneUndoWithPixelParity()
    {
        var s=LiveMcpTests.Open();var part=new PartLayer(new(4,4,20,20),Enumerable.Repeat((byte)255,400).ToArray());
        var patch=new PatchLayer(new(4,4,3,3),Enumerable.Repeat(new byte[]{4,8,12,255},9).SelectMany(x=>x).ToArray(),new PatchTransform(12,12,2,20));
        s.Execute(new AddLayer(part),new SetLayerVisibility(part.Id,false),new AddLayer(patch));
        using var m=await McpCoreHarness.CreateAsync(s);
        var inspect=Value(await m.CallAsync("repair_inspect",new{roi=new{x=0,y=0,width=32,height=32},target=part.Id.ToString(),maxEdge=32}));
        Assert.AreEqual(400,inspect.GetProperty("holePixels").GetInt32());Assert.IsTrue(inspect.GetProperty("contour").GetArrayLength()>0);
        using var beforeCache=new CompositeCache(s.Document!);var before=beforeCache.RenderPending()!.PremultipliedBgra;
        int undo=s.UndoCount;
        Value(await m.CallAsync("edit",new{operations=new[]{new{type="patch.bakeToRepair",target=patch.Id.ToString()}}}));
        Assert.AreEqual(undo+1,s.UndoCount);using var afterCache=new CompositeCache(s.Document!);
        CollectionAssert.AreEqual(before,afterCache.RenderPending()!.PremultipliedBgra);
        s.Undo();Assert.AreSame(patch,s.Document!.GetLayer(patch.Id));
    }

    [TestMethod]
    public async Task FinishingUsesExistingKernelAndInvalidTargetRollsBack()
    {
        var s=LiveMcpTests.Open();var data=s.Document!.Original.CopyPixelBytes();
        var repair=new RepairLayer(new(0,0,32,32),data);s.Execute(new AddLayer(repair));
        var expected=new RepairFinishingKernel().Blur(repair,new(20.5,20.5),2,.5,s.Document.Dimensions);
        using var m=await McpCoreHarness.CreateAsync(s);
        Value(await m.CallAsync("edit",new{operations=new[]{new{type="blur.stroke",target=repair.Id.ToString(),points=new[]{new{x=20.5,y=20.5}},radius=2,strength=.5}}}));
        CollectionAssert.AreEqual(expected.StraightBgra.ToArray(),repair.CopyPixels(expected.Region));
        var before=repair.CopyPixels(repair.Bounds);int undo=s.UndoCount;
        var result=await m.CallAsync("edit",new{operations=new object[]{Stroke(repair.Id),new{type="blur.stroke",target=s.Document.Base.Id.ToString(),points=new[]{new{x=20.5,y=20.5}},radius=2,strength=.5}}});
        Assert.IsTrue(result.IsError);CollectionAssert.AreEqual(before,repair.CopyPixels(repair.Bounds));Assert.AreEqual(undo,s.UndoCount);
    }

    [TestMethod]
    public void ClosedCloneSchemaKeepsLegacyAndRejectsOutOfRangeExtensions()
    {
        var basic=new{operations=new[]{Stroke()}};LiveSchema.Validate("edit",JsonSerializer.SerializeToElement(basic));
        var invalid=JsonSerializer.Serialize(basic).Replace("\"sourceScale\":4","\"sourceScale\":9");
        Assert.ThrowsExactly<LiveException>(()=>LiveSchema.Validate("edit",JsonDocument.Parse(invalid).RootElement));
    }
}
