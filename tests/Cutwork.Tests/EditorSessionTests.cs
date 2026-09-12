using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class EditorSessionTests
{
    [TestMethod]
    public void PreviewSourceSwitchDoesNotReplaceTheOpenDocument()
    {
        var document = new CutworkDocument(
            new OriginalAsset("original.png", new PixelSize(1, 1), 4, new byte[4]));
        var session = new EditorSession();
        session.Open(document);

        Assert.AreEqual(PreviewSource.Composite, session.PreviewSource);

        session.SetPreviewSource(PreviewSource.Original);

        Assert.AreEqual(PreviewSource.Original, session.PreviewSource);
        Assert.AreSame(document, session.Document);
    }
}
