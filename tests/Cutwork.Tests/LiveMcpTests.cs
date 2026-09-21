using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Mcp;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LiveMcpTests
{
    internal static EditorSession Open(long budget = 128L * 1024 * 1024)
    {
        var pixels = Enumerable.Range(0, 32 * 32).SelectMany(i => new byte[]
            { (byte)(i % 32 * 7), (byte)(i / 32 * 7), 100, 255 }).ToArray();
        var session = new EditorSession(budget);
        session.Open(new(new("synthetic.png", new(32, 32), 128, pixels)));
        return session;
    }

    private static readonly object[] Fence =
        [new { x = 4, y = 4 }, new { x = 20, y = 4 }, new { x = 20, y = 20 }, new { x = 4, y = 20 }];
    private static object Create => new { type = "part.create", fence = Fence, step = 0, name = "Face" };
    private static object Edit(params object[] operations) => new { operations };
    private static JsonElement Value(McpResult result) => result.Value?.Clone()
        ?? JsonSerializer.SerializeToElement(new { });
    private static void Ok(McpResult result) => Assert.IsFalse(result.IsError, result.Error);

    [TestMethod]
    public async Task AtomicPartMaskClonePatchUsesOneOrdinaryHistoryAndPreservesOriginal()
    {
        var session = Open();
        using var mcp = await McpCoreHarness.CreateAsync(session);
        var original = session.Document!.Original.CopyPixelBytes();
        var selection = session.SelectedLayerId;
        var result = await mcp.CallAsync("edit", Edit(Create,
            new { type = "mask.stroke", target = "@0", points = new[] { new { x = 10.5, y = 10.5 }, new { x = 15.5, y = 10.5 } }, radius = 2, polarity = "Erase" },
            new { type = "clone.stroke", target = (string?)null, ownerPart = "@0", global = false, source = new { x = 6.5, y = 6.5 }, points = new[] { new { x = 10.5, y = 10.5 }, new { x = 15.5, y = 10.5 } }, radius = 2, mode = "Fixed" },
            new { type = "patch.create", fence = Fence, transform = new { centerX = 12.5, centerY = 12.5, scale = 1, rotationDegrees = 0 }, name = "Patch" }));
        Ok(result);
        Assert.AreSame(session, mcp.Editor.Session);
        Assert.AreEqual(1, session.UndoCount);
        var ids = session.Document.Layers.Select(layer => layer.Id).ToArray();
        var part = session.Document.Layers.OfType<PartLayer>().Single();
        var mask = part.CopyMask(part.Bounds);
        var repair = session.Document.Layers.OfType<RepairLayer>().Single();
        var repaired = repair.CopyPixels(repair.Bounds);
        Assert.AreEqual(part.Id, repair.OwnerPartId);
        Assert.AreEqual((byte)0, part.MaskAt(10, 10));
        session.Undo();
        Assert.AreEqual(1, session.Document.Layers.Count);
        Assert.AreEqual(selection, session.SelectedLayerId);
        session.Redo();
        CollectionAssert.AreEqual(ids, session.Document.Layers.Select(layer => layer.Id).ToArray());
        CollectionAssert.AreEqual(mask, part.CopyMask(part.Bounds));
        CollectionAssert.AreEqual(repaired, repair.CopyPixels(repair.Bounds));
        CollectionAssert.AreEqual(original, session.Document.Original.CopyPixelBytes());
    }

    [TestMethod]
    public async Task CoreRevisionGuardRejectsUiEditUndoBackToSameHistoryState()
    {
        var session = Open();
        using var mcp = await McpCoreHarness.CreateAsync(session);
        session.Execute(new RenameLayer(session.Document!.Base.Id, "UI"));
        session.Undo();
        Assert.AreEqual(0L, session.CurrentRevision);
        var result = await mcp.CallAsync("edit", Edit(Create), expectedRevision: 0);
        Assert.AreEqual(McpErrors.StaleRevision, result.Error);
        Assert.AreEqual(1, session.RedoCount);
        session.Redo();
        Assert.IsTrue(mcp.Grant.IsActive);
    }

    [TestMethod]
    public async Task FailedBatchRestoresPriorHistorySelectionOwnershipAndBytes()
    {
        var session = Open();
        using var mcp = await McpCoreHarness.CreateAsync(session);
        session.Execute(new RenameLayer(session.Document!.Base.Id, "before"));
        session.Undo();
        var selected = session.SelectedLayerId;
        var result = await mcp.CallAsync("edit", Edit(Create,
            new { type = "layer.delete", target = session.Document.Base.Id.ToString() }));
        Assert.IsTrue(result.IsError);
        Assert.AreEqual(1, session.Document.Layers.Count);
        Assert.AreEqual(1, session.RedoCount);
        Assert.AreEqual(0L, session.CurrentRevision);
        Assert.AreEqual(selected, session.SelectedLayerId);
        Assert.IsFalse(session.HasActiveTransaction);
        Assert.IsTrue(session.Document.Revision > 2);
        session.Redo();
        Assert.AreEqual("before", session.Document.Base.Name);
    }

    [TestMethod]
    public async Task HistoryBudgetFailureRollsBackWithoutEvictingEarlierHistory()
    {
        var session = Open(400);
        session.Execute(new RenameLayer(session.Document!.Base.Id, "kept"));
        var revision = session.CurrentRevision;
        using var mcp = await McpCoreHarness.CreateAsync(session);
        var result = await mcp.CallAsync("edit", Edit(Create));
        Assert.IsTrue(result.IsError);
        Assert.AreEqual(1, session.Document.Layers.Count);
        Assert.AreEqual(revision, session.CurrentRevision);
        Assert.AreEqual(1, session.UndoCount);
        Assert.IsFalse(session.HasActiveTransaction);
    }

    [TestMethod]
    public async Task ReadOnlyRejectsMutationAndUnknownToolsFailClosed()
    {
        var session = Open();
        using var mcp = await McpCoreHarness.CreateAsync(session, McpPermission.ReadOnly);
        Assert.AreEqual(McpErrors.Forbidden, (await mcp.CallAsync("edit", Edit(Create))).Error);
        foreach (string name in new[] { "open", "save", "export", "future", "select", "rawRaster" })
            Assert.AreEqual(McpErrors.UnsupportedCapability, (await mcp.CallAsync(name, new { })).Error);
        Assert.AreEqual(0, session.UndoCount);
    }

    [TestMethod]
    public async Task HumanPendingPreviewAndTransactionsRejectReadsAndEdits()
    {
        var session = Open();
        var tool = new PartToolController(session, new GuriguriPartFitter());
        tool.Activate();
        tool.PointerDown(new(5, 5), 1, CanvasModifiers.None);
        using var mcp = await McpCoreHarness.CreateAsync(session, busy: () => tool.State != PartToolState.Idle);
        Assert.AreEqual(McpErrors.Busy, (await mcp.CallAsync("edit", Edit(Create))).Error);
        Assert.AreEqual(McpErrors.Busy, (await mcp.CallAsync("layers", new { offset = 0, count = 64 })).Error);
        Assert.AreEqual(PartToolState.DrawingFence, tool.State);
        tool.Cancel();
        using var transaction = session.BeginTransaction();
        Assert.AreEqual(McpErrors.Busy, (await mcp.CallAsync("edit", Edit(Create))).Error);
    }

    [TestMethod]
    public async Task CancellationAtCoreCommitGateCannotCommitLate()
    {
        var session = Open();
        using var cancellation = new CancellationTokenSource();
        int dispatches = 0;
        Task Dispatch(Action action, CancellationToken token)
        {
            dispatches++;
            if (dispatches == 3) cancellation.Cancel();
            action();
            return Task.CompletedTask;
        }
        using var mcp = await McpCoreHarness.CreateAsync(session, dispatch: Dispatch);
        var result = await mcp.CallAsync("edit", Edit(
            new { type = "layer.rename", target = session.Document!.Base.Id.ToString(), name = "cancelled" }),
            cancellationToken: cancellation.Token);
        Assert.AreEqual(McpErrors.Cancelled, result.Error);
        Assert.AreEqual("", session.Document.Base.Name);
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsFalse(session.HasActiveTransaction);
    }

    [TestMethod]
    public async Task SameInstanceReopenRevokesOldGrantAndFreshEnableUsesNewToken()
    {
        var session = Open();
        var oldToken = session.DocumentToken;
        using var old = await McpCoreHarness.CreateAsync(session);
        session.Open(session.Document!);
        Assert.IsFalse(old.Grant.IsActive);
        Assert.AreNotEqual(oldToken, session.DocumentToken);
        Assert.AreEqual(McpErrors.Unauthorized,
            (await old.CallAsync("context", new { }, documentToken: oldToken)).Error);
        using var fresh = await McpCoreHarness.CreateAsync(session, McpPermission.ReadOnly);
        Ok(await fresh.CallAsync("context", new { }));
    }

    [TestMethod]
    public async Task InvalidShapesEnumsDuplicateFieldsAndReferencesRejectBeforeMutation()
    {
        var session = Open();
        using var mcp = await McpCoreHarness.CreateAsync(session);
        foreach (string json in new[] { "null", "[]", "{\"offset\":0,\"offset\":1,\"count\":1}",
            "{\"offset\":0,\"count\":65}", "{\"offset\":0,\"count\":1,\"future\":true}" })
        {
            var input = JsonDocument.Parse(json).RootElement.Clone();
            Assert.AreEqual(McpErrors.InvalidRequest,
                (await mcp.Boundary.InvokeAsync(mcp.Grant, "layers", input,
                    new(mcp.Host.Snapshot.RuntimeId, session.DocumentToken, null))).Error);
        }
        Assert.IsTrue((await mcp.CallAsync("edit", Edit(
            new { type = "layer.rename", target = "@0", name = "bad" }))).IsError);
        Assert.IsTrue((await mcp.CallAsync("edit", Edit(
            new { type = "layer.rename", target = Guid.Empty.ToString(), name = "bad" }))).IsError);
        Assert.AreEqual(0, session.UndoCount);
    }

    [TestMethod]
    public void CountRadiusCoordinateAndResourceBoundariesAreClosed()
    {
        object Rename(int length) => new
            { type = "layer.rename", target = Guid.NewGuid().ToString(), name = new string('n', length) };
        JsonElement Batch(object[] operations) => JsonSerializer.SerializeToElement(new { operations });
        LiveSchema.Validate("edit", Batch(Enumerable.Repeat(Rename(256), 64).ToArray()));
        Assert.ThrowsExactly<LiveException>(() =>
            LiveSchema.Validate("edit", Batch(Enumerable.Repeat(Rename(256), 65).ToArray())));
        Assert.ThrowsExactly<LiveException>(() => LiveSchema.Validate("edit", Batch([Rename(257)])));
        object Stroke(int count, double radius) => new
        {
            type = "mask.stroke",
            target = Guid.NewGuid().ToString(),
            points = Enumerable.Repeat(new { x = 1, y = 1 }, count).ToArray(),
            radius,
            polarity = "Add",
        };
        LiveSchema.Validate("edit", Batch([Stroke(4096, 64)]));
        Assert.ThrowsExactly<LiveException>(() => LiveSchema.Validate("edit", Batch([Stroke(4097, 64)])));
        Assert.ThrowsExactly<LiveException>(() => LiveSchema.Validate("edit", Batch([Stroke(1, 64.01)])));
        LiveLimits.Point(new(31.99, 31.99), new(32, 32));
        Assert.ThrowsExactly<LiveException>(() => LiveLimits.Point(new(32, 1), new(32, 32)));
        Assert.ThrowsExactly<LiveException>(() => LiveLimits.Point(new(double.NaN, 1), new(32, 32)));
        Assert.ThrowsExactly<LiveException>(() => LiveLimits.Surface(new(0, 0, 513, 512)));
        var history = new LiveBudget(256);
        history.ReserveHistory(256);
        Assert.ThrowsExactly<LiveException>(() => history.ReserveHistory(1));
    }

    [TestMethod]
    public async Task RemoteRedoPreflightsRestoredLayersBeforeHistoryTravel()
    {
        var session = new EditorSession();
        session.Open(new(new("large.png", new(1000, 1000), 4000, new byte[4_000_000])));
        session.Execute(Enumerable.Range(0, 5).Select(_ => (EditCommand)new AddLayer(
            new PartLayer(new(0, 0, 1000, 1000), new byte[1_000_000]))).ToArray());
        session.Undo();
        using var mcp = await McpCoreHarness.CreateAsync(session);
        var result = await mcp.CallAsync("redo", new { });
        Assert.AreEqual("work_limit", result.Error);
        Assert.AreEqual(1, session.Document!.Layers.Count);
        Assert.AreEqual(1, session.RedoCount);
        session.Redo();
        Assert.AreEqual(6, session.Document.Layers.Count);
    }

    [TestMethod]
    public async Task PreviewCreationAndCloneReuseExistingKernels()
    {
        var session = Open();
        using var mcp = await McpCoreHarness.CreateAsync(session);
        var expected = PolygonGuriguri.Create(session.Document!.Original,
            [new(4, 4), new(20, 4), new(20, 20), new(4, 20)]).Adjust(2);
        Ok(await mcp.CallAsync("edit", Edit(
            new { type = "part.create", fence = Fence, step = 2, name = "fit" })));
        var part = session.Document.Layers.OfType<PartLayer>().Single();
        CollectionAssert.AreEqual(expected, part.CopyMask(part.Bounds));

        foreach (string mode in new[] { "Fixed", "Offset" })
        {
            var ui = Open();
            var remote = Open();
            var tool = new CloneRepairController(ui, new CloneRepairKernel());
            tool.Activate();
            tool.SetRadius(2);
            tool.SetSamplingMode(Enum.Parse<CloneSamplingMode>(mode));
            tool.PointerDown(new(4.5, 4.5), 1, CanvasModifiers.Alt);
            tool.PointerDown(new(10.5, 10.5), 1, CanvasModifiers.None);
            tool.PointerMove(new(20.5, 10.5), CanvasModifiers.None);
            while (tool.HasPendingWork) tool.ProcessPendingWork();
            tool.PointerUp(new(20.5, 10.5), CanvasPointerButton.Left, CanvasModifiers.None);
            while (tool.HasPendingWork) tool.ProcessPendingWork();
            using var remoteMcp = await McpCoreHarness.CreateAsync(remote);
            Ok(await remoteMcp.CallAsync("edit", Edit(new
            {
                type = "clone.stroke",
                target = (string?)null,
                ownerPart = (string?)null,
                global = true,
                source = new { x = 4.5, y = 4.5 },
                points = new[] { new { x = 10.5, y = 10.5 }, new { x = 20.5, y = 10.5 } },
                radius = 2,
                mode,
            })));
            var uiRepair = ui.Document!.Layers.OfType<RepairLayer>().Single();
            var remoteRepair = remote.Document!.Layers.OfType<RepairLayer>().Single();
            Assert.AreEqual(uiRepair.Bounds, remoteRepair.Bounds);
            CollectionAssert.AreEqual(uiRepair.CopyPixels(uiRepair.Bounds),
                remoteRepair.CopyPixels(remoteRepair.Bounds));
        }
    }
}
