using System.IO;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Imaging.Persistence;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class PortableImageTests
{
    [TestMethod]
    public void StraightBgraAndGrayPreserveEveryAlphaIncludingInvisibleColor()
    {
        var pixels = new byte[256 * 4];
        for (int i = 0; i < 256; i++) { pixels[i * 4] = 17; pixels[i * 4 + 1] = 89; pixels[i * 4 + 2] = 231; pixels[i * 4 + 3] = (byte)i; }
        var png = PngAssetCodec.EncodeBgra32(new(256, 1), pixels);
        Assert.AreEqual((byte)6, png[25]);
        CollectionAssert.AreEqual(pixels, PngAssetCodec.DecodeBgra32(png, new(256, 1)));
        var mask = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var gray = PngAssetCodec.EncodeGray8(new(256, 1), mask);
        Assert.AreEqual((byte)0, gray[25]);
        CollectionAssert.AreEqual(mask, PngAssetCodec.DecodeGray8(gray, new(256, 1)));
    }

    [TestMethod]
    public void ImportRejectsCorruptionAndCanonicalAssetsCheckDimensions()
    {
        using var directory = new FlimgAtomicSaveTests.TemporaryDirectory();
        var png = RasterPng.Encode(new(2, 1), new byte[] { 10, 20, 30, 255, 40, 50, 60, 128 });
        Assert.ThrowsExactly<FlimgException>(() => PngAssetCodec.DecodeBgra32(png, new(3, 1)));
        png[^5] ^= 1;
        File.WriteAllBytes(Path.Combine(directory.Path, "bad.png"), png);
        Assert.IsFalse(new ImageImportService().Import(Path.Combine(directory.Path, "bad.png")).IsSuccess);
    }

    [TestMethod]
    public void PremultipliedPngUnpremultipliesOnce()
    {
        var png = RasterPng.Encode(new(1, 1), new byte[] { 10, 20, 30, 128 }, premultiplied: true);
        var straight = PngAssetCodec.DecodeBgra32(png, new(1, 1));
        CollectionAssert.AreEqual(new byte[] { 20, 40, 60, 128 }, straight);
    }
}
