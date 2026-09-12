using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging;

/// <summary>
/// Deterministic outside-in fitting inside one user-authored polygon fence.
/// Boundary preparation and the progressive removal order are independent of WPF.
/// </summary>
public sealed class PolygonGuriguri : IPartFittingSession
{
    public const double KeepFactorPerStep = 0.88;
    public const int MinimumKeepPixels = 8;
    private const double BoundaryWeight = 60.0;

    private readonly byte[] _inside;
    private readonly float[] _boundary;
    private readonly double[] _best;
    private readonly bool[] _finalized;
    private readonly List<int> _removalOrder = [];
    private readonly PriorityQueue<int, PixelPriority> _frontier = new();

    private PolygonGuriguri(DocumentRect bounds, byte[] inside, float[] boundary)
    {
        if (inside.Length != checked(bounds.Width * bounds.Height) || boundary.Length != inside.Length)
            throw new ArgumentException("Prepared Guriguri buffers must match bounds.");
        if (!inside.Any(value => value != 0))
            throw new ArgumentException("Polygon fence must contain pixels.", nameof(inside));

        Bounds = bounds;
        _inside = inside.Select(value => value == 0 ? (byte)0 : (byte)255).ToArray();
        _boundary = boundary.ToArray();
        PolygonPixelCount = _inside.Count(value => value != 0);
        _best = Enumerable.Repeat(double.PositiveInfinity, _inside.Length).ToArray();
        _finalized = new bool[_inside.Length];

        for (var index = 0; index < _inside.Length; index++)
        {
            if (_inside[index] == 0 || !IsInsideEdge(index)) continue;
            var initial = CrossingCost(_boundary[index], _boundary[index]);
            _best[index] = initial;
            _frontier.Enqueue(index, new(initial, index));
        }
    }

    public DocumentRect Bounds { get; }
    public int PolygonPixelCount { get; }
    public int Step { get; private set; }
    public int CurrentKeepPixels => TargetKeepPixelsForStep(Step, PolygonPixelCount);

    public static PolygonGuriguri Create(OriginalAsset original, IReadOnlyList<DocumentPoint> fence)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(fence);
        if (fence.Count < 3) throw new ArgumentException("A polygon needs at least three points.", nameof(fence));

        var bounds = PolygonMaskRasterizer.GetClippedBounds(fence, original.Dimensions);
        if (bounds.IsEmpty) throw new ArgumentException("Polygon does not cover the document.", nameof(fence));
        var mask = PolygonMaskRasterizer.Rasterize(fence, bounds);
        var boundary = GuriguriBoundaryMap.Build(original, bounds);
        return new(bounds, mask, boundary);
    }

    public static PolygonGuriguri FromPrepared(
        DocumentRect bounds,
        ReadOnlySpan<byte> polygonMask,
        ReadOnlySpan<float> boundaryMap) =>
        new(bounds, polygonMask.ToArray(), boundaryMap.ToArray());

    public byte[] Adjust(int wheelSteps)
    {
        Step = Math.Max(0, Step + wheelSteps);
        return CopyCurrentMask();
    }

    public byte[] CopyCurrentMask() => MaskForKeepCount(CurrentKeepPixels);

    public byte[] MaskForKeepCount(int keepCount)
    {
        keepCount = Math.Clamp(keepCount, 1, PolygonPixelCount);
        var removeCount = PolygonPixelCount - keepCount;
        EnsureRemovedCount(removeCount);
        var mask = _inside.ToArray();
        for (var i = 0; i < Math.Min(removeCount, _removalOrder.Count); i++) mask[_removalOrder[i]] = 0;
        return mask;
    }

    public static int TargetKeepPixelsForStep(int step, int polygonPixels)
    {
        polygonPixels = Math.Max(1, polygonPixels);
        step = Math.Max(0, step);
        var minimum = Math.Min(polygonPixels, MinimumKeepPixels);
        var target = (int)Math.Round(polygonPixels * Math.Pow(KeepFactorPerStep, step));
        return Math.Clamp(target, minimum, polygonPixels);
    }

    private void EnsureRemovedCount(int target)
    {
        target = Math.Clamp(target, 0, PolygonPixelCount);
        while (_frontier.Count > 0 && _removalOrder.Count < target)
        {
            _frontier.TryDequeue(out var index, out var priority);
            if (_finalized[index] || priority.Cost != _best[index]) continue;

            _finalized[index] = true;
            _removalOrder.Add(index);
            foreach (var neighbor in Neighbors(index))
            {
                if (_inside[neighbor] == 0 || _finalized[neighbor]) continue;
                var candidate = priority.Cost + CrossingCost(_boundary[index], _boundary[neighbor]);
                if (candidate >= _best[neighbor]) continue;
                _best[neighbor] = candidate;
                _frontier.Enqueue(neighbor, new(candidate, neighbor));
            }
        }
    }

    private bool IsInsideEdge(int index)
    {
        var x = index % Bounds.Width;
        var y = index / Bounds.Width;
        return x == 0 || x + 1 == Bounds.Width || y == 0 || y + 1 == Bounds.Height
            || _inside[index - 1] == 0 || _inside[index + 1] == 0
            || _inside[index - Bounds.Width] == 0 || _inside[index + Bounds.Width] == 0;
    }

    private IEnumerable<int> Neighbors(int index)
    {
        var x = index % Bounds.Width;
        var y = index / Bounds.Width;
        if (x > 0) yield return index - 1;
        if (x + 1 < Bounds.Width) yield return index + 1;
        if (y > 0) yield return index - Bounds.Width;
        if (y + 1 < Bounds.Height) yield return index + Bounds.Width;
    }

    private static double CrossingCost(float first, float second)
    {
        var strength = Math.Max(first, second) / 100.0;
        return 1.0 + BoundaryWeight * Math.Pow(strength, 4);
    }

    private readonly record struct PixelPriority(double Cost, int Index) : IComparable<PixelPriority>
    {
        public int CompareTo(PixelPriority other)
        {
            var cost = Cost.CompareTo(other.Cost);
            return cost != 0 ? cost : Index.CompareTo(other.Index);
        }
    }
}

