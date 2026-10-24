using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging;

public sealed record PolygonPart(DocumentRect Bounds,byte[] Mask,int PixelCount)
{
    public const int MaximumPixels=4*1024*1024, MaximumVertices=64;
    public const long MaximumWork=256_000_000, MaximumWorkingBytes=128L*1024*1024;

    public static DocumentRect Validate(IReadOnlyList<DocumentPoint> fence,PixelSize size)
    {
        if(fence.Count<3 || fence.Count>MaximumVertices)throw new ArgumentException("polygon_vertex_limit");
        foreach(var p in fence)
            if(!double.IsFinite(p.X)||!double.IsFinite(p.Y)||p.X<0||p.Y<0||p.X>=size.Width||p.Y>=size.Height)
                throw new ArgumentException("coordinate_range");
        var bounds=PolygonMaskRasterizer.GetClippedBounds(fence,size);
        long area=(long)bounds.Width*bounds.Height;
        if(bounds.IsEmpty || area>MaximumPixels || area*fence.Count>MaximumWork)
            throw new ArgumentException("polygon_work_limit");
        return bounds;
    }
    public static PolygonPart Create(IReadOnlyList<DocumentPoint> fence,PixelSize size,CancellationToken ct=default)
    {
        var bounds=Validate(fence,size);ct.ThrowIfCancellationRequested();
        var mask=PolygonMaskRasterizer.Rasterize(fence,bounds,ct);
        int count=mask.Count(x=>x!=0);
        if(count<PartToolController.MinimumFencePixels)throw new ArgumentException("fence_too_small");
        return new(bounds,mask,count);
    }
}
