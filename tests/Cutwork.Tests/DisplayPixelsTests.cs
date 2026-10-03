using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class DisplayPixelsTests
{
    [TestMethod]
    public void PartAndSelectedMaskTintsPreserveDisplayBytesAndSourceMask()
    {
        var mask = new byte[] { 0, 128, 255 };
        var part = DisplayPixels.TintMask(mask, 170, 70, 235, 88);
        var selected = DisplayPixels.TintMask(mask, 58, 11, 210, 80);

        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 29, 12, 40, 44, 58, 24, 81, 88 }, part);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 9, 1, 32, 40, 18, 3, 65, 80 }, selected);
        CollectionAssert.AreEqual(new byte[] { 0, 128, 255 }, mask);
    }

    [TestMethod]
    public void PremultiplicationKeepsAlphaAndUsesCompositorRounding()
    {
        var pixels = new byte[] { 1, 2, 3, 0, 255, 128, 1, 128, 17, 33, 255, 255 };
        DisplayPixels.PremultiplyInPlace(pixels);

        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 128, 64, 1, 128, 17, 33, 255, 255 }, pixels);
    }

    [TestMethod]
    public void InvalidBgraBufferIsRejectedBeforeAnyPixelIsChanged()
    {
        var pixels = new byte[] { 255, 128, 64, 128, 1 };
        Assert.ThrowsExactly<ArgumentException>(() => DisplayPixels.PremultiplyInPlace(pixels));
        CollectionAssert.AreEqual(new byte[] { 255, 128, 64, 128, 1 }, pixels);
    }
}
