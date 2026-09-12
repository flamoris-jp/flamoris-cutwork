using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flamoris.Cutwork.Imaging.Persistence;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class FlimgValidationTests
{
    [TestMethod]
    public void MissingAndMalformedManifestAreRejected()
    {
        var valid = Entries(FlimgRoundTripTests.Write(FlimgRoundTripTests.FullDocument()));
        AssertRejected(FlimgError.MissingManifest,
            Archive(valid.Where(entry => entry.Path != "manifest.json")));
        AssertRejected(FlimgError.MalformedManifest,
            Archive(Replace(valid, "manifest.json", "{"u8.ToArray())));
    }

    [TestMethod]
    public void UnsupportedVersionHasStableRejection()
    {
        AssertRejected(FlimgError.UnsupportedVersion, MutateManifest(root =>
            root["schemaVersion"] = 99));
    }

    [TestMethod]
    public void MissingAssetAndChecksumMismatchAreRejected()
    {
        var valid = Entries(FlimgRoundTripTests.Write(FlimgRoundTripTests.FullDocument()));
        AssertRejected(FlimgError.MissingAsset,
            Archive(valid.Where(entry => entry.Path != "assets/original.png")));
        AssertRejected(FlimgError.ChecksumMismatch,
            Archive(Replace(valid, "assets/original.png", [1, 2, 3, 4])));
    }

    [TestMethod]
    public void DuplicateAndCanonicalCollidingPathsAreRejected()
    {
        var valid = Entries(FlimgRoundTripTests.Write(FlimgRoundTripTests.FullDocument()));
        AssertRejected(FlimgError.DuplicateEntry,
            Archive(valid.Concat([valid.Single(entry => entry.Path == "manifest.json")])));
        AssertRejected(FlimgError.DuplicateEntry,
            Archive(valid.Append(("MANIFEST.JSON", "{}"u8.ToArray()))));
    }

    [TestMethod]
    [DataRow("../evil.png")]
    [DataRow("/absolute.png")]
    [DataRow("C:/absolute.png")]
    [DataRow("layers\\evil.png")]
    public void UnsafeArchivePathsAreRejected(string path)
    {
        var valid = Entries(FlimgRoundTripTests.Write(FlimgRoundTripTests.FullDocument()));
        AssertRejected(FlimgError.InvalidArchivePath,
            Archive(valid.Append((path, [1]))));
    }

    [TestMethod]
    public void DuplicateLayerIdentityAndInvalidKindsAreRejected()
    {
        AssertRejected(FlimgError.InvalidIdentity, MutateManifest(root =>
        {
            var layers = root["layers"]!.AsArray();
            layers[1]!["id"] = layers[0]!["id"]!.GetValue<string>();
        }));
        AssertRejected(FlimgError.InvalidLayer, MutateManifest(root =>
            root["layers"]!.AsArray()[0]!["kind"] = "clone"));
    }

    [TestMethod]
    public void InvalidBaseCountAndPositionAreRejected()
    {
        AssertRejected(FlimgError.InvalidLayer, MutateManifest(root =>
        {
            var layers = root["layers"]!.AsArray();
            layers.Remove(layers.Single(node => node!["kind"]!.GetValue<string>() == "base"));
        }));
        AssertRejected(FlimgError.InvalidLayer, MutateManifest(root =>
        {
            var layers = root["layers"]!.AsArray();
            var baseIndex = layers.Select((node, index) => (node, index))
                .Single(item => item.node!["kind"]!.GetValue<string>() == "base").index;
            var baseNode = layers[baseIndex];
            layers.RemoveAt(baseIndex);
            layers.Insert(0, baseNode);
        }));
    }

    [TestMethod]
    public void InvalidBoundsAndPatchTransformAreRejected()
    {
        AssertRejected(FlimgError.InvalidLayer, MutateManifest(root =>
            root["layers"]!.AsArray()[0]!["bounds"]!["x"] = 99));
        AssertRejected(FlimgError.InvalidPatchTransform, MutateManifest(root =>
        {
            var patch = root["layers"]!.AsArray()
                .Single(node => node!["kind"]!.GetValue<string>() == "patch");
            patch!["transform"]!["scale"] = 10.01;
        }));
    }

    [TestMethod]
    public void AssetDimensionMismatchAndMalformedPngAreRejectedAfterHashValidation()
    {
        var wrongSize = Entries(FlimgRoundTripTests.Write(
            new Flamoris.Cutwork.Core.CutworkDocument(FlimgRoundTripTests.Original(2, 2))))
            .Single(entry => entry.Path == "assets/original.png").Bytes;
        AssertRejected(FlimgError.AssetDimensionMismatch,
            ReplaceAssetAndHash("assets/original.png", wrongSize));
        AssertRejected(FlimgError.MalformedPng,
            ReplaceAssetAndHash("assets/original.png", [1, 2, 3, 4]));
    }

    [TestMethod]
    public void DeclaredAndManifestSizeLimitsAreRejected()
    {
        AssertRejected(FlimgError.InvalidDimensions, MutateManifest(root =>
            root["canvas"]!["width"] = FlimgArchiveCodec.MaximumDimension + 1));
        var oversized = Enumerable.Repeat((byte)' ', FlimgArchiveCodec.MaximumManifestBytes + 1).ToArray();
        AssertRejected(FlimgError.SizeLimitExceeded,
            Archive([("manifest.json", oversized)]));
    }

    [TestMethod]
    public void PhysicalEntryReadsStopAtDeclaredLengthPlusOne()
    {
        using var overlong = new MemoryStream([1, 2, 3, 4, 5, 6]);
        var exception = Assert.ThrowsExactly<FlimgException>(() => FlimgArchiveCodec.ReadBounded(
            overlong, declaredLength: 4, limit: 4, new ArchiveReadBudget(100)));

        Assert.AreEqual(FlimgError.MalformedArchive, exception.Error);
        Assert.AreEqual(5, overlong.Position);

        using var tooShort = new MemoryStream([1, 2, 3]);
        exception = Assert.ThrowsExactly<FlimgException>(() => FlimgArchiveCodec.ReadBounded(
            tooShort, declaredLength: 4, limit: 4, new ArchiveReadBudget(100)));
        Assert.AreEqual(FlimgError.MalformedArchive, exception.Error);
    }

    [TestMethod]
    public void PhysicalEntryReadsEnforceCumulativeArchiveBudget()
    {
        var budget = new ArchiveReadBudget(6);
        using var first = new MemoryStream([1, 2, 3, 4]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 },
            FlimgArchiveCodec.ReadBounded(first, 4, 4, budget));

        using var second = new MemoryStream([5, 6, 7, 8]);
        var exception = Assert.ThrowsExactly<FlimgException>(() =>
            FlimgArchiveCodec.ReadBounded(second, 4, 4, budget));

        Assert.AreEqual(FlimgError.SizeLimitExceeded, exception.Error);
        Assert.AreEqual(2, budget.Remaining);
    }

    [TestMethod]
    public void ForgedDeclaredLengthIsRejectedAsMalformedArchive()
    {
        var archive = Archive([("manifest.json", "12345678"u8.ToArray())],
            CompressionLevel.NoCompression);
        PatchCentralDirectoryLength(archive, "manifest.json", 7);

        AssertRejected(FlimgError.MalformedArchive, archive);
    }

    [TestMethod]
    public void ExcessiveEntryCountIsRejectedBeforeManifestProcessing()
    {
        var entries = Enumerable.Range(0, FlimgArchiveCodec.MaximumEntries + 1)
            .Select(index => ($"entries/{index}", Array.Empty<byte>()));

        AssertRejected(FlimgError.SizeLimitExceeded, Archive(entries));
    }

    [TestMethod]
    public void MalformedZipIsRejected()
    {
        AssertRejected(FlimgError.MalformedArchive, [1, 2, 3, 4]);
    }

    private static byte[] MutateManifest(Action<JsonObject> mutate)
    {
        var valid = Entries(FlimgRoundTripTests.Write(FlimgRoundTripTests.FullDocument()));
        var manifest = JsonNode.Parse(valid.Single(entry => entry.Path == "manifest.json").Bytes)!.AsObject();
        mutate(manifest);
        return Archive(Replace(valid, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }))));
    }

    private static byte[] ReplaceAssetAndHash(string path, byte[] bytes) => MutateArchive(entries =>
    {
        var manifest = JsonNode.Parse(entries.Single(entry => entry.Path == "manifest.json").Bytes)!.AsObject();
        manifest["original"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var withAsset = Replace(entries, path, bytes);
        return Replace(withAsset, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true })));
    });

    private static byte[] MutateArchive(
        Func<List<(string Path, byte[] Bytes)>, IEnumerable<(string Path, byte[] Bytes)>> mutate)
    {
        var valid = Entries(FlimgRoundTripTests.Write(FlimgRoundTripTests.FullDocument()));
        return Archive(mutate(valid));
    }

    private static IEnumerable<(string Path, byte[] Bytes)> Replace(
        IEnumerable<(string Path, byte[] Bytes)> entries, string path, byte[] bytes) =>
        entries.Select(entry => entry.Path == path ? (path, bytes) : entry);

    private static List<(string Path, byte[] Bytes)> Entries(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return archive.Entries.Select(entry =>
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            return (entry.FullName, output.ToArray());
        }).ToList();
    }

    private static byte[] Archive(IEnumerable<(string Path, byte[] Bytes)> entries,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path, compressionLevel);
                using var target = entry.Open();
                target.Write(item.Bytes);
            }
        }
        return output.ToArray();
    }

    private static void PatchCentralDirectoryLength(byte[] archive, string path, uint length)
    {
        for (var index = 0; index <= archive.Length - 46; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(index, 4)) != 0x02014b50)
                continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(index + 28, 2));
            if (index + 46 + nameLength > archive.Length) continue;
            var name = Encoding.UTF8.GetString(archive, index + 46, nameLength);
            if (name != path) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(index + 24, 4), length);
            return;
        }
        Assert.Fail($"Central directory entry not found: {path}");
    }

    private static void AssertRejected(FlimgError expected, byte[] bytes)
    {
        FlimgAtomicSaveTests.AssertFlimg(expected, () => FlimgRoundTripTests.Read(bytes));
    }
}