public sealed class GuriguriPartFitter : IPartBoundaryFitter
{
    public IPartFittingSession Create(OriginalAsset original, IReadOnlyList<DocumentPoint> fence) =>
        PolygonGuriguri.Create(original, fence);
}

public static class PolygonMaskRasterizer
{
    public static DocumentRect GetClippedBounds(IReadOnlyList<DocumentPoint> points, PixelSize document)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3) return default;
        var left = Math.Clamp((int)Math.Floor(points.Min(point => point.X)), 0, document.Width);
        var top = Math.Clamp((int)Math.Floor(points.Min(point => point.Y)), 0, document.Height);
        var right = Math.Clamp((int)Math.Ceiling(points.Max(point => point.X)) + 1, 0, document.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(points.Max(point => point.Y)) + 1, 0, document.Height);
        return right <= left || bottom <= top ? default : new(left, top, right - left, bottom - top);
    }

    public static byte[] Rasterize(IReadOnlyList<DocumentPoint> points, DocumentRect bounds)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3 || bounds.IsEmpty) return [];
        var mask = new byte[checked(bounds.Width * bounds.Height)];
        for (var localY = 0; localY < bounds.Height; localY++)
        {
            for (var localX = 0; localX < bounds.Width; localX++)
            {
                var point = new DocumentPoint(bounds.X + localX, bounds.Y + localY);
                if (Contains(points, point)) mask[localY * bounds.Width + localX] = 255;
            }
        }
        return mask;
    }

    private static bool Contains(IReadOnlyList<DocumentPoint> polygon, DocumentPoint point)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var first = polygon[i];
            var second = polygon[(i + 1) % polygon.Count];
            if (OnSegment(first, second, point)) return true;
            if ((first.Y > point.Y) == (second.Y > point.Y)) continue;
            var crossingX = first.X + (point.Y - first.Y) * (second.X - first.X) / (second.Y - first.Y);
            if (crossingX > point.X) inside = !inside;
        }
        return inside;
    }

    private static bool OnSegment(DocumentPoint first, DocumentPoint second, DocumentPoint point)
    {
        const double tolerance = 1e-9;
        var cross = (point.X - first.X) * (second.Y - first.Y)
            - (point.Y - first.Y) * (second.X - first.X);
        if (Math.Abs(cross) > tolerance) return false;
        return point.X >= Math.Min(first.X, second.X) - tolerance
            && point.X <= Math.Max(first.X, second.X) + tolerance
            && point.Y >= Math.Min(first.Y, second.Y) - tolerance
            && point.Y <= Math.Max(first.Y, second.Y) + tolerance;
    }
}

public static class GuriguriBoundaryMap
{
    private static readonly double[] GaussianKernel = CreateGaussianKernel(0.8, 2);

    public static float[] Build(OriginalAsset original, DocumentRect bounds)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (!DocumentRect.FromSize(original.Dimensions).Contains(bounds))
            throw new ArgumentOutOfRangeException(nameof(bounds));

