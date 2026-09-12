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
    private DocumentPoint _segmentStart;
    private DocumentPoint _segmentTarget;
    private double _segmentLength;
    private double _segmentTraversed;
    private bool _hasPendingSegment;
    private double _distanceSinceEmission;

    public StrokeSampler(double radius)
    {
        if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        Radius = radius;
        Spacing = Math.Max(0.5, radius * 0.5);
    }

    public double Radius { get; }
    public double Spacing { get; }
    public bool HasPendingSamples => _hasPendingSegment;

    public IReadOnlyList<DocumentPoint> Begin(DocumentPoint point)
    {
        Validate(point);
        _lastInput = point;
        _hasPendingSegment = false;
        _segmentLength = 0;
        _segmentTraversed = 0;
        _distanceSinceEmission = 0;
        return [point];
    }

    public IReadOnlyList<DocumentPoint> Add(DocumentPoint point)
    {
        Validate(point);
        if (_lastInput is not { } previous) return Begin(point);
        if (_hasPendingSegment)
        {
            if (Distance(point, _segmentTarget) <= DuplicateEpsilon) return TakePendingBatch();

            // Pointer events may outpace deferred processing. Keep only the latest target and
            // continue from the last emitted point; this bounds pending memory without creating
            // a second sampler or transaction.
            previous = PointOnPendingSegment();
            _lastInput = previous;
            _hasPendingSegment = false;
            _distanceSinceEmission = 0;
        }

        if (Distance(point, previous) <= DuplicateEpsilon) return [];
        QueueSegment(previous, point);
        return TakePendingBatch();
    }

    public IReadOnlyList<DocumentPoint> TakePendingBatch()
    {
        if (!_hasPendingSegment) return [];
        var result = new List<DocumentPoint>(MaximumBatchSamples);
        while (_hasPendingSegment && result.Count < MaximumBatchSamples)
        {
            var remaining = _segmentLength - _segmentTraversed;
            var needed = Spacing - _distanceSinceEmission;
            if (remaining + DuplicateEpsilon < needed)
            {
                _distanceSinceEmission += Math.Max(0, remaining);
                CompleteSegment();
                continue;
            }

            _segmentTraversed = Math.Min(_segmentLength, _segmentTraversed + needed);
            result.Add(PointOnPendingSegment());
            _distanceSinceEmission = 0;
            if (_segmentLength - _segmentTraversed <= DuplicateEpsilon) CompleteSegment();
        }
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

    private void QueueSegment(DocumentPoint start, DocumentPoint target)
    {
        _segmentStart = start;
        _segmentTarget = target;
        _segmentLength = Distance(start, target);
        _segmentTraversed = 0;
        _hasPendingSegment = true;
    }

    private DocumentPoint PointOnPendingSegment()
    {
        var ratio = _segmentLength <= DuplicateEpsilon ? 1 : _segmentTraversed / _segmentLength;
        return new(_segmentStart.X + (_segmentTarget.X - _segmentStart.X) * ratio,
            _segmentStart.Y + (_segmentTarget.Y - _segmentStart.Y) * ratio);
    }

    private void CompleteSegment()
    {
        _lastInput = _segmentTarget;
        _hasPendingSegment = false;
        _segmentLength = 0;
        _segmentTraversed = 0;
    }

    private static double Distance(DocumentPoint first, DocumentPoint second)
    {
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
