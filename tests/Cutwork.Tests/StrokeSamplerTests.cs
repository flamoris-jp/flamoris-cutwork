using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class StrokeSamplerTests
{
    [TestMethod]
    public void SparseInputProducesContinuousRadiusBasedSamples()
    {
        var sampler = new StrokeSampler(4);
        var points = sampler.Begin(new(0, 0)).Concat(sampler.Add(new(10, 0))).ToArray();

        CollectionAssert.AreEqual(new[] { 0d, 2d, 4d, 6d, 8d, 10d }, points.Select(point => point.X).ToArray());
        Assert.IsTrue(points.Zip(points.Skip(1)).All(pair => pair.Second.X - pair.First.X <= 4));
    }

    [TestMethod]
    public void DuplicateAndSubSpacingInputDoNotCreatePathologicalOutput()
    {
        var sampler = new StrokeSampler(4);
        sampler.Begin(new(1, 1));

        Assert.AreEqual(0, sampler.Add(new(1, 1)).Count);
        Assert.AreEqual(0, sampler.Add(new(1.2, 1)).Count);
        Assert.AreEqual(1, sampler.Add(new(3.1, 1)).Count);
    }

    [TestMethod]
    public void SpacingScalesWithDocumentRadius()
    {
        Assert.AreEqual(1, new StrokeSampler(2).Spacing);
        Assert.AreEqual(4, new StrokeSampler(8).Spacing);
    }

    [TestMethod]
    public void SamplingDoesNotDependOnViewportState()
    {
        var first = new StrokeSampler(3);
        var second = new StrokeSampler(3);
        var a = first.Begin(new(2, 4)).Concat(first.Add(new(12, 4))).ToArray();
        var viewport = new ViewportTransform();
        viewport.ZoomAt(new(0, 0), 4); viewport.PanBy(19, -7);
        var b = second.Begin(new(2, 4)).Concat(second.Add(new(12, 4))).ToArray();

        CollectionAssert.AreEqual(a, b);
    }

    [TestMethod]
    public void LongSparseInputIsPartitionedIntoBoundedOrderedBatches()
    {
        var sampler = new StrokeSampler(1);
        sampler.Begin(new(0, 0));
        var samples = sampler.Add(new(1000, 1000));
        var batches = StrokeSampler.Batch(samples).ToArray();

        Assert.IsTrue(samples.Count > StrokeSampler.MaximumBatchSamples);
        Assert.IsTrue(batches.Length > 1);
        Assert.IsTrue(batches.All(batch => batch.Count is > 0
            && batch.Count <= StrokeSampler.MaximumBatchSamples));
        CollectionAssert.AreEqual(samples.ToArray(), batches.SelectMany(batch => batch).ToArray());
    }
}