        var count = checked(bounds.Width * bounds.Height);
        var l = new double[count];
        var a = new double[count];
        var b = new double[count];
        for (var y = 0; y < bounds.Height; y++)
        {
            for (var x = 0; x < bounds.Width; x++)
            {
                var pixel = original.PixelAt(bounds.X + x, bounds.Y + y);
                RgbToLab(pixel[2], pixel[1], pixel[0], out var lv, out var av, out var bv);
                var index = y * bounds.Width + x;
                l[index] = lv * 2.55;
                a[index] = av + 128.0;
                b[index] = bv + 128.0;
            }
        }

        var blurredL = Blur(l, bounds.Width, bounds.Height);
        var blurredA = Blur(a, bounds.Width, bounds.Height);
        var blurredB = Blur(b, bounds.Width, bounds.Height);
        var gradient = new double[count];
        AddScharrMagnitudeSquared(blurredL, gradient, bounds.Width, bounds.Height, 1.0);
        AddScharrMagnitudeSquared(blurredA, gradient, bounds.Width, bounds.Height, 0.65);
        AddScharrMagnitudeSquared(blurredB, gradient, bounds.Width, bounds.Height, 0.65);
        for (var i = 0; i < gradient.Length; i++) gradient[i] = Math.Sqrt(gradient[i]);

        var scale = Percentile(gradient, 0.975);
        if (scale <= 1e-6) return new float[count];
        var result = new float[count];
        for (var i = 0; i < result.Length; i++)
            result[i] = (float)(Math.Sqrt(Math.Clamp(gradient[i] / scale, 0.0, 1.0)) * 100.0);
        return result;
    }

    private static void RgbToLab(byte red, byte green, byte blue, out double l, out double a, out double b)
    {
        static double Linearize(byte value)
        {
            var v = value / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        static double F(double value) => value > 0.008856
            ? Math.Cbrt(value)
            : 7.787 * value + 16.0 / 116.0;

        var r = Linearize(red);
        var g = Linearize(green);
        var bl = Linearize(blue);
        var x = (r * 0.4124564 + g * 0.3575761 + bl * 0.1804375) / 0.95047;
        var y = r * 0.2126729 + g * 0.7151522 + bl * 0.0721750;
        var z = (r * 0.0193339 + g * 0.1191920 + bl * 0.9503041) / 1.08883;
        var fx = F(x);
        var fy = F(y);
        var fz = F(z);
        l = 116.0 * fy - 16.0;
        a = 500.0 * (fx - fy);
        b = 200.0 * (fy - fz);
    }

    private static double[] Blur(double[] source, int width, int height)
    {
        var horizontal = new double[source.Length];
        var result = new double[source.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var k = -2; k <= 2; k++)
                    horizontal[y * width + x] += source[y * width + Reflect101(x + k, width)] * GaussianKernel[k + 2];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var k = -2; k <= 2; k++)
                    result[y * width + x] += horizontal[Reflect101(y + k, height) * width + x] * GaussianKernel[k + 2];
        return result;
    }

    private static void AddScharrMagnitudeSquared(
        double[] source, double[] destination, int width, int height, double weight)
    {
        for (var y = 0; y < height; y++)
        {
            var top = Reflect101(y - 1, height);
            var bottom = Reflect101(y + 1, height);
            for (var x = 0; x < width; x++)
            {
                var left = Reflect101(x - 1, width);
                var right = Reflect101(x + 1, width);
                var gx = -3 * source[top * width + left] + 3 * source[top * width + right]
                    - 10 * source[y * width + left] + 10 * source[y * width + right]
                    - 3 * source[bottom * width + left] + 3 * source[bottom * width + right];
                var gy = -3 * source[top * width + left] - 10 * source[top * width + x]
                    - 3 * source[top * width + right] + 3 * source[bottom * width + left]
                    + 10 * source[bottom * width + x] + 3 * source[bottom * width + right];
                destination[y * width + x] += weight * (gx * gx + gy * gy);
            }
        }
    }

    private static int Reflect101(int value, int length)
    {
        if (length <= 1) return 0;
        while (value < 0 || value >= length)
            value = value < 0 ? -value : 2 * length - value - 2;
        return value;
    }

    private static double Percentile(double[] values, double fraction)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var position = (sorted.Length - 1) * fraction;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double[] CreateGaussianKernel(double sigma, int radius)
    {
        var kernel = new double[radius * 2 + 1];
        var sum = 0.0;
        for (var i = -radius; i <= radius; i++)
        {
            var value = Math.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = value;
            sum += value;
        }
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= sum;
        return kernel;
    }
}
