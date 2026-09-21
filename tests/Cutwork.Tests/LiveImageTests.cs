using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Mcp;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LiveImageTests
{
    private static readonly object[] Fence =
        [new { x = 4, y = 4 }, new { x = 20, y = 4 }, new { x = 20, y = 20 }, new { x = 4, y = 20 }];

    [TestMethod]
    public Task PurePreviewReturnsSamePngMaskAsCreateWithoutTouchingSession() => Sta(async () =>
    {
        var session = LiveMcpTests.Open();
        using var mcp = await McpCoreHarness.CreateAsync(session);
        var selection = session.SelectedLayerId;
        var revision = session.Document!.Revision;
        var preview = await mcp.CallAsync("part_preview", new { fence = Fence, step = 2, maxEdge = 1024 });
        Assert.IsFalse(preview.IsError, preview.Error);
        Assert.AreEqual(revision, session.Document.Revision);
        Assert.AreEqual(selection, session.SelectedLayerId);
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsFalse(session.IsDirty);
        var value = preview.Value!.Value;
        Assert.AreEqual("image/png", value.GetProperty("mimeType").GetString());
        var rendered = Decode(Convert.FromBase64String(value.GetProperty("pngBase64").GetString()!));

        var create = await mcp.CallAsync("edit", new
        {
            operations = new[] { new { type = "part.create", fence = Fence, step = 2, name = "pure" } },
        });
        Assert.IsFalse(create.IsError, create.Error);
        var part = session.Document.Layers.OfType<PartLayer>().Single();
        var mask = part.CopyMask(part.Bounds);
        for (int i = 0; i < mask.Length; i++) Assert.AreEqual(mask[i], rendered[i * 4]);
    });

    [TestMethod]
    public Task RoiCompositeMatchesExistingCacheAndExactCoordinateMapping() => Sta(() =>
    {
        var session = LiveMcpTests.Open();
        session.Execute(new AddLayer(new PartLayer(new(4, 4, 8, 8),
            Enumerable.Repeat((byte)255, 64).ToArray())));
        session.Execute(new SetLayerVisibility(session.Document!.Layers.OfType<PartLayer>().Single().Id, false));
        using var cache = new CompositeCache(session.Document);
        cache.RenderPending();
        var all = cache.CopyPixels();
        var image = LiveImages.Capture(session.Document, "Composite", null, new(4, 4, 8, 8), 4, default);
        Assert.AreEqual(2d, image.DocumentPixelsPerOutputX);
        Assert.AreEqual(new DocumentRect(4, 4, 8, 8), image.Crop);
        var actual = Decode(image.Png);
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
            CollectionAssert.AreEqual(all.Skip(((4 + y * 2 + 1) * 32 + 4 + x * 2 + 1) * 4).Take(4).ToArray(),
                actual.Skip((y * 4 + x) * 4).Take(4).ToArray());
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ReplacementRevokesBeforeAnotherImageCanBeDisclosed() => Sta(async () =>
    {
        var session = LiveMcpTests.Open();
        using var old = await McpCoreHarness.CreateAsync(session);
        var oldToken = session.DocumentToken;
        session.Open(new CutworkDocument(session.Document!.Original));
        var result = await old.CallAsync("image",
            new { source = "Original", target = (string?)null, roi = (object?)null, maxEdge = 32 },
            documentToken: oldToken);
        Assert.AreEqual(McpErrors.Unauthorized, result.Error);
        Assert.IsNull(result.Value);
    });

    [TestMethod]
    public Task PreviewEdgeAndEncodedPngBudgetRejectOversizedReadback() => Sta(async () =>
    {
        var pixels = new byte[1024 * 1024 * 4];
        new Random(33).NextBytes(pixels);
        var session = new EditorSession();
        session.Open(new(new("noise.png", new(1024, 1024), 4096, pixels)));
        using var mcp = await McpCoreHarness.CreateAsync(session, McpPermission.ReadOnly);
        var small = await mcp.CallAsync("image",
            new { source = "Original", target = (string?)null, roi = (object?)null, maxEdge = 256 });
        Assert.IsFalse(small.IsError, small.Error);
        Assert.IsTrue(Convert.FromBase64String(small.Value!.Value.GetProperty("pngBase64").GetString()!).Length
            <= LiveLimits.PngBytes);
        foreach (int edge in new[] { 1024, 1025 })
        {
            var large = await mcp.CallAsync("image",
                new { source = "Original", target = (string?)null, roi = (object?)null, maxEdge = edge });
            Assert.IsTrue(large.IsError);
            Assert.IsNull(large.Value);
        }
        Assert.AreEqual(0L, session.Document!.Revision);
        Assert.AreEqual(0, session.UndoCount);
    });

    private static byte[] Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        converted.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        return pixels;
    }

    private static Task Sta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception exception) { done.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
