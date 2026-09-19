namespace Flamoris.Cutwork.Core;

/// <summary>Shared document-space mask dabs for UI gestures and complete external strokes.</summary>
public static class MaskBrushKernel
{
    public static void Apply(EditTransaction transaction, PartLayer part,
        IReadOnlyList<DocumentPoint> samples, MaskPolarity polarity, double radius, PixelSize dimensions)
    {
        if (samples.Count > StrokeSampler.MaximumBatchSamples || !Enum.IsDefined(polarity)
            || !double.IsFinite(radius) || radius < MaskBrushController.MinimumRadius
            || radius > MaskBrushController.MaximumRadius) throw new ArgumentOutOfRangeException(nameof(samples));
        var region = default(DocumentRect);
        foreach (var sample in samples)
        {
            var dab = DabBounds(sample, radius, dimensions);
            region = region.Union(polarity == MaskPolarity.Add ? dab : dab.Intersect(part.Bounds));
        }
        if (region.IsEmpty) return;

        // A disconnected click must not inflate a compact Part into a near-full-frame rectangle.
        // Continuous sampled strokes naturally overlap the current local surface as they cross its edge.
        if (polarity == MaskPolarity.Add && region.Intersect(part.Bounds).IsEmpty) return;

        var after = part.CopyMaskWithTransparentOutside(region);
        var value = polarity == MaskPolarity.Add ? (byte)255 : (byte)0;
        var radiusSquared = radius * radius;
        for (var y = region.Y; y < region.Bottom; y++)
        for (var x = region.X; x < region.Right; x++)
        {
            var centerX = x + 0.5;
            var centerY = y + 0.5;
            if (!samples.Any(sample =>
                    (centerX - sample.X) * (centerX - sample.X)
                    + (centerY - sample.Y) * (centerY - sample.Y) <= radiusSquared)) continue;
            after[(y - region.Y) * region.Width + x - region.X] = value;
        }
        transaction.Apply(new MaskPatch(part.Id, region, after));
    }
    public static DocumentRect DabBounds(DocumentPoint point, double radius, PixelSize dimensions)
    {
        var left = Math.Clamp((int)Math.Floor(point.X - radius), 0, dimensions.Width);
        var top = Math.Clamp((int)Math.Floor(point.Y - radius), 0, dimensions.Height);
        var right = Math.Clamp((int)Math.Ceiling(point.X + radius), 0, dimensions.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(point.Y + radius), 0, dimensions.Height);
        return right <= left || bottom <= top ? default : new(left, top, right - left, bottom - top);
    }
}
