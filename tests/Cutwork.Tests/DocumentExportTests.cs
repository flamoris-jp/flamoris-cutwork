using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Imaging.Export;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class DocumentExportTests
{
    [TestMethod]
    public void CompositeExportMatchesFullDocumentCompositorAndIgnoresViewport()
    {
        using var directory = new FlimgAtomicSaveTests.TemporaryDirectory();
        var first = Path.Combine(directory.Path, "first.png");
        var second = Path.Combine(directory.Path, "second.png");
        var document = OpaqueDocument();
        using var cache = new CompositeCache(document);
        var expected = cache.RenderPending()!.PremultipliedBgra;
        var service = new DocumentExportService();

        service.ExportComposite(document, first);
        var session = new EditorSession();
        session.Open(document);
        session.Viewport.Fit(document.Dimensions, new(777, 333));
        session.Viewport.PanBy(53, -17);
        session.Viewport.ZoomAt(new(100, 80), 3.7);
        service.ExportComposite(document, second);

        CollectionAssert.AreEqual(File.ReadAllBytes(first), File.ReadAllBytes(second));
        var imported = new ImageImportService().Import(first);
        Assert.IsTrue(imported.IsSuccess);
        Assert.AreEqual(document.Dimensions, imported.Original!.Dimensions);
        CollectionAssert.AreEqual(expected, imported.Original.CopyPixelBytes());
        Assert.AreEqual(0L, document.Revision);
    }

    [TestMethod]
    public void HandoffJsonAndFilenamesAreDeterministicAndCollisionResistant()
    {
        using var directory = new FlimgAtomicSaveTests.TemporaryDirectory();
        var first = Path.Combine(directory.Path, "first.zip");
        var second = Path.Combine(directory.Path, "second.zip");
        var document = FlimgRoundTripTests.FullDocument();
        var service = new DocumentExportService();

        service.ExportLayerHandoff(document, first);
        service.ExportLayerHandoff(document, second);

        var firstEntries = ReadEntries(first);
        var secondEntries = ReadEntries(second);
        CollectionAssert.AreEqual(firstEntries.Keys.ToArray(), secondEntries.Keys.ToArray());
        foreach (var path in firstEntries.Keys)
            CollectionAssert.AreEqual(firstEntries[path], secondEntries[path]);
        var assetNames = firstEntries.Keys.Where(path => path.EndsWith(".png", StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(document.Layers.Count - 1, assetNames.Length);
        Assert.AreEqual(assetNames.Length, assetNames.Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(assetNames.All(path => !path.Contains("eye / left", StringComparison.Ordinal)));

        using var json = JsonDocument.Parse(firstEntries["handoff.json"]);
        var layers = json.RootElement.GetProperty("layers").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(0, layers.Length).ToArray(),
            layers.Select(layer => layer.GetProperty("order").GetInt32()).ToArray());
        CollectionAssert.AreEqual(document.Layers.Select(layer => layer.Id.ToString("D")).ToArray(),
            layers.Select(layer => layer.GetProperty("id").GetString()).ToArray());
        Assert.AreEqual("base", layers.Single(layer => layer.GetProperty("kind").GetString() == "base")
            .GetProperty("kind").GetString());
    }

    [TestMethod]
    public void HandoffPartPatchAndRepairAssetsFollowFixedConventions()
    {
        using var directory = new FlimgAtomicSaveTests.TemporaryDirectory();
        var path = Path.Combine(directory.Path, "handoff.zip");
        var document = FlimgRoundTripTests.FullDocument();
        new DocumentExportService().ExportLayerHandoff(document, path);
        var entries = ReadEntries(path);
        using var json = JsonDocument.Parse(entries["handoff.json"]);
        var layers = json.RootElement.GetProperty("layers").EnumerateArray().ToArray();

        var part = (PartLayer)document.Layers.First(layer => layer is PartLayer);
        var partJson = layers.Single(layer => layer.GetProperty("id").GetString() == part.Id.ToString("D"));
        var partAsset = DecodeEntry(entries[partJson.GetProperty("asset").GetString()!], part.Bounds);
        for (var y = part.Bounds.Y; y < part.Bounds.Bottom; y++)
        for (var x = part.Bounds.X; x < part.Bounds.Right; x++)
        {
            var offset = ((y - part.Bounds.Y) * part.Bounds.Width + x - part.Bounds.X) * 4;
            CollectionAssert.AreEqual(document.Original.PixelAt(x, y)[..3].ToArray(),
                partAsset.AsSpan(offset, 3).ToArray());
            Assert.AreEqual(CompositeCache.Multiply(document.Original.PixelAt(x, y)[3], part.MaskAt(x, y)),
                partAsset[offset + 3]);
        }

        var patch = document.Layers.OfType<PatchLayer>().Single();
        var patchJson = layers.Single(layer => layer.GetProperty("id").GetString() == patch.Id.ToString("D"));
        Assert.AreEqual(patch.Bounds.X, patchJson.GetProperty("bounds").GetProperty("x").GetInt32());
        Assert.AreEqual(patch.SourceBounds.X,
            patchJson.GetProperty("sourceBounds").GetProperty("x").GetInt32());
        Assert.AreEqual(patch.Transform.Scale,
            patchJson.GetProperty("transform").GetProperty("scale").GetDouble());
        CollectionAssert.AreEqual(patch.CopySourcePixels(), DecodeEntry(
            entries[patchJson.GetProperty("asset").GetString()!], patch.SourceBounds));

        var repair = document.Layers.OfType<RepairLayer>().Single();
        var repairJson = layers.Single(layer => layer.GetProperty("id").GetString() == repair.Id.ToString("D"));
        CollectionAssert.AreEqual(repair.CopyPixels(repair.Bounds), DecodeEntry(
            entries[repairJson.GetProperty("asset").GetString()!], repair.Bounds));
    }

    private static Dictionary<string, byte[]> ReadEntries(string path)
    {
        using var file = File.OpenRead(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            result.Add(entry.FullName, output.ToArray());
        }
        return result;
    }

    private static byte[] DecodeEntry(byte[] png, DocumentRect bounds)
    {
        using var directory = new FlimgAtomicSaveTests.TemporaryDirectory();
        var path = Path.Combine(directory.Path, "asset.png");
        File.WriteAllBytes(path, png);
        var result = new ImageImportService().Import(path);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(new PixelSize(bounds.Width, bounds.Height), result.Original!.Dimensions);
        return result.Original.CopyPixelBytes();
    }

    private static CutworkDocument OpaqueDocument()
    {
        var original = new OriginalAsset("opaque", new(3, 2), 12,
            Enumerable.Repeat(new byte[] { 10, 20, 30, 255 }, 6).SelectMany(pixel => pixel).ToArray());
        return CutworkDocument.Restore(Guid.NewGuid(), original,
        [
            new PartLayerRestoreState(Guid.NewGuid(), "part", null, true,
                new(1, 0, 1, 1), new byte[] { 255 }),
            new BaseLayerRestoreState(Guid.NewGuid(), "base", null, true),
            new RepairLayerRestoreState(Guid.NewGuid(), "repair", null, true,
                new(1, 0, 1, 1), new byte[] { 80, 90, 100, 255 }),
        ]);
    }
}
