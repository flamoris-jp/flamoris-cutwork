using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LargePolygonTests
{
    [TestMethod]
    public void ClothingSizedFenceIsExactFullResolutionBeyondOldFittingAndSurfaceLimits()
    {
        var fence=new DocumentPoint[]{new(20,20),new(1000,20),new(1000,1400),new(20,1400)};
        var result=PolygonPart.Create(fence,new(1200,1600));
        Assert.IsTrue(result.Mask.Length>262144);Assert.AreEqual((1000-20+1)*(1400-20+1),result.PixelCount);
        CollectionAssert.AreEqual(PolygonMaskRasterizer.Rasterize(fence,result.Bounds),result.Mask);
        Assert.IsTrue(result.Mask.All(x=>x==255));
    }

    [TestMethod]
    public void ConcavePolygonKeepsExactCutoutAndCancellationIsObserved()
    {
        var fence=new DocumentPoint[]{new(1,1),new(600,1),new(600,200),new(200,200),new(200,600),new(1,600)};
        var result=PolygonPart.Create(fence,new(700,700));
        int At(int x,int y)=>result.Mask[(y-result.Bounds.Y)*result.Bounds.Width+x-result.Bounds.X];
        Assert.AreEqual(255,At(100,500));Assert.AreEqual(0,At(500,500));Assert.AreEqual(255,At(200,200));
        using var c=new CancellationTokenSource();c.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>PolygonPart.Create(fence,new(700,700),c.Token));
    }

    [TestMethod]
    public void LargeBudgetRefusesUnboundedAllocationAndPixelCoordinatesOutsideDocument()
    {
        Assert.ThrowsExactly<ArgumentException>(()=>PolygonPart.Create([new(0,0),new(3000,0),new(3000,3000),new(0,3000)],new(4000,4000)));
        Assert.ThrowsExactly<ArgumentException>(()=>PolygonPart.Create([new(0,0),new(700,0),new(0,700)],new(700,700)));
        Assert.ThrowsExactly<ArgumentException>(()=>PolygonPart.Create([new(0,0),new(double.NaN,1),new(2,2)],new(4,4)));
    }
}
