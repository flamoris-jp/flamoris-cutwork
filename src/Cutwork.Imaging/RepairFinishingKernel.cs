using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging;

/// <summary>Small deterministic Blur and directional Smudge kernels for compact Repair pixels.</summary>
public sealed class RepairFinishingKernel : IRepairFinishingKernel
{
    public RepairFinishingPatch Blur(RepairLayer target, DocumentPoint center,
        double radius, double strength, PixelSize documentSize)
    {
        Validate(target, radius, strength);
        var destination = DabBounds(center, radius, documentSize).Intersect(target.Bounds);
        if (destination.IsEmpty) return default;
        var read = Expand(destination, 1, documentSize);
        var source = target.CopyPixelsWithTransparentOutside(read);
        var after = target.CopyPixels(destination);
        var radiusSquared = radius * radius;
        var changed = false;

        for (var y = destination.Y; y < destination.Bottom; y++)
        for (var x = destination.X; x < destination.Right; x++)
        {
            var dx = x + 0.5 - center.X;
            var dy = y + 0.5 - center.Y;
            if (dx * dx + dy * dy > radiusSquared) continue;
            var destinationOffset = checked(((y - destination.Y) * destination.Width
                + x - destination.X) * 4);
            for (var channel = 0; channel < 4; channel++)
            {
                var sum = 0;
                for (var sampleY = y - 1; sampleY <= y + 1; sampleY++)
                for (var sampleX = x - 1; sampleX <= x + 1; sampleX++)
                    sum += Pixel(source, read, sampleX, sampleY, channel);
                var blurred = (byte)((sum + 4) / 9);
                var value = Blend(after[destinationOffset + channel], blurred, strength);
                if (value == after[destinationOffset + channel]) continue;
                after[destinationOffset + channel] = value;
                changed = true;
            }
        }
        return new(destination, after, changed);
    }

    public RepairFinishingPatch Smudge(RepairLayer target, DocumentPoint from, DocumentPoint to,
        double radius, double strength, PixelSize documentSize)
    {
        Validate(target, radius, strength);
        var movementX = to.X - from.X;
        var movementY = to.Y - from.Y;
        if (!double.IsFinite(movementX) || !double.IsFinite(movementY)
            || movementX * movementX + movementY * movementY <= 1e-12) return default;

        var destination = DabBounds(to, radius, documentSize).Intersect(target.Bounds);
        if (destination.IsEmpty) return default;
        var sourceBounds = ShiftedBounds(destination, -movementX, -movementY, documentSize);
        // Bilinear source sampling can touch the next pixel on each axis.
        var read = Expand(destination.Union(sourceBounds), 1, documentSize);
        var source = target.CopyPixelsWithTransparentOutside(read);
        var after = target.CopyPixels(destination);
        var radiusSquared = radius * radius;
        var changed = false;

        for (var y = destination.Y; y < destination.Bottom; y++)
        for (var x = destination.X; x < destination.Right; x++)
        {
            var brushX = x + 0.5 - to.X;
            var brushY = y + 0.5 - to.Y;
            if (brushX * brushX + brushY * brushY > radiusSquared) continue;
            // Convert the shifted source pixel-center position to pixel-grid coordinates.
            var sourceX = x - movementX;
            var sourceY = y - movementY;
            var destinationOffset = checked(((y - destination.Y) * destination.Width
                + x - destination.X) * 4);
            for (var channel = 0; channel < 4; channel++)
            {
                var sampled = BilinearPixel(source, read, sourceX, sourceY, channel);
                var value = Blend(after[destinationOffset + channel], sampled, strength);
                if (value == after[destinationOffset + channel]) continue;
                after[destinationOffset + channel] = value;
                changed = true;
            }
        }
        return new(destination, after, changed);
    }

    private static byte Blend(byte current, double target, double strength) =>
        (byte)Math.Clamp((int)Math.Round(current + (target - current) * strength,
            MidpointRounding.AwayFromZero), 0, 255);

    private static double BilinearPixel(byte[] pixels, DocumentRect bounds,
        double x, double y, int channel)
    {
        var left = (int)Math.Floor(x);
        var top = (int)Math.Floor(y);
        var fractionX = x - left;
        var fractionY = y - top;
        var topValue = Pixel(pixels, bounds, left, top, channel) * (1 - fractionX)
            + Pixel(pixels, bounds, left + 1, top, channel) * fractionX;
        var bottomValue = Pixel(pixels, bounds, left, top + 1, channel) * (1 - fractionX)
            + Pixel(pixels, bounds, left + 1, top + 1, channel) * fractionX;
        return topValue * (1 - fractionY) + bottomValue * fractionY;
    }

    private static byte Pixel(byte[] pixels, DocumentRect bounds, int x, int y, int channel)
    {
        if (x < bounds.X || y < bounds.Y || x >= bounds.Right || y >= bounds.Bottom) return 0;
        return pixels[checked(((y - bounds.Y) * bounds.Width + x - bounds.X) * 4 + channel)];
    }

    private static DocumentRect DabBounds(DocumentPoint center, double radius, PixelSize dimensions)
    {
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y))
            throw new ArgumentOutOfRangeException(nameof(center));
        var left = Math.Clamp((int)Math.Floor(center.X - radius), 0, dimensions.Width);
        var top = Math.Clamp((int)Math.Floor(center.Y - radius), 0, dimensions.Height);
        var right = Math.Clamp((int)Math.Ceiling(center.X + radius), 0, dimensions.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(center.Y + radius), 0, dimensions.Height);
        return right <= left || bottom <= top ? default : new(left, top, right - left, bottom - top);
    }

    private static DocumentRect Expand(DocumentRect bounds, int halo, PixelSize dimensions)
    {
        var left = Math.Max(0, bounds.X - halo);
        var top = Math.Max(0, bounds.Y - halo);
        var right = Math.Min(dimensions.Width, bounds.Right + halo);
        var bottom = Math.Min(dimensions.Height, bounds.Bottom + halo);
        return new(left, top, right - left, bottom - top);
    }

    private static DocumentRect ShiftedBounds(DocumentRect bounds, double dx, double dy,
        PixelSize dimensions)
    {
        var left = Math.Clamp((int)Math.Floor(bounds.X + dx), 0, dimensions.Width);
        var top = Math.Clamp((int)Math.Floor(bounds.Y + dy), 0, dimensions.Height);
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right + dx), 0, dimensions.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom + dy), 0, dimensions.Height);
        return right <= left || bottom <= top ? default : new(left, top, right - left, bottom - top);
    }

    private static void Validate(RepairLayer target, double radius, double strength)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!double.IsFinite(radius) || radius < RepairFinishingController.MinimumRadius
            || radius > RepairFinishingController.MaximumRadius)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (!double.IsFinite(strength) || strength < RepairFinishingController.MinimumStrength
            || strength > RepairFinishingController.MaximumStrength)
            throw new ArgumentOutOfRangeException(nameof(strength));
    }
}
