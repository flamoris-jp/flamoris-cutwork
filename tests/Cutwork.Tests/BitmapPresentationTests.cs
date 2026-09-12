using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Flamoris.Cutwork.App.Controls;
using Flamoris.Cutwork.App.Rendering;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class BitmapPresentationTests
{
    [TestMethod]
    public void LocalEditUpdatesExistingBitmapAtDocumentOffset()
    {
        // Exercise the actual adapter without opening a window or taking screenshots.
        RunSta(() =>
        {
            var s = EditHistoryTests.Open(); var repair = EditHistoryTests.Repair();
            s.Execute(new AddLayer(repair));
            using var cache = new CompositeCache(s.Document!);
            var surface = new WriteableBitmapSurface(); surface.Initialize(s.Document!.Dimensions);
            surface.Apply(cache.RenderPending()!);
            var bitmap = surface.Bitmap; var generation = surface.Generation; var pixels = surface.TransferredPixelCount;
            var roi = new DocumentRect(2, 1, 1, 1);
            s.Execute(new RasterPatch(repair.Id, roi, new byte[] { 80, 40, 20, 128 }));
            surface.Apply(cache.RenderPending()!);
            Assert.AreSame(bitmap, surface.Bitmap); Assert.AreEqual(generation, surface.Generation);
            Assert.AreEqual(roi, surface.LastUpdatedRegion);
            Assert.AreEqual(pixels + 1, surface.TransferredPixelCount);
            var actual = new byte[48]; surface.Bitmap!.CopyPixels(actual, 16, 0);
            CollectionAssert.AreEqual(cache.CopyPixels(), actual);
            CollectionAssert.AreEqual(new byte[] { 40, 20, 10, 128 }, actual.Skip(24).Take(4).ToArray());
        });
    }

    [TestMethod]
    public void CanvasPreviewSwitchKeepsEditsPendingAndUndoRefreshesSameSurface()
    {
        RunSta(() =>
        {
            var s = EditHistoryTests.Open(); var canvas = new DocumentCanvas();
            canvas.AttachSession(s); canvas.Present(s.Document!);
            canvas.Measure(new Size(800, 600)); canvas.Arrange(new Rect(0, 0, 800, 600));
            var image = (Image)canvas.FindName("ImageSurface");
            var composite = (BitmapSource)image.Source;
            var generation = canvas.BitmapGeneration;
            s.SetPreviewSource(PreviewSource.Original); canvas.RefreshPreview();
            var originalPreview = image.Source;
            var repair = EditHistoryTests.Repair();
            s.Execute(new AddLayer(repair),
                new RasterPatch(repair.Id, new DocumentRect(2, 1, 1, 1), new byte[] { 20, 40, 60, 255 }));
            canvas.RefreshPreview(); Assert.AreSame(originalPreview, image.Source);
            s.SetPreviewSource(PreviewSource.Composite); canvas.RefreshPreview();
            Assert.AreSame(composite, image.Source); Assert.AreEqual(generation, canvas.BitmapGeneration);
            var pixels = new byte[48]; ((BitmapSource)image.Source).CopyPixels(pixels, 16, 0);
            CollectionAssert.AreEqual(new byte[] { 20, 40, 60, 255 }, pixels.Skip(24).Take(4).ToArray());
            s.Undo(); canvas.RefreshPreview(); ((BitmapSource)image.Source).CopyPixels(pixels, 16, 0);
            CollectionAssert.AreEqual(new byte[48], pixels);
            s.Redo(); canvas.RefreshPreview();
            Assert.AreEqual(generation, canvas.BitmapGeneration);
            canvas.ActualSize(); Assert.AreEqual(1.0, s.Viewport.Zoom);
            canvas.Fit(); Assert.IsTrue(s.Viewport.Zoom > 1.0);
            canvas.RefreshPreview();
            Assert.AreEqual(generation, canvas.BitmapGeneration);
        });
    }

    [TestMethod]
    public void PartPreviewUsesOverlayWithoutRebuildingAuthoredBitmap()
    {
        RunSta(() =>
        {
            var pixels = Enumerable.Repeat(new byte[] { 50, 80, 110, 255 }, 20 * 20)
                .SelectMany(pixel => pixel).ToArray();
            var session = new EditorSession();
            session.Open(new CutworkDocument(new OriginalAsset("fixture.png", new(20, 20), 80, pixels)));
            var tool = new PartToolController(session, new GuriguriPartFitter());
            var router = new CanvasInputRouter(session);
            var canvas = new DocumentCanvas();
            canvas.AttachSession(session);
            canvas.AttachInputRouter(router, tool);
            canvas.Present(session.Document!);
            canvas.Measure(new Size(800, 600));
            canvas.Arrange(new Rect(0, 0, 800, 600));
            var generation = canvas.BitmapGeneration;
            router.SetActiveTool(tool);

            tool.PointerDown(new(2, 2), 1, CanvasModifiers.None);
            tool.PointerDown(new(12, 2), 1, CanvasModifiers.None);
            tool.PointerDown(new(12, 12), 1, CanvasModifiers.None);
            tool.PointerDown(new(2, 12), 1, CanvasModifiers.None);
            tool.FinalizeFence();
            tool.Wheel(1, CanvasModifiers.None);

            var overlay = (Image)canvas.FindName("PartMaskOverlay");
            Assert.AreEqual(Visibility.Visible, overlay.Visibility);
            Assert.IsInstanceOfType(overlay.Source, typeof(WriteableBitmap));
            Assert.AreEqual(generation, canvas.BitmapGeneration);
            Assert.AreEqual(0, session.UndoCount);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
