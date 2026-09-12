namespace Flamoris.Cutwork.Core;

/// <summary>
/// Deterministic document-space sampling for brush-like gestures. The sampler
/// suppresses duplicate input and emits evenly spaced points across sparse
/// pointer events without owning document mutation or UI state.
/// </summary>
public sealed class StrokeSampler
{
    public const int MaximumBatchSamples = 8;
    private const double DuplicateEpsilon = 1e-6;
    private DocumentPoint? _lastInput;
    private double _distanceSinceEmission;

    public StrokeSampler(double radius)
    {
        if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        Radius = radius;
        Spacing = Math.Max(0.5, radius * 0.5);
    }

    public double Radius { get; }
    public double Spacing { get; }

    public IReadOnlyList<DocumentPoint> Begin(DocumentPoint point)
    {
        Validate(point);
        _lastInput = point;
        _distanceSinceEmission = 0;
        return [point];
    }

    public IReadOnlyList<DocumentPoint> Add(DocumentPoint point)
    {
        Validate(point);
        if (_lastInput is not { } previous) return Begin(point);
        var dx = point.X - previous.X;
        var dy = point.Y - previous.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= DuplicateEpsilon) return [];

        var result = new List<DocumentPoint>();
        var directionX = dx / distance;
        var directionY = dy / distance;
        var traversed = 0.0;
        var remaining = distance;
        var needed = Spacing - _distanceSinceEmission;
        while (remaining + DuplicateEpsilon >= needed)
        {
            traversed += needed;
            result.Add(new(previous.X + directionX * traversed, previous.Y + directionY * traversed));
            remaining -= needed;
            needed = Spacing;
            _distanceSinceEmission = 0;
        }
        _distanceSinceEmission += Math.Max(0, remaining);
        _lastInput = point;
        return result;
    }

    /// <summary>Splits one pointer event into deterministic, bounded authoring corridors.</summary>
    public static IEnumerable<IReadOnlyList<DocumentPoint>> Batch(
        IReadOnlyList<DocumentPoint> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        for (var offset = 0; offset < samples.Count; offset += MaximumBatchSamples)
        {
            var count = Math.Min(MaximumBatchSamples, samples.Count - offset);
            var batch = new DocumentPoint[count];
            for (var index = 0; index < count; index++) batch[index] = samples[offset + index];
            yield return batch;
        }
    }

    private static void Validate(DocumentPoint point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentOutOfRangeException(nameof(point));
    }
}
