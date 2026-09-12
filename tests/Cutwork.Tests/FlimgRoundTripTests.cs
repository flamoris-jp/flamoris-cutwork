using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Imaging.Persistence;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class FlimgRoundTripTests
{
    [TestMethod]
    public void OriginalAndBaseRoundTripWithStableIdentity()
    {
        var documentId = Guid.NewGuid();
        var baseId = Guid.NewGuid();
        var original = Original(5, 4);
        var document = CutworkDocument.Restore(documentId, original,
        [
            new BaseLayerRestoreState(baseId, "base", "canvas", false),
        ]);

        var loaded = RoundTrip(document);

        Assert.AreEqual(documentId, loaded.Id);
        Assert.AreEqual(baseId, loaded.Base.Id);
        Assert.AreEqual("base", loaded.Base.Name);
        Assert.AreEqual("canvas", loaded.Base.SemanticName);
        Assert.IsFalse(loaded.Base.Visible);
        Assert.AreEqual(original.SourceName, loaded.Original.SourceName);
        CollectionAssert.AreEqual(original.CopyPixelBytes(), loaded.Original.CopyPixelBytes());
    }

    [TestMethod]
    public void EveryAuthoredLayerRoundTripsExactBytesOrderMetadataAndComposite()
    {
        var document = FullDocument();
        using var beforeCache = new CompositeCache(document);
        var before = beforeCache.RenderPending()!.PremultipliedBgra.ToArray();

        var loaded = RoundTrip(document);
        using var afterCache = new CompositeCache(loaded);
        var after = afterCache.RenderPending()!.PremultipliedBgra.ToArray();

        Assert.AreEqual(document.Id, loaded.Id);
        CollectionAssert.AreEqual(document.Layers.Select(layer => layer.Id).ToArray(),
            loaded.Layers.Select(layer => layer.Id).ToArray());
        for (var index = 0; index < document.Layers.Count; index++)
        {
            var expected = document.Layers[index];
            var actual = loaded.Layers[index];
            Assert.AreEqual(expected.Kind, actual.Kind);
            Assert.AreEqual(expected.Name, actual.Name);
            Assert.AreEqual(expected.SemanticName, actual.SemanticName);
            Assert.AreEqual(expected.Visible, actual.Visible);
            if (expected is PartLayer expectedPart)
                CollectionAssert.AreEqual(expectedPart.CopyMask(expectedPart.Bounds),
                    ((PartLayer)actual).CopyMask(actual.Bounds));
            if (expected is PatchLayer expectedPatch)
            {
                var actualPatch = (PatchLayer)actual;
                Assert.AreEqual(expectedPatch.SourceBounds, actualPatch.SourceBounds);
                Assert.AreEqual(expectedPatch.Transform, actualPatch.Transform);
                CollectionAssert.AreEqual(expectedPatch.CopySourcePixels(), actualPatch.CopySourcePixels());
                CollectionAssert.AreEqual(expectedPatch.SourcePolygon.ToArray(),
                    actualPatch.SourcePolygon.ToArray());
            }
            if (expected is RepairLayer expectedRepair)
                CollectionAssert.AreEqual(expectedRepair.CopyPixels(expectedRepair.Bounds),
                    ((RepairLayer)actual).CopyPixels(actual.Bounds));
        }
        CollectionAssert.AreEqual(document.Original.CopyPixelBytes(), loaded.Original.CopyPixelBytes());
        CollectionAssert.AreEqual(before, after);
    }

    [TestMethod]
    public void LoadedSessionIsCleanHasNoHistoryAndDoesNotPersistSessionState()
    {
        var sourceSession = new EditorSession();
        sourceSession.Open(FullDocument());
        sourceSession.Viewport.Fit(sourceSession.Document!.Dimensions, new(400, 300));
        sourceSession.Viewport.PanBy(17, -9);
        sourceSession.SetPreviewSource(PreviewSource.Original);
        var bytes = Write(sourceSession.Document);

        var json = ReadManifest(bytes);
        Assert.IsFalse(json.Contains("history", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("viewport", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("selection", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("tool", StringComparison.OrdinalIgnoreCase));

        var session = new EditorSession();
        session.Open(Read(bytes));
        Assert.AreEqual(0, session.UndoCount);
        Assert.AreEqual(0, session.RedoCount);
        Assert.IsFalse(session.IsDirty);
        Assert.AreEqual(PreviewSource.Composite, session.PreviewSource);
        Assert.AreEqual(1.0, session.Viewport.Zoom);
        Assert.AreEqual(session.Document!.Base.Id, session.SelectedLayerId);
    }

    [TestMethod]
    public void ArchiveUsesCanonicalPathsAndStableLayerAssetNames()
    {
        var document = FullDocument();
        var bytes = Write(document);
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var expected = new List<string> { "manifest.json", "assets/original.png" };
        expected.AddRange(document.Layers.Where(layer => layer is not BaseLayer).Select(layer =>
            $"layers/{layer.Id:N}/{(layer is PartLayer ? "mask.png" : "pixels.png")}"));
        CollectionAssert.AreEqual(expected, archive.Entries.Select(entry => entry.FullName).ToList());
        Assert.IsTrue(archive.Entries.All(entry => entry.LastWriteTime.Year == 1980));
    }

    internal static CutworkDocument FullDocument()
    {
        var dimensions = new PixelSize(8, 6);
        var original = Original(dimensions.Width, dimensions.Height);
        var partBounds = new DocumentRect(1, 1, 3, 2);
        var patchBounds = new DocumentRect(2, 2, 2, 2);
        var transform = new PatchTransform(4, 3, 1.25, 15);
        return CutworkDocument.Restore(Guid.NewGuid(), original,
        [
            new PartLayerRestoreState(Guid.NewGuid(), "eye / left", "eye_left", true,
                partBounds, new byte[] { 0, 17, 128, 200, 254, 255 }),
            new PartLayerRestoreState(Guid.NewGuid(), "eye / left", null, false,
                new(5, 1, 2, 2), new byte[] { 255, 64, 32, 0 }),
            new BaseLayerRestoreState(Guid.NewGuid(), "base", null, true),
            new PatchLayerRestoreState(Guid.NewGuid(), "patch", "underpaint", true,
                patchBounds, Pixels(2, 2, 29, transparent: true), transform,
                [new(2, 2), new(4, 2), new(4, 4), new(2, 4)]),
            new RepairLayerRestoreState(Guid.NewGuid(), "repair", "paint", false,
                new(0, 4, 3, 2), Pixels(3, 2, 83, transparent: true)),
        ]);
    }

    internal static byte[] Write(CutworkDocument document)
    {
        using var stream = new MemoryStream();
        new FlimgArchiveCodec().Write(stream, document);
        return stream.ToArray();
    }

    internal static CutworkDocument Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return new FlimgArchiveCodec().Read(stream);
    }

    private static CutworkDocument RoundTrip(CutworkDocument document) => Read(Write(document));

    internal static string ReadManifest(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static OriginalAsset Original(int width, int height)
    {
        var pixels = Pixels(width, height, 11, transparent: true);
        return new("imported artwork.jpg", new(width, height), width * 4, pixels);
    }

    internal static byte[] Pixels(int width, int height, byte seed, bool transparent = false)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < width * height; index++)
        {
            pixels[index * 4] = (byte)(seed + index * 3);
            pixels[index * 4 + 1] = (byte)(seed + index * 5);
            pixels[index * 4 + 2] = (byte)(seed + index * 7);
            pixels[index * 4 + 3] = transparent && index % 3 == 0 ? (byte)(index * 11) : (byte)255;
        }
        return pixels;
    }
}
