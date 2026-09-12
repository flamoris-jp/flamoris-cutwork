using System.Runtime.ExceptionServices;
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
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
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
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
