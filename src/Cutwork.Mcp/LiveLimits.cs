using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Mcp;

public static class LiveLimits
{
    public const int FrameBytes = 4 * 1024 * 1024, Depth = 64, Operations = 64, Points = 4096,
        FenceVertices = 64, Dabs = 1024, SurfacePixels = 262144, FittingPixels = 65536,
        PreviewEdge = 1024, PngBytes = 1024 * 1024, PageSize = 64;
    public const double Radius = 64;
    public const long WorkUnits = 8_000_000, WorkingBytes = 64L * 1024 * 1024;
    public static long Area(DocumentRect bounds) => (long)bounds.Width * bounds.Height;
    public static void Surface(DocumentRect bounds)
    {
        if (bounds.IsEmpty || Area(bounds) > SurfacePixels) throw new LiveException("surface_limit");
    }
    public static void Point(DocumentPoint point, PixelSize size)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || point.X < 0 || point.Y < 0 || point.X >= size.Width || point.Y >= size.Height)
            throw new LiveException("coordinate_range");
    }
    public static DocumentRect Fence(IReadOnlyList<DocumentPoint> fence, PixelSize size, LiveBudget budget)
    {
        if (fence.Count is < 3 or > FenceVertices) throw new LiveException("fence_limit");
        foreach (var p in fence) Point(p, size);
        var bounds = PolygonMaskRasterizer.GetClippedBounds(fence, size);
        if (bounds.IsEmpty || Area(bounds) > FittingPixels) throw new LiveException("fitting_limit");
        budget.Add(Area(bounds) * fence.Count, Area(bounds) * 256);
        return bounds;
    }
    public static IReadOnlyList<IReadOnlyList<DocumentPoint>> Stroke(DocumentPoint[] points, double radius,
        PixelSize size, LiveBudget budget)
    {
        if (points.Length is < 1 or > Points || !double.IsFinite(radius) || radius < 0.5 || radius > Radius)
            throw new LiveException("stroke_limit");
        double distance = 0;
        for (int i = 0; i < points.Length; i++)
        {
            Point(points[i], size);
            if (i > 0) distance += Math.Sqrt(Math.Pow(points[i].X - points[i-1].X, 2) + Math.Pow(points[i].Y - points[i-1].Y, 2));
        }
        var sampler = new StrokeSampler(radius);
        if (1 + Math.Floor(distance / sampler.Spacing) > Dabs) throw new LiveException("dab_limit");
        var batches = new List<IReadOnlyList<DocumentPoint>>();
        void Add(IReadOnlyList<DocumentPoint> batch)
        {
            if (batch.Count == 0) return;
            var roi = batch.Aggregate(default(DocumentRect), (r, p) => r.Union(MaskBrushKernel.DabBounds(p, radius, size)));
            Surface(roi);
            budget.Add(Area(roi) * batch.Count, Area(roi) * 24 + batch.Count * 32L);
            batches.Add(batch);
        }
        Add(sampler.Begin(points[0]));
        foreach (var point in points.Skip(1))
        {
            Add(sampler.Add(point));
            while (sampler.HasPendingSamples) Add(sampler.TakePendingBatch());
        }
        if (batches.Sum(b => b.Count) > Dabs) throw new LiveException("dab_limit");
        return batches;
    }
}

public sealed class LiveBudget(long historyLimit = long.MaxValue)
{
    private DocumentRect dirty;
    private int maximumLayers;
    private long redrawCost;
    // Rollback and shared Undo publish the union, including the gap between distant edits.
    public void Dirty(DocumentRect region, int layerCount)
    {
        dirty = dirty.Union(region);
        maximumLayers = Math.Max(maximumLayers, layerCount);
        long cost = LiveLimits.Area(dirty) * Math.Max(1L, maximumLayers * 2L);
        Add(cost - redrawCost, 0);
        redrawCost = cost;
    }
    public long History { get; private set; }
    public void ReserveHistory(long bytes)
    {
        History = checked(History + bytes);
        if (History > historyLimit) throw new LiveException("history_limit");
    }
    public long Work { get; private set; }
    public long Bytes { get; private set; }
    public void Add(long work, long bytes)
    {
        Work = checked(Work + work); Bytes = checked(Bytes + bytes);
        if (Work > LiveLimits.WorkUnits || Bytes > LiveLimits.WorkingBytes) throw new LiveException("work_limit");
    }
}
