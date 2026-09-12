using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class PatchWorkflowTests
{
    [TestMethod]
    public void SourceFreezesStraightBgraFromImmutableOriginalOnly()
    {
        var original = Original();
        var before = original.CopyPixelBytes();
        var polygon = new[] { new DocumentPoint(1, 1), new(4, 1), new(4, 4), new(1, 4) };

        var frozen = new PatchSourceSampler().Freeze(original, polygon);
        var bytes = frozen.StraightBgra.ToArray();
        var sourcePixel = original.PixelAt(2, 2).ToArray();

        CollectionAssert.AreEqual(sourcePixel, bytes.AsSpan(((2 - frozen.SourceBounds.Y) * frozen.SourceBounds.Width
            + 2 - frozen.SourceBounds.X) * 4, 4).ToArray());
        CollectionAssert.AreEqual(before, original.CopyPixelBytes());
        var copy = frozen.StraightBgra.ToArray(); copy[0] ^= 255;
        Assert.AreNotEqual(copy[0], frozen.StraightBgra.Span[0]);
    }

    [TestMethod]
    public void PatchTranslationScaleAndRotationAreDeterministic()
    {
        var source = new byte[]
        {
            10, 20, 30, 255, 40, 50, 60, 255,
            70, 80, 90, 255, 100, 110, 120, 255,
        };
        var patch = new PatchLayer(new(1, 1, 2, 2), source,
            new PatchTransform(5, 5, 1, 0));
        CollectionAssert.AreEqual(source.AsSpan(0, 4).ToArray(), patch.SampleAt(4, 4).ToArray());

        patch = new PatchLayer(new(1, 1, 2, 2), source, new PatchTransform(6, 6, 2, 0));
        CollectionAssert.AreEqual(source.AsSpan(0, 4).ToArray(), patch.SampleAt(4, 4).ToArray());

        patch = new PatchLayer(new(1, 1, 2, 2), source, new PatchTransform(5, 5, 1, 90));
        CollectionAssert.AreEqual(source.AsSpan(4, 4).ToArray(), patch.SampleAt(5, 5).ToArray());
    }

    [TestMethod]
    public void ScaleIsClampedToAcceptedMaximum()
    {
        Assert.AreEqual(10, new PatchTransform(10, 10, 10).Scale);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new PatchTransform(10, 10, 10.01));
    }

    [TestMethod]
    public void CommitIsOneUnderpaintEntryAndUndoRedoPreserveIdentityContentAndTransform()
    {
        var session = Session();
        var tool = new PatchToolController(session, new PatchSourceSampler());
        tool.Activate();
        AddSource(tool);
        Assert.IsTrue(tool.FinalizeSource());
        var source = tool.Snapshot().Source!.StraightBgra.ToArray();
        var transform = new PatchTransform(6, 6, 1.5, 30);
        tool.SetPendingTransform(transform);

        var patch = tool.Commit()!;

        Assert.AreEqual(1, session.UndoCount);
        Assert.IsTrue(session.Document!.Layers.ToList().IndexOf(patch)
            > session.Document.Layers.ToList().IndexOf(session.Document.Base));
        Assert.AreEqual(transform, patch.Transform);
        CollectionAssert.AreEqual(source, patch.CopySourcePixels());
        var id = patch.Id;
        session.Undo();
        Assert.IsFalse(session.Document.Layers.Any(layer => layer.Id == id));
        session.Redo();
        Assert.AreSame(patch, session.Document.GetLayer(id));
        Assert.AreEqual(transform, patch.Transform);
        CollectionAssert.AreEqual(source, patch.CopySourcePixels());
    }

    [TestMethod]
    public void CancelLeavesAuthoredStateHistoryAndRevisionUntouched()
    {
        var session = Session();
        session.MarkSaved();
        var revision = session.Document!.Revision;
        var tool = new PatchToolController(session, new PatchSourceSampler());
        tool.Activate(); AddSource(tool); tool.FinalizeSource();
        tool.SetPendingTransform(new PatchTransform(6, 6, 1.2, 15));

        tool.Cancel();

        Assert.AreEqual(1, session.Document.Layers.Count);
        Assert.AreEqual(0, session.UndoCount);
        Assert.AreEqual(revision, session.Document.Revision);
        Assert.IsFalse(session.IsDirty);
    }

    [TestMethod]
    public void TransformCommandDirtiesOldAndNewBoundsAndRestoresExactly()
    {
        var session = Session();
        var patch = new PatchLayer(new(1, 1, 2, 2), new byte[16]);
        session.Execute(new AddLayer(patch)); session.MarkSaved();
        DocumentChange? change = null;
        session.Document!.Changed += (_, value) => change = value;
        var before = patch.Transform;
        var oldBounds = patch.Bounds;
        var after = new PatchTransform(6, 6, 2, 45);

        session.Execute(new SetPatchTransform(patch.Id, after));

        Assert.AreEqual(oldBounds.Union(patch.Bounds), change!.DirtyRegion);
        Assert.AreEqual(after, patch.Transform);
        session.Undo(); Assert.AreEqual(before, patch.Transform);
        session.Redo(); Assert.AreEqual(after, patch.Transform);
    }

    [TestMethod]
    public void TransformedPatchRendersBelowBaseThroughAuthoredHole()
    {
        var session = Session();
        var hole = new DocumentRect(6, 6, 1, 1);
        var part = new PartLayer(hole, new byte[] { 255 });
        var patchPixel = new byte[] { 200, 100, 50, 255 };
        var patch = new PatchLayer(new(1, 1, 1, 1), patchPixel,
            new PatchTransform(6.5, 6.5));
        session.Execute(new AddLayer(part), new SetLayerVisibility(part.Id, false), new AddLayer(patch));

        using var cache = new CompositeCache(session.Document!);
        cache.RenderPending();

        CollectionAssert.AreEqual(patchPixel,
            cache.CopyPixels().AsSpan((6 * 12 + 6) * 4, 4).ToArray());
        Assert.IsTrue(session.Document.Layers.ToList().IndexOf(patch)
            > session.Document.Layers.ToList().IndexOf(session.Document.Base));
    }

    [TestMethod]
    public void ViewportChangesDoNotAlterPendingOrAuthoredPatchTransform()
    {
        var session = Session();
        var tool = new PatchToolController(session, new PatchSourceSampler());
        tool.Activate(); AddSource(tool); tool.FinalizeSource();
        var expected = new PatchTransform(6, 6, 1.5, -20);
        tool.SetPendingTransform(expected);

        session.Viewport.ZoomAt(new(0, 0), 3); session.Viewport.PanBy(20, -4);

        Assert.AreEqual(expected, tool.Snapshot().Transform);
        var patch = tool.Commit()!;
        Assert.AreEqual(expected, patch.Transform);
    }

    private static void AddSource(PatchToolController tool)
    {
        tool.PointerDown(new(1, 1), 1, CanvasModifiers.None);
        tool.PointerDown(new(4, 1), 1, CanvasModifiers.None);
        tool.PointerDown(new(4, 4), 1, CanvasModifiers.None);
        tool.PointerDown(new(1, 4), 1, CanvasModifiers.None);
    }

    private static EditorSession Session()
    {
        var session = new EditorSession();
        session.Open(new CutworkDocument(Original()));
        return session;
    }

    private static OriginalAsset Original()
    {
        var pixels = new byte[12 * 12 * 4];
        for (var y = 0; y < 12; y++)
        for (var x = 0; x < 12; x++)
        {
            var offset = (y * 12 + x) * 4;
            pixels[offset] = (byte)(x * 10);
            pixels[offset + 1] = (byte)(y * 10);
            pixels[offset + 2] = (byte)(x + y);
            pixels[offset + 3] = 255;
        }
        return new OriginalAsset("fixture.png", new(12, 12), 48, pixels);
    }
}
