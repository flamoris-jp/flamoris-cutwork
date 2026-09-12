using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class ViewportTransformTests
{
    [TestMethod]
    public void DocumentViewportRoundTripIsStableAtScaledDpi()
    {
        var transform = CreateTransformedViewport();
        var original = new DocumentPoint(741.25, 392.75);

        var roundTrip = transform.ViewportToDocument(transform.DocumentToViewport(original));

        Assert.AreEqual(original.X, roundTrip.X, 1e-9);
        Assert.AreEqual(original.Y, roundTrip.Y, 1e-9);
    }

    [TestMethod]
    public void ViewportDocumentRoundTripIsStableAtScaledDpi()
    {
        var transform = CreateTransformedViewport();
        var original = new ViewportPoint(612.5, 331.75);

        var roundTrip = transform.DocumentToViewport(transform.ViewportToDocument(original));

        Assert.AreEqual(original.X, roundTrip.X, 1e-9);
        Assert.AreEqual(original.Y, roundTrip.Y, 1e-9);
    }

    [TestMethod]
    public void ZoomAndPanDoNotMutateDocumentGeometryOrOriginalPixels()
    {
        var original = new OriginalAsset("original.png", new PixelSize(2, 1), 8, new byte[8]);
        var document = new CutworkDocument(original);
        var initialPixels = document.Original.CopyPixelBytes();
        var transform = new ViewportTransform();

        transform.Fit(document.Dimensions, new ViewportSize(800, 600));
        transform.ZoomAt(new ViewportPoint(300, 200), 1.5);
        transform.PanBy(42.0, -17.0);

        Assert.AreEqual(new PixelSize(2, 1), document.Dimensions);
        CollectionAssert.AreEqual(initialPixels, document.Original.CopyPixelBytes());
    }

    [TestMethod]
    public void ActualSizeMapsOneDocumentPixelToOneDevicePixel()
    {
        var transform = new ViewportTransform();
        transform.SetDpiScale(1.5, 1.5, new ViewportPoint(0, 0));
        transform.ActualSize(new PixelSize(100, 100), new ViewportSize(500, 500));

        var first = transform.DocumentToViewport(new DocumentPoint(0, 0));
        var second = transform.DocumentToViewport(new DocumentPoint(1, 0));

        Assert.AreEqual(1.0, (second.X - first.X) * transform.DpiScaleX, 1e-9);
    }

    [TestMethod]
    public void RepeatedZoomPanAndDpiChangesKeepAnchorStable()
    {
        var transform = new ViewportTransform();
        var anchor = new ViewportPoint(512.25, 304.75);
        transform.Fit(new PixelSize(1920, 1080), new ViewportSize(1024, 640));
        var expected = transform.ViewportToDocument(anchor);

        for (var index = 0; index < 20; index++)
        {
            transform.ZoomAt(anchor, 1.08);
            transform.PanBy(3.25, -1.75);
            transform.PanBy(-3.25, 1.75);
            transform.SetDpiScale(index % 2 == 0 ? 1.5 : 1.0, index % 2 == 0 ? 1.5 : 1.0, anchor);
        }

        var actual = transform.ViewportToDocument(anchor);
        Assert.AreEqual(expected.X, actual.X, 1e-8);
        Assert.AreEqual(expected.Y, actual.Y, 1e-8);
    }

    private static ViewportTransform CreateTransformedViewport()
    {
        var transform = new ViewportTransform();
        transform.SetDpiScale(1.5, 1.25, new ViewportPoint(0, 0));
        transform.Fit(new PixelSize(1920, 1080), new ViewportSize(1280, 720));
        transform.ZoomAt(new ViewportPoint(640, 360), 2.3);
        transform.PanBy(37.5, -18.25);
        return transform;
    }
}
