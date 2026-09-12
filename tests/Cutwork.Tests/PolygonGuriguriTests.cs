using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class PolygonGuriguriTests
{
    [TestMethod]
    public void RetainedScheduleShrinksSmoothlyAndRemainsPositive()
    {
        var counts = Enumerable.Range(0, 8)
            .Select(step => PolygonGuriguri.TargetKeepPixelsForStep(step, 10_000)).ToArray();
        Assert.AreEqual(10_000, counts[0]);
        Assert.IsTrue(counts.Zip(counts.Skip(1)).All(pair => pair.Second < pair.First));
        Assert.IsTrue(counts.All(count => count > 0));
    }

    [TestMethod]
    public void MasksAreNestedHardFencedAndRestoreExactly()
    {
        var bounds = new DocumentRect(0, 0, 25, 25);
        var polygon = new byte[625];
        for (var y = 2; y < 23; y++)
            for (var x = 2; x < 23; x++) polygon[y * 25 + x] = 255;
        var guriguri = PolygonGuriguri.FromPrepared(bounds, polygon, new float[625]);

        var large = guriguri.MaskForKeepCount(300);
        var small = guriguri.MaskForKeepCount(120);
        var restored = guriguri.MaskForKeepCount(300);

        Assert.AreEqual(300, large.Count(value => value != 0));
        Assert.AreEqual(120, small.Count(value => value != 0));
        Assert.IsTrue(small.Zip(large).All(pair => pair.First == 0 || pair.Second == 255));
        Assert.IsTrue(large.Zip(polygon).All(pair => pair.First == 0 || pair.Second == 255));
        CollectionAssert.AreEqual(large, restored);
    }

    [TestMethod]
    public void StrongInnerBoundaryResistsOuterPeel()
    {
        const int size = 41;
        var polygon = new byte[size * size];
        var boundary = new float[size * size];
        for (var y = 3; y < 38; y++)
            for (var x = 3; x < 38; x++) polygon[y * size + x] = 255;
        for (var x = 12; x <= 28; x++) boundary[12 * size + x] = boundary[28 * size + x] = 95;
        for (var y = 12; y <= 28; y++) boundary[y * size + 12] = boundary[y * size + 28] = 95;
        var guriguri = PolygonGuriguri.FromPrepared(new(0, 0, size, size), polygon, boundary);

        var mask = guriguri.MaskForKeepCount(400);

        Assert.AreEqual(0, mask[4 * size + 4]);
        Assert.AreEqual(255, mask[20 * size + 20]);
    }

    [TestMethod]
    public void BoundaryPreparationIsDeterministicBoundedAndFindsContrast()
    {
        var pixels = new byte[60 * 40 * 4];
        for (var y = 0; y < 40; y++)
            for (var x = 0; x < 60; x++)
            {
                var value = (byte)(x < 30 ? 80 : 220);
                var offset = (y * 60 + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        var original = new OriginalAsset("fixture.png", new(60, 40), 240, pixels);

        var first = GuriguriBoundaryMap.Build(original, new(0, 0, 60, 40));
        var second = GuriguriBoundaryMap.Build(original, new(0, 0, 60, 40));

        CollectionAssert.AreEqual(first, second);
        Assert.IsTrue(first.All(value => value is >= 0 and <= 100));
        Assert.IsTrue(Mean(first, 60, 29, 32) > Mean(first, 60, 5, 10));
    }

    [TestMethod]
    public void FixedOriginalAndFenceProduceFixedMask()
    {
        var pixels = Enumerable.Repeat(new byte[] { 90, 90, 90, 255 }, 9 * 9)
            .SelectMany(value => value).ToArray();
        var original = new OriginalAsset("flat.png", new(9, 9), 36, pixels);
        var fence = new[]
        {
            new DocumentPoint(1, 1), new DocumentPoint(7, 1),
            new DocumentPoint(7, 7), new DocumentPoint(1, 7),
        };
        var first = PolygonGuriguri.Create(original, fence);
        var second = PolygonGuriguri.Create(original, fence);

        var firstMask = first.Adjust(2);
        var secondMask = second.Adjust(2);

        Assert.AreEqual(new DocumentRect(1, 1, 7, 7), first.Bounds);
        Assert.AreEqual(49, first.PolygonPixelCount);
        Assert.AreEqual(first.PolygonPixelCount, second.PolygonPixelCount);
        CollectionAssert.AreEqual(firstMask, secondMask);
        Assert.AreEqual(255, firstMask[4 * first.Bounds.Width + 4]);
        Assert.AreEqual(
            "68774B530E6104A0F4E1B106F647D62215F7DA4C62384C6D85FD4A513C93AA20",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(firstMask)));
    }

    private static double Mean(float[] values, int width, int left, int right)
    {
        var selected = new List<float>();
        for (var y = 0; y < values.Length / width; y++)
            for (var x = left; x < right; x++) selected.Add(values[y * width + x]);
        return selected.Average(value => value);
    }
}
