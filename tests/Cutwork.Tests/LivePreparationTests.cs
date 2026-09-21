using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LivePreparationTests
{
    private static readonly object[] Fence =
        [new { x = 4, y = 4 }, new { x = 20, y = 4 }, new { x = 20, y = 20 }, new { x = 4, y = 20 }];

    [TestMethod]
    public async Task BlockedPreviewPreparationLeavesHostLaneFreeAndRevokeDisclosesNothing()
    {
        var session = LiveMcpTests.Open();
        using var lane = new SingleLane();
        using var fitter = new BlockingFitter();
        using var mcp = await McpCoreHarness.CreateAsync(session,
            dispatch: lane.Dispatch, partFitter: fitter);

        var call = mcp.CallAsync("part_preview",
            new { fence = Fence, step = 2, maxEdge = 32 });
        Assert.IsTrue(fitter.Started.Wait(TimeSpan.FromSeconds(5)), "Fitting did not start.");
        try
        {
            Assert.IsTrue(await mcp.Host.InvokeAsync(() => true, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)),
                "Bounded Guriguri preparation occupied the host serialization lane.");
            mcp.Boundary.Disable();
        }
        finally { fitter.Release.Set(); }

        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(McpErrors.Unauthorized, result.Error);
        Assert.IsNull(result.Value);
        Assert.AreEqual(1, session.Document!.Layers.Count);
        Assert.AreEqual(0, session.UndoCount);
    }

    [TestMethod]
    public async Task RevisionChangeDuringPartPreparationCannotCommitPreparedMask()
    {
        var session = LiveMcpTests.Open();
        using var lane = new SingleLane();
        using var fitter = new BlockingFitter();
        using var mcp = await McpCoreHarness.CreateAsync(session,
            dispatch: lane.Dispatch, partFitter: fitter);

        var call = mcp.CallAsync("edit", new
        {
            operations = new[] { new { type = "part.create", fence = Fence, step = 1, name = "stale" } },
        });
        Assert.IsTrue(fitter.Started.Wait(TimeSpan.FromSeconds(5)), "Fitting did not start.");
        try
        {
            await mcp.Host.InvokeAsync(() =>
                session.Execute(new RenameLayer(session.Document!.Base.Id, "human edit")),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { fitter.Release.Set(); }

        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(McpErrors.StaleRevision, result.Error);
        Assert.AreEqual("human edit", session.Document!.Base.Name);
        Assert.AreEqual(1, session.Document.Layers.Count);
        Assert.AreEqual(1, session.UndoCount);
        Assert.IsFalse(session.HasActiveTransaction);
    }

    [TestMethod]
    public async Task RevisionChangeDuringPreviewPreparationCannotDisclosePreparedImage()
    {
        var session = LiveMcpTests.Open();
        using var lane = new SingleLane();
        using var fitter = new BlockingFitter();
        using var mcp = await McpCoreHarness.CreateAsync(session,
            dispatch: lane.Dispatch, partFitter: fitter);

        var call = mcp.CallAsync("part_preview",
            new { fence = Fence, step = 1, maxEdge = 32 });
        Assert.IsTrue(fitter.Started.Wait(TimeSpan.FromSeconds(5)), "Fitting did not start.");
        try
        {
            await mcp.Host.InvokeAsync(() =>
                session.Execute(new RenameLayer(session.Document!.Base.Id, "new revision")),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { fitter.Release.Set(); }

        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(McpErrors.StaleRevision, result.Error);
        Assert.IsNull(result.Value);
        Assert.AreEqual("new revision", session.Document!.Base.Name);
        Assert.AreEqual(1, session.Document.Layers.Count);
    }

    [TestMethod]
    public async Task DocumentReplacementDuringPartPreparationCannotCommitIntoReplacement()
    {
        var session = LiveMcpTests.Open();
        using var lane = new SingleLane();
        using var fitter = new BlockingFitter();
        using var mcp = await McpCoreHarness.CreateAsync(session,
            dispatch: lane.Dispatch, partFitter: fitter);
        var replacement = new CutworkDocument(session.Document!.Original);

        var call = mcp.CallAsync("edit", new
        {
            operations = new[] { new { type = "part.create", fence = Fence, step = 1, name = "old" } },
        });
        Assert.IsTrue(fitter.Started.Wait(TimeSpan.FromSeconds(5)), "Fitting did not start.");
        try
        {
            await mcp.Host.InvokeAsync(() => session.Open(replacement), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { fitter.Release.Set(); }

        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(McpErrors.Unauthorized, result.Error);
        Assert.IsFalse(mcp.Grant.IsActive);
        Assert.AreSame(replacement, session.Document);
        Assert.AreEqual(1, replacement.Layers.Count);
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsFalse(session.HasActiveTransaction);
    }

    private sealed class BlockingFitter : IPartBoundaryFitter, IDisposable
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public IPartFittingSession Create(OriginalAsset original,
            IReadOnlyList<DocumentPoint> fence)
        {
            Started.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release fitting preparation.");
            return new GuriguriPartFitter().Create(original, fence);
        }

        public void Dispose()
        {
            Release.Set();
            Started.Dispose();
            Release.Dispose();
        }
    }

    private sealed class SingleLane : IDisposable
    {
        private readonly ConcurrentExclusiveSchedulerPair scheduler =
            new(TaskScheduler.Default, maxConcurrencyLevel: 1);

        public Task Dispatch(Action action, CancellationToken cancellationToken) =>
            Task.Factory.StartNew(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
            }, cancellationToken, TaskCreationOptions.DenyChildAttach, scheduler.ExclusiveScheduler);

        public void Dispose()
        {
            scheduler.Complete();
            scheduler.Completion.GetAwaiter().GetResult();
        }
    }
}
