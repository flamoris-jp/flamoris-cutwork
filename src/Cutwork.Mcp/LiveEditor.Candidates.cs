using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Mcp;

public sealed partial class LiveEditor
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    // Accessed only on the host lane. Each set owns <= 3 * FittingPixels bytes.
    private CandidateSet? candidates;

    private async Task<JsonElement> PartCandidatesAsync(RequestContext context, JsonElement args,
        CancellationToken cancellationToken)
    {
        var steps = args.GetProperty("steps").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        if (steps.Length is < 2 or > LiveLimits.Candidates
            || steps.Any(step => step is < 0 or > 100)
            || steps.Zip(steps.Skip(1)).Any(pair => pair.First >= pair.Second))
            throw new LiveException("candidate_steps");

        FittingSource source = null!;
        CapabilityGrant grant = null!;
        await context.ReadAsync(() =>
        {
            DemandIdle(cancellationToken);
            grant = currentGrant?.Invoke() ?? throw new LiveException(McpErrors.Unauthorized);
            if (!grant.IsActive) throw new LiveException(McpErrors.Unauthorized);
            source = new(Document.Original, Document.Dimensions, session.DocumentToken, Document.Revision);
            return Empty;
        });

        var prepared = await Task.Run(() =>
        {
            var budget = new LiveBudget();
            var points = Points(args.GetProperty("fence"));
            var bounds = LiveLimits.Fence(points, source.Dimensions, budget);
            // Reserve all mask copies and preview/encoding/JSON intermediates
            // before allocation, in addition to the existing fitter estimate.
            budget.Add(LiveLimits.Area(bounds) * steps.Length,
                LiveLimits.Area(bounds) * steps.Length * 64);
            cancellationToken.ThrowIfCancellationRequested();
            var fit = fitter.Create(source.Original, points);
            if (fit.PolygonPixelCount < PartToolController.MinimumFencePixels)
                throw new LiveException("fence_too_small");
            if (fit.Bounds != bounds) throw new LiveException(McpErrors.InvalidRequest);
            var masks = new Dictionary<string, PreparedPart>(StringComparer.Ordinal);
            var previews = new List<object>();
            int pngBytes = 0;
            foreach (int step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Copy even if a fitter returns its own mutable scratch buffer.
                var mask = fit.Adjust(step - fit.Step).ToArray();
                if (fit.Step != step || mask.Length != LiveLimits.Area(bounds))
                    throw new LiveException(McpErrors.InvalidRequest);
                int retained = mask.Count(value => value != 0);
                var image = LiveImages.Cutout(source.Original, bounds, mask,
                    args.GetProperty("maxEdge").GetInt32(), cancellationToken);
                pngBytes = checked(pngBytes + image.Png.Length);
                if (pngBytes > LiveLimits.CandidatePngBytes) throw new LiveException("png_limit");
                string id = Guid.NewGuid().ToString("N");
                masks.Add(id, new(bounds, mask));
                previews.Add(new
                {
                    candidateId = id, step, bounds,
                    remainingPixels = retained, fencePixels = fit.PolygonPixelCount,
                    retainedRatio = (double)retained / fit.PolygonPixelCount,
                    maskSha256 = Convert.ToHexString(SHA256.HashData(mask)).ToLowerInvariant(),
                    preview = Image(image, source.DocumentToken,
                        source.Revision.ToString(CultureInfo.InvariantCulture)),
                });
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = Element(new
            {
                source.DocumentToken, revision = source.Revision.ToString(CultureInfo.InvariantCulture),
                expiresInSeconds = (int)LiveLimits.CandidateLifetime.TotalSeconds,
                candidates = previews,
            });
            return (Masks: masks, Result: result);
        }, cancellationToken);

        return await context.ReadAsync(() =>
        {
            DemandIdle(cancellationToken);
            if (!grant.IsActive || !ReferenceEquals(grant, currentGrant?.Invoke()))
                throw new LiveException(McpErrors.Unauthorized);
            if (session.DocumentToken != source.DocumentToken)
                throw new LiveException(McpErrors.StaleDocument);
            if (Document.Revision != source.Revision)
                throw new LiveException(McpErrors.StaleRevision);
            candidates = new(source.DocumentToken, source.Revision, grant,
                clock.GetTimestamp(), prepared.Masks);
            return prepared.Result;
        });
    }

    private IReadOnlyDictionary<int, PreparedPart> ResolveCandidates(JsonElement args,
        IReadOnlyDictionary<int, PreparedPart> prepared)
    {
        var operations = args.GetProperty("operations").EnumerateArray().ToArray();
        if (!operations.Any(op => op.TryGetProperty("candidateId", out _))) return prepared;
        var set = candidates;
        if (set is null || set.DocumentToken != session.DocumentToken || set.Revision != Document.Revision
            || !set.Grant.IsActive || !ReferenceEquals(set.Grant, currentGrant?.Invoke())
            || clock.GetElapsedTime(set.CreatedAt) >= LiveLimits.CandidateLifetime)
        {
            candidates = null;
            throw new LiveException("stale_candidate");
        }
        var resolved = prepared.ToDictionary(pair => pair.Key, pair => pair.Value);
        for (int i = 0; i < operations.Length; i++)
        {
            if (!operations[i].TryGetProperty("candidateId", out var id)) continue;
            if (!set.Masks.TryGetValue(id.GetString()!, out var mask))
                throw new LiveException("stale_candidate");
            resolved.Add(i, mask);
        }
        return resolved;
    }

    private sealed record CandidateSet(string DocumentToken, long Revision, CapabilityGrant Grant,
        long CreatedAt, IReadOnlyDictionary<string, PreparedPart> Masks);
}
