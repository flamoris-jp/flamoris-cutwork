using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging;

/// <summary>One stroke: frozen source and maximum per-pixel coverage, shared by UI and MCP.</summary>
public sealed class TransformedCloneStroke : ICloneStroke
{
    private readonly CloneStrokeMapping mapping;
    private readonly double radius;
    private readonly PixelSize size;
    private readonly OriginalAsset original;
    private readonly DocumentRect sourceBounds;
    private readonly byte[]? frozen;
    private readonly Dictionary<long, Coverage> coverage = [];
    private sealed record Coverage(byte[] Before, double Weight);
    public const int MaximumSourcePixels = 262144;

    public TransformedCloneStroke(CutworkDocument document, CloneStrokeMapping mapping,
        double radius, DocumentRect sourceBounds)
    {
        mapping.Validate();
        if (!double.IsFinite(radius) || radius < .5 || radius > 512) throw new ArgumentOutOfRangeException(nameof(radius));
        this.mapping = mapping; this.radius = radius; size = document.Dimensions;
        original = document.Original; this.sourceBounds = sourceBounds;
        if (mapping.SourceKind != CloneSourceKind.CompositeAtStrokeStart || mapping.Opacity == 0) return;
        long area = (long)sourceBounds.Width * sourceBounds.Height;
        if (area > MaximumSourcePixels || area * Math.Max(1L, document.Layers.Count * 2L) > 8_000_000)
            throw new CloneBudgetException();
        frozen = new byte[checked((int)area * 4)];
        for (int y = sourceBounds.Y; y < sourceBounds.Bottom; y++)
        for (int x = sourceBounds.X; x < sourceBounds.Right; x++)
        {
            var p = frozen.AsSpan(((y-sourceBounds.Y)*sourceBounds.Width+x-sourceBounds.X)*4,4);
            CompositeCache.CompositePixel(document,x,y,CompositeCache.HoleAt(document,x,y),p);
            if (p[3] != 0) for (int c=0;c<3;c++) p[c]=(byte)Math.Min(255,(p[c]*255+p[3]/2)/p[3]);
        }
    }

    public static DocumentRect SourceBounds(IReadOnlyList<DocumentPoint> samples,
        CloneStrokeMapping mapping, double radius, PixelSize size)
    {
        double left=double.PositiveInfinity, top=left, right=double.NegativeInfinity, bottom=right;
        foreach (var d in samples)
        foreach (var dx in new[]{-radius,radius})
        foreach (var dy in new[]{-radius,radius})
        {
            var p=mapping.SourceFor(new(d.X+dx,d.Y+dy),d);
            left=Math.Min(left,p.X); top=Math.Min(top,p.Y); right=Math.Max(right,p.X); bottom=Math.Max(bottom,p.Y);
        }
        int x=(int)Math.Clamp(Math.Floor(left-.5),0,size.Width), y=(int)Math.Clamp(Math.Floor(top-.5),0,size.Height);
        int r=(int)Math.Clamp(Math.Ceiling(right+.5)+1,0,size.Width), b=(int)Math.Clamp(Math.Ceiling(bottom+.5)+1,0,size.Height);
        return r<=x || b<=y ? default : new(x,y,r-x,b-y);
    }

    public CloneRepairPatch Apply(RepairLayer target, IReadOnlyList<DocumentPoint> samples)
    {
        if (samples.Count > StrokeSampler.MaximumBatchSamples) throw new ArgumentOutOfRangeException(nameof(samples));
        var roi=samples.Aggregate(default(DocumentRect),(r,p)=>r.Union(MaskBrushKernel.DabBounds(p,radius,size)));
        if (roi.IsEmpty || mapping.Opacity==0) return default;
        var pixels=target.CopyPixelsWithTransparentOutside(roi);
        bool changed=false;
        Span<double> sample=stackalloc double[4];
        foreach (var d in samples)
        {
            var bounds=MaskBrushKernel.DabBounds(d,radius,size);
            for(int y=bounds.Y;y<bounds.Bottom;y++)
            for(int x=bounds.X;x<bounds.Right;x++)
            {
                double distance=Math.Sqrt(Math.Pow(x+.5-d.X,2)+Math.Pow(y+.5-d.Y,2))/radius;
                if(distance>1) continue;
                double t=mapping.Hardness>=1 ? 0 : Math.Clamp((distance-mapping.Hardness)/(1-mapping.Hardness),0,1);
                double brush=(1-t*t*(3-2*t))*mapping.Opacity;
                var point=mapping.SourceFor(new(x+.5,y+.5),d);
                Sample(point,sample);
                double weight=sample[3]/255*brush;
                if(weight<=0) continue;
                long key=(long)y*size.Width+x;
                var dst=pixels.AsSpan(((y-roi.Y)*roi.Width+x-roi.X)*4,4);
                if(!coverage.TryGetValue(key,out var previous))
                {
                    if(coverage.Count>=MaximumSourcePixels) throw new CloneBudgetException();
                    previous=new(dst.ToArray(),0);
                }
                if(weight<previous.Weight) continue;
                coverage[key]=previous with {Weight=weight};
                double a=weight+previous.Before[3]/255.0*(1-weight);
                for(int c=0;c<3;c++)
                {
                    byte value=a<=0 ? (byte)0 : (byte)Math.Clamp(Math.Round((sample[c]*weight+previous.Before[c]*previous.Before[3]/255.0*(1-weight))/a),0,255);
                    if(dst[c]!=value) changed=true; dst[c]=value;
                }
                byte alpha=(byte)Math.Clamp(Math.Round(a*255),0,255);
                if(dst[3]!=alpha) changed=true; dst[3]=alpha;
            }
        }
        return new(roi,pixels,changed);
    }

    private void Sample(DocumentPoint p, Span<double> result)
    {
        // A pixel's center is x+.5. Interpolate premultiplied samples before unpremultiplying.
        double sx=p.X-.5, sy=p.Y-.5;
        int x=(int)Math.Floor(sx),y=(int)Math.Floor(sy);
        double fx=sx-x,fy=sy-y;
        result.Clear();
        for(int j=0;j<2;j++) for(int i=0;i<2;i++)
        {
            int px=x+i,py=y+j;
            if((uint)px>=(uint)size.Width || (uint)py>=(uint)size.Height) continue;
            if(frozen is not null && !(px>=sourceBounds.X && px<sourceBounds.Right && py>=sourceBounds.Y && py<sourceBounds.Bottom)) continue;
            ReadOnlySpan<byte> pixel=frozen is null ? original.PixelAt(px,py)
                : frozen.AsSpan(((py-sourceBounds.Y)*sourceBounds.Width+px-sourceBounds.X)*4,4);
            double w=(i==0?1-fx:fx)*(j==0?1-fy:fy),a=pixel[3]/255.0;
            result[3]+=pixel[3]*w;
            for(int c=0;c<3;c++) result[c]+=pixel[c]*a*w;
        }
        if(result[3]>0) for(int c=0;c<3;c++) result[c]*=255/result[3];
    }
}
