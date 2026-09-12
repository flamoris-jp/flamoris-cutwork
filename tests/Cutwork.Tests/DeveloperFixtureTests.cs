using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Imaging.Diagnostics;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class DeveloperFixtureTests
{
    [TestMethod]
    public void FixtureUsesOrdinaryTransactionAndRestoresItsComposite()
    {
        var session = EditHistoryTests.Open(); var document = session.Document!;
        session.Execute(DeveloperLayerFixture.Create(document));
        Assert.AreEqual(4, document.Layers.Count);
        Assert.AreEqual(1, session.UndoCount);
        CollectionAssert.AreEqual(new[] { LayerKind.Part, LayerKind.Base, LayerKind.Patch, LayerKind.Repair },
            document.Layers.Select(layer => layer.Kind).ToArray());
        using var cache = new CompositeCache(document); cache.RenderPending();
        var after = cache.CopyPixels();
        session.Undo(); cache.RenderPending(); Assert.AreEqual(1, document.Layers.Count);
        session.Redo(); cache.RenderPending(); CollectionAssert.AreEqual(after, cache.CopyPixels());
    }
}
