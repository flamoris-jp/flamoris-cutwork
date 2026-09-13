using Flamoris.Cutwork.App;
using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class Phase91WorkflowTests
{
    [TestMethod]
    public void PartsProjectionOrderIsIndependentFromLayerDrawOrder()
    {
        var session = EditHistoryTests.Open();
        var first = EditHistoryTests.Part();
        var second = EditHistoryTests.Part(1);
        var third = new PartLayer(new DocumentRect(2, 0, 2, 2), [255, 255, 255, 255]);
        session.Execute(new AddLayer(first), new AddLayer(second), new AddLayer(third));

        var partOrderBefore = PartLayerProjection.Create(session.Document).Select(part => part.Id).ToArray();
        var drawOrderBefore = session.Document!.Layers.OfType<PartLayer>().Select(part => part.Id).ToArray();
        session.Execute(new ReorderLayer(drawOrderBefore[0], 2));

        var partOrderAfter = PartLayerProjection.Create(session.Document).Select(part => part.Id).ToArray();
        var drawOrderAfter = session.Document.Layers.OfType<PartLayer>().Select(part => part.Id).ToArray();
        CollectionAssert.AreEqual(partOrderBefore, partOrderAfter);
        CollectionAssert.AreNotEqual(drawOrderBefore, drawOrderAfter);
        CollectionAssert.AreEqual(partOrderAfter.OrderBy(id => id).ToArray(), partOrderAfter);
    }

    [TestMethod]
    public void RenameAndSemanticNamePreserveStablePartIdentityAndUndoTogether()
    {
        var session = EditHistoryTests.Open();
        var part = EditHistoryTests.Part();
        session.Execute(new AddLayer(part));
        session.Open(session.Document!);
        session.SelectLayer(part.Id);

        session.Execute(new RenameLayer(part.Id, "Left eye"),
            new SetLayerSemanticName(part.Id, "eye_left"));

        Assert.AreEqual(part.Id, session.SelectedLayerId);
        Assert.AreEqual("Left eye", part.Name);
        Assert.AreEqual("eye_left", part.SemanticName);
        session.Undo();
        Assert.AreEqual(part.Id, session.SelectedLayerId);
        Assert.AreEqual("", part.Name);
        Assert.IsNull(part.SemanticName);
        session.Redo();
        Assert.AreEqual("Left eye", part.Name);
        Assert.AreEqual("eye_left", part.SemanticName);
    }

    [TestMethod]
    public void SemanticNameAllowsFreeTextAndNormalizesOnlyOuterWhitespace()
    {
        var session = EditHistoryTests.Open();
        var part = EditHistoryTests.Part();
        session.Execute(new AddLayer(part));
        session.Open(session.Document!);

        session.Execute(new SetLayerSemanticName(part.Id, "  costume_ribbon_07  "));
        Assert.AreEqual("costume_ribbon_07", part.SemanticName);
        session.Execute(new SetLayerSemanticName(part.Id, "   "));
        Assert.IsNull(part.SemanticName);
    }

    [TestMethod]
    public void PartDeleteRemovesOwnedMaskAndUndoRestoresIdentityAndPixels()
    {
        var session = EditHistoryTests.Open();
        var part = new PartLayer(new DocumentRect(0, 0, 2, 2), [10, 20, 30, 40], "Face");
        session.Execute(new AddLayer(part));
        session.Open(session.Document!);
        session.SelectLayer(part.Id);
        var mask = part.CopyMask(part.Bounds);

        session.Execute(new DeleteLayer(part.Id));
        Assert.IsFalse(session.Document!.Layers.Any(layer => layer.Id == part.Id));
        Assert.AreEqual(session.Document.Base.Id, session.SelectedLayerId);
        session.Undo();

        var restored = (PartLayer)session.Document.GetLayer(part.Id);
        Assert.AreSame(part, restored);
        CollectionAssert.AreEqual(mask, restored.CopyMask(restored.Bounds));
        Assert.AreEqual(part.Id, session.SelectedLayerId);
    }

    [TestMethod]
    public void CloneStrokeMappingKeepsAnchorAndOffsetImmutable()
    {
        var mapping = new CloneStrokeMapping(new(2.25, 3.75), new(10.5, 12.5));

        Assert.AreEqual(new DocumentPoint(-8.25, -8.75), mapping.Offset);
        Assert.AreEqual(new DocumentPoint(5.75, 6.25), mapping.SourceFor(new(14, 15)));
        Assert.AreEqual(new DocumentPoint(2.25, 3.75), mapping.SourceAnchor);
        Assert.AreEqual(new DocumentPoint(10.5, 12.5), mapping.DestinationAnchor);
    }
}
