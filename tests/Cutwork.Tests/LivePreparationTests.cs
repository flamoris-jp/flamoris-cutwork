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
    [DataRow("part_preview")]
    [DataRow("part_candidates")]
    public async Task BlockedPreviewPreparationLeavesHostLaneFreeAndRevokeDisclosesNothing(string tool)
    {
        var session = LiveMcpTests.Open();
        using var lane = new SingleLane();
        using var fitter = new BlockingFitter();
        using var mcp = await McpCoreHarness.CreateAsync(session,
            dispatch: lane.Dispatch, partFitter: fitter);

        var call = mcp.CallAsync(tool, PreviewInput(tool));
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
            {
                session.Execute(new RenameLayer(session.Document!.Base.Id, "human edit"));
                return true;
            },
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
    [DataRow("part_preview")]
    [DataRow("part_candidates")]
    public async Task RevisionChangeDuringPreviewPreparationCannotDisclosePreparedImage(string tool)
    {
        var session = LiveMcpTests.Open();
        using var lane = new SingleLane();
        using var fitter = new BlockingFitter();
        using var mcp = await McpCoreHarness.CreateAsync(session,
            dispatch: lane.Dispatch, partFitter: fitter);

        var call = mcp.CallAsync(tool, PreviewInput(tool));
        Assert.IsTrue(fitter.Started.Wait(TimeSpan.FromSeconds(5)), "Fitting did not start.");
        try
        {
            await mcp.Host.InvokeAsync(() =>
            {
                session.Execute(new RenameLayer(session.Document!.Base.Id, "new revision"));
                return true;
            },
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
            await mcp.Host.InvokeAsync(() =>
            {
                session.Open(replacement);
                return true;
            }, CancellationToken.None)
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

    [TestMethod]
    public async Task ReplacementDuringCandidatesCannotPublishUnderFreshGrant()
    {
        var session = LiveMcpTests.Open();
        using var lane = new SingleLane();
        using var fitter = new BlockingFitter();
        using var mcp = await McpCoreHarness.CreateAsync(session,
            dispatch: lane.Dispatch, partFitter: fitter);
        var call = mcp.CallAsync("part_candidates", PreviewInput("part_candidates"));
        Assert.IsTrue(fitter.Started.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await mcp.Host.InvokeAsync(() =>
            {
                session.Open(session.Document!);
                return true;
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await mcp.ReenableAsync();
        }
        finally { fitter.Release.Set(); }
        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(McpErrors.Unauthorized, result.Error);
        Assert.IsNull(result.Value);
        Assert.AreEqual(0, session.UndoCount);
    }

    private static object PreviewInput(string tool) => tool == "part_candidates"
        ? new { fence = Fence, steps = new[] { 0, 4, 8 }, maxEdge = 32 }
        : new { fence = Fence, step = 2, maxEdge = 32 };

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
