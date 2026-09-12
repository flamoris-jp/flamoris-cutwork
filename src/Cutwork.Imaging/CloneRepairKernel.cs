using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging;

/// <summary>Deterministic hard-circle Clone kernel over a conservative destination ROI.</summary>
public sealed class CloneRepairKernel : ICloneRepairKernel
{
    public CloneRepairPatch CreatePatch(OriginalAsset original, RepairLayer target,
        IReadOnlyList<DocumentPoint> destinationSamples, DocumentPoint offset, double radius)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(destinationSamples);
        if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y))
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        if (destinationSamples.Count == 0) return default;

        var region = DestinationBounds(destinationSamples, radius, original.Dimensions);
        if (region.IsEmpty) return default;
        var pixels = target.CopyPixelsWithTransparentOutside(region);
        var radiusSquared = radius * radius;
        var changed = false;

        for (var y = region.Y; y < region.Bottom; y++)
        for (var x = region.X; x < region.Right; x++)
        {
            var centerX = x + 0.5;
            var centerY = y + 0.5;
            var covered = false;
            for (var sampleIndex = 0; sampleIndex < destinationSamples.Count; sampleIndex++)
            {
                var sample = destinationSamples[sampleIndex];
                var dx = centerX - sample.X;
                var dy = centerY - sample.Y;
                if (dx * dx + dy * dy <= radiusSquared)
                {
                    covered = true;
                    break;
                }
            }
            if (!covered) continue;

            var sourceX = (int)Math.Floor(centerX + offset.X);
            var sourceY = (int)Math.Floor(centerY + offset.Y);
            if ((uint)sourceX >= (uint)original.Dimensions.Width
                || (uint)sourceY >= (uint)original.Dimensions.Height) continue;
            var destination = pixels.AsSpan(
                checked(((y - region.Y) * region.Width + x - region.X) * 4), 4);
            var source = original.PixelAt(sourceX, sourceY);
            if (source.SequenceEqual(destination)) continue;
            source.CopyTo(destination);
            changed = true;
        }

        return new(region, pixels, changed);
    }

    private static DocumentRect DestinationBounds(IReadOnlyList<DocumentPoint> samples,
        double radius, PixelSize dimensions)
    {
        var left = dimensions.Width;
        var top = dimensions.Height;
        var right = 0;
        var bottom = 0;
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (!double.IsFinite(sample.X) || !double.IsFinite(sample.Y))
                throw new ArgumentOutOfRangeException(nameof(samples));
            left = Math.Min(left, Math.Clamp((int)Math.Floor(sample.X - radius), 0, dimensions.Width));
            top = Math.Min(top, Math.Clamp((int)Math.Floor(sample.Y - radius), 0, dimensions.Height));
            right = Math.Max(right, Math.Clamp((int)Math.Ceiling(sample.X + radius), 0, dimensions.Width));
            bottom = Math.Max(bottom, Math.Clamp((int)Math.Ceiling(sample.Y + radius), 0, dimensions.Height));
        }
        return right <= left || bottom <= top ? default : new(left, top, right - left, bottom - top);
    }
}
