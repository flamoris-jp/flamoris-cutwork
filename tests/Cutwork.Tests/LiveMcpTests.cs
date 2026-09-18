using System.IO;
using System.Text;
using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Mcp;
using ModelContextProtocol.Protocol;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LiveMcpTests
{
    internal static EditorSession Open(long budget = 128L * 1024 * 1024)
    {
        var pixels = Enumerable.Range(0, 32 * 32).SelectMany(i => new byte[] { (byte)(i % 32 * 7), (byte)(i / 32 * 7), 100, 255 }).ToArray();
        var s = new EditorSession(budget); s.Open(new(new("synthetic.png", new(32, 32), 128, pixels))); return s;
    }
    private static readonly object[] Fence = [new { x = 4, y = 4 }, new { x = 20, y = 4 }, new { x = 20, y = 20 }, new { x = 4, y = 20 }];
    private static object Create => new { type = "part.create", fence = Fence, step = 0, name = "Face" };
    private static LiveEditor Adapter(EditorSession s, LiveAccess a, Func<bool>? busy = null, Func<Task>? yield = null) => new(s, a, busy ?? (() => false), yield ?? (() => Task.CompletedTask), _ => { });
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Text(CallToolResult result) => JsonDocument.Parse(result.Content.OfType<TextContentBlock>().First().Text).RootElement.Clone();
    private static Task<CallToolResult> Edit(LiveEditor e, params object[] ops) => e.CallAsync("edit", Args(new { documentToken = e.Session.DocumentToken, expectedRevision = e.Session.Document!.Revision.ToString(), operations = ops }));
    private static void Ok(CallToolResult r) => Assert.IsFalse(r.IsError == true, Text(r).ToString());

    [TestMethod]
    public async Task AtomicPartMaskClonePatchUsesOneOrdinaryHistoryAndPreservesOriginal()
    {
        var s = Open(); using var a = new LiveAccess(s, LivePermission.Edit); var e = Adapter(s, a);
        var original = s.Document!.Original.CopyPixelBytes(); var selection = s.SelectedLayerId;
        var result = await Edit(e, Create,
            new { type = "mask.stroke", target = "@0", points = new[] { new { x = 10.5, y = 10.5 }, new { x = 15.5, y = 10.5 } }, radius = 2, polarity = "Erase" },
            new { type = "clone.stroke", target = (string?)null, ownerPart = "@0", global = false, source = new { x = 6.5, y = 6.5 }, points = new[] { new { x = 10.5, y = 10.5 }, new { x = 15.5, y = 10.5 } }, radius = 2, mode = "Fixed" },
            new { type = "patch.create", fence = Fence, transform = new { centerX = 12.5, centerY = 12.5, scale = 1, rotationDegrees = 0 }, name = "Patch" });
        Ok(result); Assert.AreSame(s, e.Session); Assert.AreEqual(1, s.UndoCount);
        var ids = s.Document.Layers.Select(l => l.Id).ToArray();
        var part = s.Document.Layers.OfType<PartLayer>().Single(); var mask = part.CopyMask(part.Bounds);
        var repair = s.Document.Layers.OfType<RepairLayer>().Single(); var repaired = repair.CopyPixels(repair.Bounds);
        Assert.AreEqual(part.Id, repair.OwnerPartId); Assert.AreEqual((byte)0, part.MaskAt(10, 10));
        s.Undo(); Assert.AreEqual(1, s.Document.Layers.Count); Assert.AreEqual(selection, s.SelectedLayerId);
        s.Redo(); CollectionAssert.AreEqual(ids, s.Document.Layers.Select(l => l.Id).ToArray());
        CollectionAssert.AreEqual(mask, part.CopyMask(part.Bounds)); CollectionAssert.AreEqual(repaired, repair.CopyPixels(repair.Bounds));
        CollectionAssert.AreEqual(original, s.Document.Original.CopyPixelBytes());
    }
    [TestMethod]
    public async Task RevisionRejectsUiEditUndoBackToSameHistoryState()
    {
        var s = Open(); using var a = new LiveAccess(s, LivePermission.Edit); var e = Adapter(s, a);
        var args = Args(new { documentToken = s.DocumentToken, expectedRevision = "0", operations = new[] { Create } });
        s.Execute(new RenameLayer(s.Document!.Base.Id, "UI")); s.Undo(); Assert.AreEqual(0L, s.CurrentRevision);
        var r = await e.CallAsync("edit", args); Assert.AreEqual("revision_conflict", Text(r).GetProperty("error").GetString());
        Assert.AreEqual(1, s.RedoCount); s.Redo(); Assert.IsTrue(a.IsActive);
    }
    [TestMethod]
    public async Task FailedBatchRestoresPriorHistorySelectionOwnershipAndBytes()
    {
        var s = Open(); using var a = new LiveAccess(s, LivePermission.Edit); var e = Adapter(s, a);
        s.Execute(new RenameLayer(s.Document!.Base.Id, "before")); s.Undo(); var selected = s.SelectedLayerId;
        var r = await Edit(e, Create, new { type = "layer.delete", target = s.Document.Base.Id.ToString() });
        Assert.IsTrue(r.IsError); Assert.AreEqual(1, s.Document.Layers.Count); Assert.AreEqual(1, s.RedoCount);
        Assert.AreEqual(0L, s.CurrentRevision); Assert.AreEqual(selected, s.SelectedLayerId); Assert.IsFalse(s.HasActiveTransaction);
        Assert.IsTrue(s.Document.Revision > 2); s.Redo(); Assert.AreEqual("before", s.Document.Base.Name);
    }
    [TestMethod]
    public async Task HistoryBudgetFailureRollsBackWithoutEvictingEarlierHistory()
    {
        var s = Open(400); using var a = new LiveAccess(s, LivePermission.Edit); var e = Adapter(s, a);
        s.Execute(new RenameLayer(s.Document!.Base.Id, "kept")); var revision = s.CurrentRevision;
        var r = await Edit(e, Create); Assert.IsTrue(r.IsError); Assert.AreEqual(1, s.Document.Layers.Count);
        Assert.AreEqual(revision, s.CurrentRevision); Assert.AreEqual(1, s.UndoCount); Assert.IsFalse(s.HasActiveTransaction);
    }
    [TestMethod]
    public async Task ReadOnlyDiscoveryAndDirectInvocationFailClosed()
    {
        var s = Open(); using var a = new LiveAccess(s, LivePermission.ReadOnly); var e = Adapter(s, a);
        Assert.IsFalse(LiveSchema.Tools(a.Permission).Any(t => t.Name is "edit" or "undo" or "redo"));
        Assert.AreEqual("read_only", Text(await Edit(e, Create)).GetProperty("error").GetString());
        foreach (string name in new[] { "open", "save", "export", "future", "select", "rawRaster" })
            Assert.IsTrue((await e.CallAsync(name, Args(new { }))).IsError);
    }
    [TestMethod]
    public async Task HumanPendingPreviewAndTransactionsRejectReadsAndEdits()
    {
        var s = Open(); using var a = new LiveAccess(s, LivePermission.Edit); var tool = new PartToolController(s, new GuriguriPartFitter());
        tool.Activate(); tool.PointerDown(new(5, 5), 1, CanvasModifiers.None);
        var e = Adapter(s, a, () => tool.State != PartToolState.Idle);
        Assert.AreEqual("busy", Text(await Edit(e, Create)).GetProperty("error").GetString());
        Assert.AreEqual("busy", Text(await e.CallAsync("layers", Args(new { offset = 0, count = 64 }))).GetProperty("error").GetString());
        Assert.AreEqual(PartToolState.DrawingFence, tool.State); tool.Cancel();
        using var tx = s.BeginTransaction(); Assert.IsTrue((await Edit(e, Create)).IsError);
        Assert.IsTrue(Text(await e.CallAsync("context", Args(new { }))).GetProperty("busy").GetBoolean());
    }
    [TestMethod]
    public async Task RevokeAtYieldBeforeCommitRollsBackWithoutSleep()
    {
        var s = Open(); using var a = new LiveAccess(s, LivePermission.Edit); int turns = 0;
        var e = Adapter(s, a, yield: () => { if (++turns == 2) a.Revoke(); return Task.CompletedTask; });
        var r = await Edit(e, new { type = "layer.rename", target = s.Document!.Base.Id.ToString(), name = "cancelled" });
        Assert.IsTrue(r.IsError); Assert.AreEqual("", s.Document.Base.Name); Assert.AreEqual(0, s.UndoCount); Assert.IsFalse(s.HasActiveTransaction);
    }
    [TestMethod]
    public async Task ReplacementAndSameInstanceReopenRevokeIrreversibly()
    {
        var s = Open(); using var a = new LiveAccess(s, LivePermission.Edit); var e = Adapter(s, a); var token = s.DocumentToken;
        s.Open(s.Document!); Assert.IsFalse(a.IsActive); Assert.AreNotEqual(token, s.DocumentToken);
        using var fresh = new LiveAccess(s, LivePermission.Edit);
        Assert.IsTrue((await e.CallAsync("context", Args(new { }))).IsError);
        Ok(await Adapter(s, fresh).CallAsync("context", Args(new { })));
    }
    [TestMethod]
    public async Task InvalidShapesEnumsDuplicateFieldsAndReferencesRejectBeforeMutation()
    {
        var s = Open(); using var a = new LiveAccess(s, LivePermission.Edit); var e = Adapter(s, a);
        foreach (string json in new[] { "null", "[]", "{\"offset\":0,\"offset\":1,\"count\":1}", "{\"offset\":0,\"count\":65}", "{\"offset\":0,\"count\":1,\"future\":true}" })
            Assert.IsTrue((await e.CallAsync("layers", JsonDocument.Parse(json).RootElement)).IsError);
        Assert.IsTrue((await Edit(e, new { type = "layer.rename", target = "@0", name = "bad" })).IsError);
        Assert.IsTrue((await Edit(e, new { type = "layer.rename", target = Guid.Empty.ToString(), name = "bad" })).IsError);
        Assert.AreEqual(0, s.UndoCount);
    }
    [TestMethod]
    public void LimitsBoundDabsGrowthFenceAndCumulativeWork()
    {
        Assert.ThrowsExactly<LiveException>(() => LiveLimits.Stroke([new(0, 0), new(9999, 9999)], 0.5, new(10000, 10000), new()));
        Assert.ThrowsExactly<LiveException>(() => LiveLimits.Surface(new(0, 0, 513, 512)));
        LiveLimits.Surface(new(0, 0, 512, 512));
        var budget = new LiveBudget(); budget.Add(LiveLimits.WorkUnits, 0);
        Assert.ThrowsExactly<LiveException>(() => budget.Add(1, 0));
        var bytes = new LiveBudget(); bytes.Add(0, LiveLimits.WorkingBytes);
        Assert.ThrowsExactly<LiveException>(() => bytes.Add(0, 1));
        Assert.ThrowsExactly<LiveException>(() => LiveLimits.Fence([new(0,0),new(1000,0),new(1000,1000)], new(2000,2000), new()));
    }
    [TestMethod]
    public async Task RemoteRedoPreflightsRestoredLayersBeforeHistoryTravel()
    {
        var s = new EditorSession(); s.Open(new(new("large.png",new(1000,1000),4000,new byte[4_000_000])));
        s.Execute(Enumerable.Range(0,5).Select(i => (EditCommand)new AddLayer(new PartLayer(new(0,0,1000,1000),new byte[1_000_000]))).ToArray());
        s.Undo(); using var access = new LiveAccess(s,LivePermission.Edit);
        var result = await Adapter(s,access).CallAsync("redo",Args(new{documentToken=s.DocumentToken,expectedRevision=s.Document!.Revision.ToString()}));
        Assert.AreEqual("work_limit",Text(result).GetProperty("error").GetString());
        Assert.AreEqual(1,s.Document.Layers.Count); Assert.AreEqual(1,s.RedoCount);
        s.Redo(); Assert.AreEqual(6,s.Document.Layers.Count);
    }
    [TestMethod]
    public void CountRadiusCoordinateAndHistoryBoundariesAreClosed()
    {
        object Rename(int length) => new { type="layer.rename", target=Guid.NewGuid().ToString(), name=new string('n',length) };
        JsonElement Batch(object[] ops) => Args(new { documentToken=new string('a',32), expectedRevision="0", operations=ops });
        LiveSchema.Validate("edit", Batch(Enumerable.Repeat(Rename(256),64).ToArray()));
        Assert.ThrowsExactly<LiveException>(() => LiveSchema.Validate("edit", Batch(Enumerable.Repeat(Rename(256),65).ToArray())));
        Assert.ThrowsExactly<LiveException>(() => LiveSchema.Validate("edit", Batch([Rename(257)])));
        object Stroke(int count, double radius) => new { type="mask.stroke", target=Guid.NewGuid().ToString(), points=Enumerable.Repeat(new{x=1,y=1},count).ToArray(), radius, polarity="Add" };
        LiveSchema.Validate("edit", Batch([Stroke(4096,64)]));
        Assert.ThrowsExactly<LiveException>(() => LiveSchema.Validate("edit", Batch([Stroke(4097,64)])));
        Assert.ThrowsExactly<LiveException>(() => LiveSchema.Validate("edit", Batch([Stroke(1,64.01)])));
        LiveLimits.Point(new(31.99,31.99), new(32,32));
        Assert.ThrowsExactly<LiveException>(() => LiveLimits.Point(new(32,1), new(32,32)));
        Assert.ThrowsExactly<LiveException>(() => LiveLimits.Point(new(double.NaN,1), new(32,32)));
        var history = new LiveBudget(256); history.ReserveHistory(256);
        Assert.ThrowsExactly<LiveException>(() => history.ReserveHistory(1));
        var fence = Enumerable.Range(0,64).Select(i => new DocumentPoint(16+8*Math.Cos(i*Math.PI/32),16+8*Math.Sin(i*Math.PI/32))).ToArray();
        LiveLimits.Fence(fence, new(32,32), new());
        Assert.ThrowsExactly<LiveException>(() => LiveLimits.Fence(fence.Append(fence[0]).ToArray(), new(32,32), new()));
        LiveLimits.Stroke([new(1,1), new(512.5,1)], 0.5, new(600,32), new());
        Assert.ThrowsExactly<LiveException>(() => LiveLimits.Stroke([new(1,1), new(513,1)], 0.5, new(600,32), new()));
    }
    [TestMethod]
    public async Task DistantEditsBoundRollbackUnionAndRestorePriorHistory()
    {
        var s = new EditorSession();
        s.Open(new(new("large.png", new(2000,2000), 8000, new byte[2000*2000*4])));
        var first = new PartLayer(new(0,0,10,10), new byte[100], "first");
        var last = new PartLayer(new(1900,1900,10,10), new byte[100], "last");
        s.Execute(new AddLayer(first)); s.Execute(new AddLayer(last));
        using var access = new LiveAccess(s, LivePermission.Edit);
        var result = await Edit(Adapter(s, access),
            new { type = "layer.visible", target = first.Id.ToString(), visible = false },
            new { type = "layer.visible", target = last.Id.ToString(), visible = false });
        Assert.AreEqual("work_limit", Text(result).GetProperty("error").GetString());
        Assert.IsTrue(first.Visible); Assert.IsTrue(last.Visible);
        Assert.AreEqual(2, s.UndoCount); Assert.IsFalse(s.HasActiveTransaction);
    }
    [TestMethod]
    public async Task FrameLimitsUtf8TruncationAndDuplicateEnvelopeAreContained()
    {
        foreach (var pair in new[] { (new byte[] { 255, 10 }, McpFrameStatus.InvalidUtf8), (Encoding.UTF8.GetBytes("12345\n"), McpFrameStatus.Oversized),
            (Encoding.UTF8.GetBytes("1234\n"), McpFrameStatus.Success), (Encoding.UTF8.GetBytes("1234"), McpFrameStatus.Truncated) })
        {
            using var stream = new MemoryStream(pair.Item1);
            Assert.AreEqual(pair.Item2, (await new McpBoundedLineReader(stream, 4, 2).ReadAsync()).Status);
        }
        foreach (string raw in new[] { "[]", "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"method\":\"initialize\"}", "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":null}" })
            Assert.ThrowsExactly<IOException>(() => BoundedProtocolStream.ValidateEnvelope(JsonDocument.Parse(raw).RootElement));
    }
    [TestMethod]
    public async Task PreviewPurityAndCreationMatchExistingFitter()
    {
        var s = Open(); using var a = new LiveAccess(s, LivePermission.Edit); var e = Adapter(s, a);
        var expected = PolygonGuriguri.Create(s.Document!.Original, [new(4,4),new(20,4),new(20,20),new(4,20)]).Adjust(2);
        // Pure fitting path is also used for preview; the encoder is covered on an STA in image tests.
        Ok(await Edit(e, new { type = "part.create", fence = Fence, step = 2, name = "fit" }));
        var part = s.Document.Layers.OfType<PartLayer>().Single(); CollectionAssert.AreEqual(expected, part.CopyMask(part.Bounds));
    }
    [TestMethod]
    public async Task CloneMatchesControllerBothModesAndExplicitTargetsIgnoreSelection()
    {
        foreach (string mode in new[] { "Fixed", "Offset" })
        {
            var ui = Open(); var remote = Open(); using var a = new LiveAccess(remote, LivePermission.Edit); var e = Adapter(remote, a);
            var tool = new CloneRepairController(ui, new CloneRepairKernel()); tool.Activate(); tool.SetRadius(2); tool.SetSamplingMode(Enum.Parse<CloneSamplingMode>(mode));
            tool.PointerDown(new(4.5,4.5),1,CanvasModifiers.Alt); tool.PointerDown(new(10.5,10.5),1,CanvasModifiers.None);
            tool.PointerMove(new(20.5,10.5),CanvasModifiers.None); while(tool.HasPendingWork) tool.ProcessPendingWork();
            tool.PointerUp(new(20.5,10.5),CanvasPointerButton.Left,CanvasModifiers.None); while(tool.HasPendingWork) tool.ProcessPendingWork();
            Ok(await Edit(e,new { type="clone.stroke",target=(string?)null,ownerPart=(string?)null,global=true,source=new{x=4.5,y=4.5},points=new[]{new{x=10.5,y=10.5},new{x=20.5,y=10.5}},radius=2,mode}));
            var u=ui.Document!.Layers.OfType<RepairLayer>().Single(); var r=remote.Document!.Layers.OfType<RepairLayer>().Single();
            Assert.AreEqual(u.Bounds,r.Bounds); CollectionAssert.AreEqual(u.CopyPixels(u.Bounds),r.CopyPixels(r.Bounds));
        }
    }
}
