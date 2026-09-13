using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging;

/// <summary>Deterministic hard-circle Clone kernel over a conservative destination ROI.</summary>
public sealed class CloneRepairKernel : ICloneRepairKernel
{
    public CloneRepairPatch CreatePatch(OriginalAsset original, RepairLayer target,
        IReadOnlyList<DocumentPoint> destinationSamples, CloneStrokeMapping mapping, double radius)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(destinationSamples);
        if (!Enum.IsDefined(mapping.Mode)
            || !double.IsFinite(mapping.SourceAnchor.X) || !double.IsFinite(mapping.SourceAnchor.Y)
            || !double.IsFinite(mapping.DestinationAnchor.X) || !double.IsFinite(mapping.DestinationAnchor.Y))
            throw new ArgumentOutOfRangeException(nameof(mapping));
        if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        if (destinationSamples.Count == 0) return default;
        if (destinationSamples.Count > StrokeSampler.MaximumBatchSamples)
            throw new ArgumentOutOfRangeException(nameof(destinationSamples));

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
            DocumentPoint? coveringSample = null;
            // A batch represents sequential dabs. The last covering dab wins, matching the
            // order in which separate batches are applied while keeping work bounded to the ROI.
            for (var sampleIndex = destinationSamples.Count - 1; sampleIndex >= 0; sampleIndex--)
            {
                var sample = destinationSamples[sampleIndex];
                var dx = centerX - sample.X;
                var dy = centerY - sample.Y;
                if (dx * dx + dy * dy <= radiusSquared)
                {
                    coveringSample = sample;
                    break;
                }
            }
            if (coveringSample is not { } destinationSample) continue;

            var sourcePoint = mapping.SourceFor(new(centerX, centerY), destinationSample);
            var sourceX = (int)Math.Floor(sourcePoint.X);
            var sourceY = (int)Math.Floor(sourcePoint.Y);
            if ((uint)sourceX >= (uint)original.Dimensions.Width
                || (uint)sourceY >= (uint)original.Dimensions.Height) continue;
            var destination = pixels.AsSpan(
                checked(((y - region.Y) * region.Width + x - region.X) * 4), 4);
            var sourcePixel = original.PixelAt(sourceX, sourceY);
            if (sourcePixel.SequenceEqual(destination)) continue;
            sourcePixel.CopyTo(destination);
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
