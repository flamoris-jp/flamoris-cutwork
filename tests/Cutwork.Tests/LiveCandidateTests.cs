using System.Security.Cryptography;
using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Mcp;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LiveCandidateTests
{
    private static readonly DocumentPoint[] Points = [new(4, 4), new(20, 4), new(20, 20), new(4, 20)];
    private static object[] Fence => Points.Select(p => (object)new { x = p.X, y = p.Y }).ToArray();
    private static object Query(params int[] steps) => new { fence = Fence, steps, maxEdge = 32 };
    private static object Create(string id, string name = "candidate") => new { type = "part.create", candidateId = id, name };
    private static object Edit(params object[] operations) => new { operations };
    private static JsonElement Value(McpResult result)
    {
        Assert.IsFalse(result.IsError, result.Error);
        return result.Value!.Value;
    }
    private static string Id(JsonElement result, int index = 0) =>
        result.GetProperty("candidates")[index].GetProperty("candidateId").GetString()!;

    [TestMethod]
    public async Task CandidatesArePureDeterministicAndCommitExactBytesWithoutRefitting()
    {
        var session = LiveMcpTests.Open();
        var fitter = new CountingFitter();
        using var mcp = await McpCoreHarness.CreateAsync(session, partFitter: fitter);
        var revision = session.Document!.Revision;
        var selected = session.SelectedLayerId;
        var first = Value(await mcp.CallAsync("part_candidates", Query(0, 4, 8)));
        Assert.AreEqual(1, fitter.Count);
        Assert.AreEqual(revision, session.Document.Revision);
        Assert.AreEqual(selected, session.SelectedLayerId);
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsFalse(session.IsDirty);
        Assert.AreEqual(3, first.GetProperty("candidates").GetArrayLength());

        var expected = new GuriguriPartFitter().Create(session.Document.Original, Points);
        foreach (var candidate in first.GetProperty("candidates").EnumerateArray())
        {
            var step = candidate.GetProperty("step").GetInt32();
            var mask = expected.Adjust(step - expected.Step);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(mask)).ToLowerInvariant(),
                candidate.GetProperty("maskSha256").GetString());
            var png = Convert.FromBase64String(candidate.GetProperty("preview").GetProperty("pngBase64").GetString()!);
            CollectionAssert.AreEqual(LiveImages.Cutout(session.Document.Original, expected.Bounds,
                mask, 32, CancellationToken.None).Png, png);
        }
        var second = Value(await mcp.CallAsync("part_candidates", Query(0, 4, 8)));
        Assert.AreEqual(first.GetProperty("candidates")[1].GetProperty("maskSha256").GetString(),
            second.GetProperty("candidates")[1].GetProperty("maskSha256").GetString());
        Assert.AreEqual("stale_candidate", (await mcp.CallAsync("edit", Edit(Create(Id(first))))).Error);
        Assert.AreEqual(0, session.UndoCount);

        var exact = expected.Adjust(4 - expected.Step);
        Value(await mcp.CallAsync("edit", Edit(Create(Id(second, 1)))));
        Assert.AreEqual(2, fitter.Count, "Commit recomputed a candidate.");
        var part = session.Document.Layers.OfType<PartLayer>().Single();
        CollectionAssert.AreEqual(exact, part.CopyMask(part.Bounds));
        Assert.AreEqual(1, session.UndoCount);
        session.Undo();
        Assert.AreEqual(1, session.Document.Layers.Count);
        Value(await mcp.CallAsync("redo", new { }));
        Assert.AreSame(part, session.Document.GetLayer(part.Id));
        CollectionAssert.AreEqual(exact, part.CopyMask(part.Bounds));
    }

    [TestMethod]
    public async Task CurrentGuardCannotReviveCandidateAfterUiEditAndUndo()
    {
        var session = LiveMcpTests.Open();
        using var mcp = await McpCoreHarness.CreateAsync(session);
        var candidate = Value(await mcp.CallAsync("part_candidates", Query(0, 4)));
        var oldRevision = session.Document!.Revision;
        session.Execute(new RenameLayer(session.Document.Base.Id, "UI"));
        session.Undo();
        Assert.AreEqual(McpErrors.StaleRevision,
            (await mcp.CallAsync("edit", Edit(Create(Id(candidate))), expectedRevision: oldRevision)).Error);
        Assert.AreEqual("stale_candidate", (await mcp.CallAsync("edit", Edit(Create(Id(candidate))))).Error);
        Assert.AreEqual(1, session.RedoCount);
    }

    [TestMethod]
    public async Task ExpiryRotationAndSameDocumentReopenInvalidateCandidates()
    {
        var session = LiveMcpTests.Open();
        var clock = new ManualClock();
        using var mcp = await McpCoreHarness.CreateAsync(session, timeProvider: clock);
        var candidate = Value(await mcp.CallAsync("part_candidates", Query(0, 100)));
        clock.Advance(LiveLimits.CandidateLifetime);
        Assert.AreEqual("stale_candidate", (await mcp.CallAsync("edit", Edit(Create(Id(candidate))))).Error);
        candidate = Value(await mcp.CallAsync("part_candidates", Query(0, 4)));
        await mcp.ReenableAsync();
        Assert.AreEqual("stale_candidate", (await mcp.CallAsync("edit", Edit(Create(Id(candidate))))).Error);
        candidate = Value(await mcp.CallAsync("part_candidates", Query(0, 4)));
        var oldToken = session.DocumentToken;
        session.Open(session.Document!);
        Assert.AreEqual(McpErrors.Unauthorized,
            (await mcp.CallAsync("edit", Edit(Create(Id(candidate))), documentToken: oldToken)).Error);
        await mcp.ReenableAsync();
        Assert.AreEqual("stale_candidate", (await mcp.CallAsync("edit", Edit(Create(Id(candidate))))).Error);
        Assert.AreEqual(0, session.UndoCount);
    }

    [TestMethod]
    public async Task MultipleCandidateBatchSharesHistoryAndFailureRestoresPriorHistory()
    {
        var session = LiveMcpTests.Open();
        using var mcp = await McpCoreHarness.CreateAsync(session);
        var candidate = Value(await mcp.CallAsync("part_candidates", Query(0, 4)));
        Value(await mcp.CallAsync("edit", Edit(Create(Id(candidate)), Create(Id(candidate, 1)),
            new { type = "layer.rename", target = "@1", name = "second" })));
        Assert.AreEqual(1, session.UndoCount);
        Assert.AreEqual(2, session.Document!.Layers.OfType<PartLayer>().Count());
        session.Undo();
        Assert.AreEqual(1, session.RedoCount);
        candidate = Value(await mcp.CallAsync("part_candidates", Query(0, 4)));
        var failed = await mcp.CallAsync("edit", Edit(Create(Id(candidate)),
            new { type = "layer.delete", target = Guid.NewGuid().ToString() }));
        Assert.IsTrue(failed.IsError);
        Assert.AreEqual(1, session.Document.Layers.Count);
        Assert.AreEqual(0, session.UndoCount);
        Assert.AreEqual(1, session.RedoCount);
        Assert.IsFalse(session.HasActiveTransaction);
    }

    [TestMethod]
    public async Task ReadOnlyCanCompareButCannotCreateAndBusyQueriesDoNotFit()
    {
        var session = LiveMcpTests.Open();
        var fitter = new CountingFitter();
        bool busy = false;
        using var mcp = await McpCoreHarness.CreateAsync(session, McpPermission.ReadOnly,
            busy: () => busy, partFitter: fitter);
        var candidate = Value(await mcp.CallAsync("part_candidates", Query(0, 4)));
        Assert.AreEqual(McpErrors.Forbidden, (await mcp.CallAsync("edit", Edit(Create(Id(candidate))))).Error);
        busy = true;
        Assert.AreEqual(McpErrors.Busy, (await mcp.CallAsync("part_candidates", Query(0, 4))).Error);
        Assert.AreEqual(1, fitter.Count);
        Assert.AreEqual(0, session.UndoCount);
    }

    [TestMethod]
    public async Task InvalidCandidateBoundsAndMixedCreateShapesFailBeforeFitting()
    {
        var session = LiveMcpTests.Open();
        var fitter = new CountingFitter();
        using var mcp = await McpCoreHarness.CreateAsync(session, partFitter: fitter);
        foreach (int[] steps in new int[][] { [], [0], [0, 1, 2, 3], [-1, 4], [0, 101], [4, 4], [8, 4] })
            Assert.IsTrue((await mcp.CallAsync("part_candidates", Query(steps))).IsError);
        Assert.IsTrue((await mcp.CallAsync("part_candidates", new { fence = Fence, steps = new[] { 0, 4 }, maxEdge = 1025 })).IsError);
        Assert.IsTrue((await mcp.CallAsync("edit", Edit(new
        {
            type = "part.create", candidateId = Guid.NewGuid().ToString("N"), fence = Fence, step = 0, name = "mixed",
        }))).IsError);
        Assert.AreEqual("stale_candidate", (await mcp.CallAsync("edit", Edit(Create(Guid.NewGuid().ToString("N"))))).Error);
        Assert.AreEqual(0, fitter.Count);
    }

    [TestMethod]
    public async Task FittingRoiLimitAppliesBeforeCandidatePreparation()
    {
        var session = new EditorSession();
        session.Open(new(new("large.png", new(258, 258), 258 * 4, new byte[258 * 258 * 4])));
        var fitter = new CountingFitter();
        using var mcp = await McpCoreHarness.CreateAsync(session, partFitter: fitter);
        var fence = new[] { new { x = 0, y = 0 }, new { x = 257, y = 0 }, new { x = 257, y = 256 }, new { x = 0, y = 256 } };
        Assert.AreEqual("fitting_limit", (await mcp.CallAsync("part_candidates", new { fence, steps = new[] { 0, 4 }, maxEdge = 32 })).Error);
        Assert.AreEqual(0, fitter.Count);
        fence[1] = new { x = 256, y = 0 }; fence[2] = new { x = 256, y = 256 };
        var result = Value(await mcp.CallAsync("part_candidates", new { fence, steps = new[] { 0, 4, 100 }, maxEdge = 1024 }));
        Assert.AreEqual(3, result.GetProperty("candidates").GetArrayLength());
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(result.GetRawText()) < LiveLimits.FrameBytes);
    }

    private sealed class CountingFitter : IPartBoundaryFitter
    {
        public int Count { get; private set; }
        public IPartFittingSession Create(OriginalAsset original, IReadOnlyList<DocumentPoint> fence)
        {
            Count++;
            return new GuriguriPartFitter().Create(original, fence);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan duration) => timestamp += (long)duration.TotalMilliseconds;
    }
}
