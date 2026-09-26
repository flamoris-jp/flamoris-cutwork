using System.Globalization;
using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Logging;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Mcp;

/// <summary>Cutwork-owned typed operations over the one authoritative session.</summary>
public sealed partial class LiveEditor(EditorSession session, McpPermission permission, Func<bool> humanBusy,
    Action<bool> editing, FlamorisLogger? logger = null, IPartBoundaryFitter? partFitter = null,
    Func<CapabilityGrant?>? currentGrant = null, TimeProvider? timeProvider = null)
{
    private readonly FlamorisLogger? log = logger;
    private readonly IPartBoundaryFitter fitter = partFitter ?? new GuriguriPartFitter();
    public EditorSession Session => session;
    private CutworkDocument Document => session.Document ?? throw new LiveException(McpErrors.HostUnavailable);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    public async Task<JsonElement> ExecuteAsync(RequestContext context, string name, JsonElement input,
        CancellationToken cancellationToken)
    {
        try
        {
            if (name == "part_preview")
                return await PartPreviewAsync(context, input, cancellationToken);
            if (name == "part_candidates")
                return await PartCandidatesAsync(context, input, cancellationToken);
            var preparedParts = name == "edit"
                ? await PreparePartsAsync(context, input, cancellationToken)
                : EmptyPreparedParts;
            return LiveSchema.IsEdit(name)
                ? await context.CommitAsync(() => Mutate(name, input, preparedParts, cancellationToken))
                : await context.ReadAsync(() => Query(name, input, cancellationToken));
        }
        catch (LiveException exception)
        {
            LogFailure(name, exception.Code, exception, exception.Code is McpErrors.StaleRevision
                or McpErrors.StaleDocument or McpErrors.Forbidden ? LogLevel.Warn : LogLevel.Debug);
            throw new McpFault(exception.Code);
        }
        catch (OperationCanceledException) { throw; }
        catch (EditException exception)
        {
            LogFailure(name, exception.Error.ToString(), exception, LogLevel.Error);
            throw new McpFault(exception.Error.ToString());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or OverflowException or FormatException)
        {
            LogFailure(name, McpErrors.InvalidRequest, exception, LogLevel.Error);
            throw new McpFault(McpErrors.InvalidRequest);
        }
    }

    private JsonElement Query(string name, JsonElement args, CancellationToken cancellationToken)
    {
        DemandIdle(cancellationToken);
        return name switch
        {
            "context" => Element(Context()),
            "layers" => Layers(args),
            "image" => ImageQuery(args, cancellationToken),
            _ => throw new LiveException(McpErrors.UnsupportedCapability),
        };
    }

    private JsonElement Mutate(string name, JsonElement args,
        IReadOnlyDictionary<int, PreparedPart> preparedParts, CancellationToken cancellationToken)
    {
        DemandIdle(cancellationToken);
        editing(true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name is "undo" or "redo")
            {
                var cost = session.NextHistoryWork(name == "redo");
                if (cost.EditCount > LiveLimits.Dabs * LiveLimits.Operations)
                    throw new LiveException("history_work_limit");
                new LiveBudget().Add(LiveLimits.Area(cost.DirtyRegion)
                    * Math.Max(1L, (Document.Layers.Count + (long)cost.TouchedLayerCount) * 2),
                    cost.RetainedBytes * 2 + cost.SurfaceBytes);
                cancellationToken.ThrowIfCancellationRequested();
                if (name == "undo") session.Undo(); else session.Redo();
                return Element(Context());
            }
            if (name != "edit") throw new LiveException(McpErrors.UnsupportedCapability);

            // Resolve against the starting revision before the batch's own edits
            // advance it. Candidate creation never re-runs the fitter.
            preparedParts = ResolveCandidates(args, preparedParts);
            var budget = new LiveBudget(session.HistoryBudgetBytes);
            var ids = new List<Guid?>();
            using var transaction = session.BeginTransaction();
            int operationIndex = 0;
            foreach (var operation in args.GetProperty("operations").EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ids.Add(Apply(operation, operationIndex++, preparedParts, ids,
                    transaction, budget, cancellationToken));
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit(ids.LastOrDefault(id => id.HasValue
                && Document.Layers.Any(layer => layer.Id == id)) ?? session.SelectedLayerId);
            return Element(new { documentToken = session.DocumentToken, revision = Revision(), affectedIds = ids });
        }
        finally { editing(false); }
    }

    private void DemandIdle(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session.HasActiveTransaction || humanBusy()) throw new LiveException(McpErrors.Busy);
    }

    private JsonElement Layers(JsonElement args)
    {
        var all = Document.Layers;
        return Element(new
        {
            documentToken = session.DocumentToken,
            revision = Revision(),
            total = all.Count,
            layers = all.Select((layer, index) => new
            {
                id = layer.Id,
                kind = layer.Kind.ToString(),
                name = Bound(layer.Name),
                semanticName = Bound(layer.SemanticName),
                bounds = layer.Bounds,
                visible = layer.Visible,
                compositorIndex = index,
                partOrder = (layer as PartLayer)?.PartOrder,
                ownerPartId = (layer as RepairLayer)?.OwnerPartId,
            }).Skip(args.GetProperty("offset").GetInt32())
                .Take(args.GetProperty("count").GetInt32()).ToArray(),
        });
    }

    private JsonElement ImageQuery(JsonElement args, CancellationToken cancellationToken)
    {
        string revision = Revision(), token = session.DocumentToken;
        var value = args.GetProperty("roi");
        DocumentRect? roi = value.ValueKind == JsonValueKind.Null ? null : new(
            value.GetProperty("x").GetInt32(), value.GetProperty("y").GetInt32(),
            value.GetProperty("width").GetInt32(), value.GetProperty("height").GetInt32());
        var image = LiveImages.Capture(Document, args.GetProperty("source").GetString()!,
            ResolveNullable(args.GetProperty("target"), []), roi,
            args.GetProperty("maxEdge").GetInt32(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Image(image, token, revision);
    }

    private async Task<JsonElement> PartPreviewAsync(RequestContext context, JsonElement args,
        CancellationToken cancellationToken)
    {
        var source = await CaptureFittingSourceAsync(context, cancellationToken);
        var result = await Task.Run(() =>
        {
            var prepared = PrepareFit(source, args, new LiveBudget(), cancellationToken);
            var image = LiveImages.Mask(prepared.Bounds, prepared.Mask,
                args.GetProperty("maxEdge").GetInt32());
            cancellationToken.ThrowIfCancellationRequested();
            return Image(image, source.DocumentToken,
                source.Revision.ToString(CultureInfo.InvariantCulture));
        }, cancellationToken);

        // The pure preparation above can outlive Stop, replacement or an ordinary
        // document edit. Re-enter the host lane before disclosing any prepared bytes.
        return await context.ReadAsync(() =>
        {
            DemandIdle(cancellationToken);
            if (session.DocumentToken != source.DocumentToken)
                throw new LiveException(McpErrors.StaleDocument);
            if (Document.Revision != source.Revision)
                throw new LiveException(McpErrors.StaleRevision);
            return result;
        });
    }

    private object Context() => new
    {
        documentToken = session.DocumentToken,
        revision = Revision(),
        historyStateRevision = session.CurrentRevision.ToString(CultureInfo.InvariantCulture),
        permission = permission == McpPermission.Edit ? "edit" : "readOnly",
        width = Document.Dimensions.Width,
        height = Document.Dimensions.Height,
        coordinateSpace = "document-pixels; half-open rectangles; pixel centers x+0.5,y+0.5",
        busy = humanBusy() || session.HasActiveTransaction,
        dirty = session.IsDirty,
        selectedLayerId = session.SelectedLayerId,
        canUndo = session.CanUndo,
        canRedo = session.CanRedo,
        undoCount = session.UndoCount,
        redoCount = session.RedoCount,
    };

    private async Task<IReadOnlyDictionary<int, PreparedPart>> PreparePartsAsync(
        RequestContext context, JsonElement args, CancellationToken cancellationToken)
    {
        var operations = args.GetProperty("operations").EnumerateArray().ToArray();
        var requested = operations.Select((operation, index) => (operation, index))
            .Where(item => item.operation.GetProperty("type").GetString() == "part.create"
                && !item.operation.TryGetProperty("candidateId", out _))
            .ToArray();
        if (requested.Length == 0) return EmptyPreparedParts;

        var source = await CaptureFittingSourceAsync(context, cancellationToken);
        return await Task.Run<IReadOnlyDictionary<int, PreparedPart>>(() =>
        {
            var prepared = new Dictionary<int, PreparedPart>();
            var budget = new LiveBudget();
            foreach (var item in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                prepared.Add(item.index, PrepareFit(source, item.operation, budget, cancellationToken));
            }
            return prepared;
        }, cancellationToken);
    }

    private async Task<FittingSource> CaptureFittingSourceAsync(RequestContext context,
        CancellationToken cancellationToken)
    {
        FittingSource? source = null;
        await context.ReadAsync(() =>
        {
            DemandIdle(cancellationToken);
            var document = Document;
            source = new(document.Original, document.Dimensions,
                session.DocumentToken, document.Revision);
            return Empty;
        });
        return source!;
    }

    private PreparedPart PrepareFit(FittingSource source, JsonElement operation,
        LiveBudget budget, CancellationToken cancellationToken)
    {
        var points = Points(operation.GetProperty("fence"));
        var bounds = LiveLimits.Fence(points, source.Dimensions, budget);
        cancellationToken.ThrowIfCancellationRequested();
        var fit = fitter.Create(source.Original, points);
        if (fit.PolygonPixelCount < PartToolController.MinimumFencePixels)
            throw new LiveException("fence_too_small");
        var mask = fit.Adjust(operation.GetProperty("step").GetInt32());
        cancellationToken.ThrowIfCancellationRequested();
        if (fit.Bounds != bounds || mask.Length != LiveLimits.Area(bounds))
            throw new LiveException(McpErrors.InvalidRequest);
        return new(bounds, mask);
    }

    private Guid? Apply(JsonElement operation, int operationIndex,
        IReadOnlyDictionary<int, PreparedPart> preparedParts, List<Guid?> ids,
        EditTransaction transaction, LiveBudget budget, CancellationToken cancellationToken)
    {
        string type = operation.GetProperty("type").GetString()!;
        Guid Target() => Resolve(operation.GetProperty("target"), ids);
        if (type is "layer.visible" or "layer.reorder" or "layer.delete" or "patch.transform")
        {
            var layer = Document.GetLayer(Target());
            budget.Dirty(layer.Bounds, Document.Layers.Count);
        }
        if (Document.Layers.Count >= 4096 && type is "part.create" or "patch.create" or "clone.stroke")
            throw new LiveException("layer_limit");
        if (type is "layer.rename" or "layer.semantic")
        {
            var existing = Document.GetLayer(Target());
            long bytes = 128 + ((long)existing.Name.Length + (existing.SemanticName?.Length ?? 0) + 256) * 2;
            budget.Add(0, bytes);
            budget.ReserveHistory(bytes);
        }
        else if (type is "layer.visible" or "layer.reorder") budget.ReserveHistory(128);
        else if (type == "patch.transform") budget.ReserveHistory(192);

        switch (type)
        {
            case "part.create":
                if (!preparedParts.TryGetValue(operationIndex, out var fit))
                    throw new LiveException(McpErrors.InvalidRequest);
                var fitBounds = fit.Bounds;
                if (operation.TryGetProperty("candidateId", out _))
                    budget.Add(LiveLimits.Area(fitBounds), LiveLimits.Area(fitBounds) * 2);
                else
                    fitBounds = LiveLimits.Fence(Points(operation.GetProperty("fence")),
                        Document.Dimensions, budget);
                if (fitBounds != fit.Bounds || fit.Mask.Length != LiveLimits.Area(fitBounds))
                    throw new LiveException(McpErrors.StaleRevision);
                budget.ReserveHistory(128 + LiveLimits.Area(fit.Bounds)
                    + operation.GetProperty("name").GetString()!.Length * 2L);
                var part = new PartLayer(fit.Bounds, fit.Mask, operation.GetProperty("name").GetString()!);
                budget.Dirty(part.Bounds, Document.Layers.Count + 1);
                transaction.Apply(new AddLayer(part));
                return part.Id;
            case "layer.rename":
                transaction.Apply(new RenameLayer(Target(), operation.GetProperty("name").GetString()!));
                return Target();
            case "layer.semantic":
                transaction.Apply(new SetLayerSemanticName(Target(), operation.GetProperty("semanticName").GetString()));
                return Target();
            case "layer.visible":
                transaction.Apply(new SetLayerVisibility(Target(), operation.GetProperty("visible").GetBoolean()));
                return Target();
            case "layer.reorder":
                transaction.Apply(new ReorderLayer(Target(), operation.GetProperty("index").GetInt32()));
                return Target();
            case "layer.delete":
                var doomed = Document.GetLayer(Target());
                var removed = Document.Layers.Where(layer => layer.Id == doomed.Id
                    || (layer as RepairLayer)?.OwnerPartId == doomed.Id).ToArray();
                var retained = removed.Sum(layer => 192L + layer.Name.Length * 2L
                    + (layer.SemanticName?.Length ?? 0) * 2L
                    + LiveLimits.Area(layer is PatchLayer patch ? patch.SourceBounds : layer.Bounds)
                        * (layer is PartLayer ? 1L : 4L)
                    + (layer is PatchLayer patchLayer ? patchLayer.SourcePolygon.Count * 16L : 0));
                budget.Dirty(removed.Aggregate(default(DocumentRect), (region, layer) => region.Union(layer.Bounds)),
                    Document.Layers.Count);
                budget.Add(0, retained);
                budget.ReserveHistory(retained);
                transaction.Apply(new DeleteLayer(doomed.Id));
                return doomed.Id;
            case "mask.stroke":
                var maskPart = Document.GetLayer(Target()) as PartLayer
                    ?? throw new LiveException("invalid_target");
                double radius = operation.GetProperty("radius").GetDouble();
                var batches = LiveLimits.Stroke(Points(operation.GetProperty("points")), radius,
                    Document.Dimensions, budget);
                var polarity = Enum.Parse<MaskPolarity>(operation.GetProperty("polarity").GetString()!);
                ValidateGrowth(maskPart.Bounds, batches, radius, budget, 1);
                foreach (var batch in batches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MaskBrushKernel.Apply(transaction, maskPart, batch, polarity, radius, Document.Dimensions);
                }
                return maskPart.Id;
            case "clone.stroke":
                return Clone(operation, ids, transaction, budget, cancellationToken);
            case "patch.create":
                var fence = Points(operation.GetProperty("fence"));
                var sourceBounds = LiveLimits.Fence(fence, Document.Dimensions, budget);
                budget.ReserveHistory(192 + LiveLimits.Area(sourceBounds) * 4 + fence.Length * 16L
                    + operation.GetProperty("name").GetString()!.Length * 2L);
                var source = new PatchSourceSampler().Freeze(Document.Original, fence);
                var patch = new PatchLayer(source.SourceBounds, source.StraightBgra.Span, Transform(operation),
                    source.SourcePolygon, operation.GetProperty("name").GetString()!);
                LiveLimits.Surface(patch.Bounds);
                budget.Dirty(patch.Bounds, Document.Layers.Count + 1);
                transaction.Apply(new AddLayer(patch));
                return patch.Id;
            case "patch.transform":
                var oldPatch = Document.GetLayer(Target()) as PatchLayer
                    ?? throw new LiveException("invalid_target");
                var transform = Transform(operation);
                var transformedBounds = PatchLayer.CalculateBounds(oldPatch.SourceSize, transform);
                LiveLimits.Surface(transformedBounds);
                budget.Dirty(oldPatch.Bounds.Union(transformedBounds), Document.Layers.Count);
                transaction.Apply(new SetPatchTransform(oldPatch.Id, transform));
                return oldPatch.Id;
            default:
                throw new LiveException("unknown_operation");
        }
    }

    private void ValidateGrowth(DocumentRect bounds, IReadOnlyList<IReadOnlyList<DocumentPoint>> batches,
        double radius, LiveBudget budget, int channels)
    {
        foreach (var batch in batches)
        {
            var roi = batch.Aggregate(default(DocumentRect),
                (region, point) => region.Union(MaskBrushKernel.DabBounds(point, radius, Document.Dimensions)));
            budget.ReserveHistory(128 + LiveLimits.Area(roi) * channels * 2);
            var next = bounds.Union(roi);
            LiveLimits.Surface(next);
            if (next != bounds) budget.Add(LiveLimits.Area(next), LiveLimits.Area(next) * channels * 2);
            budget.Dirty(roi, Document.Layers.Count + 1);
            bounds = next;
        }
    }

    private Guid? Clone(JsonElement operation, List<Guid?> ids, EditTransaction transaction,
        LiveBudget budget, CancellationToken cancellationToken)
    {
        var target = ResolveNullable(operation.GetProperty("target"), ids);
        var owner = ResolveNullable(operation.GetProperty("ownerPart"), ids);
        bool global = operation.GetProperty("global").GetBoolean();
        if ((target.HasValue ? 1 : 0) + (owner.HasValue ? 1 : 0) + (global ? 1 : 0) != 1)
            throw new LiveException("clone_target_required");
        if (owner is { } partId && Document.GetLayer(partId) is not PartLayer)
            throw new LiveException("invalid_owner");
        var points = Points(operation.GetProperty("points"));
        double radius = operation.GetProperty("radius").GetDouble();
        var batches = LiveLimits.Stroke(points, radius, Document.Dimensions, budget);
        var anchor = Point(operation.GetProperty("source"));
        LiveLimits.Point(anchor, Document.Dimensions);
        var mapping = new CloneStrokeMapping(Enum.Parse<CloneSamplingMode>(operation.GetProperty("mode").GetString()!),
            anchor, points[0]);
        var repair = target is { } id
            ? Document.GetLayer(id) as RepairLayer ?? throw new LiveException("invalid_target")
            : new RepairLayer(new((int)Math.Floor(points[0].X), (int)Math.Floor(points[0].Y), 1, 1),
                new byte[4], ownerPartId: owner);
        ValidateGrowth(repair.Bounds, batches, radius, budget, 4);
        if (target is null) budget.ReserveHistory(132 + repair.Name.Length * 2L);
        bool added = target.HasValue;
        var kernel = new CloneRepairKernel();
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var patch = kernel.CreatePatch(Document.Original, repair, batch, mapping, radius);
            if (!patch.HasChanges) continue;
            if (!added)
            {
                transaction.Apply(new AddLayer(repair));
                added = true;
            }
            transaction.Apply(new RasterPatch(repair.Id, patch.Region, patch.StraightBgra.Span));
        }
        return added ? repair.Id : null;
    }

    private void LogFailure(string tool, string code, Exception exception, LogLevel level)
    {
        var category = LiveSchema.IsEdit(tool) ? "mcp.command" : "mcp.query";
        var safeTool = LiveSchema.Schemas.ContainsKey(tool) ? tool : "unknown";
        var properties = new Dictionary<string, object?>
        {
            ["tool"] = safeTool,
            ["error"] = code,
            ["documentToken"] = session.DocumentToken,
            ["revision"] = session.Document?.Revision,
        };
        if (level == LogLevel.Error) log?.Error(category, "MCP operation failed", exception, properties);
        else if (level == LogLevel.Warn) log?.Warn(category, "MCP operation rejected", properties);
        else log?.Debug(category, "MCP operation rejected", properties);
    }

    private string Revision() => Document.Revision.ToString(CultureInfo.InvariantCulture);
    private static string? Bound(string? value) => value is { Length: > 256 } ? value[..256] : value;
    private static PatchTransform Transform(JsonElement operation)
    {
        var transform = operation.GetProperty("transform");
        return new(transform.GetProperty("centerX").GetDouble(), transform.GetProperty("centerY").GetDouble(),
            transform.GetProperty("scale").GetDouble(), transform.GetProperty("rotationDegrees").GetDouble());
    }
    private static DocumentPoint Point(JsonElement value) => new(
        value.GetProperty("x").GetDouble(), value.GetProperty("y").GetDouble());
    private static DocumentPoint[] Points(JsonElement value) => value.EnumerateArray().Select(Point).ToArray();
    private static Guid? ResolveNullable(JsonElement value, List<Guid?> ids) =>
        value.ValueKind == JsonValueKind.Null ? null : Resolve(value, ids);
    private static Guid Resolve(JsonElement value, List<Guid?> ids)
    {
        string text = value.GetString()!;
        if (text.StartsWith('@'))
        {
            int index = int.Parse(text[1..], CultureInfo.InvariantCulture);
            if (index >= ids.Count || ids[index] is not { } result) throw new LiveException("invalid_reference");
            return result;
        }
        return Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty
            ? id : throw new LiveException("invalid_id");
    }
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Json);
    private static JsonElement Image(LiveImage image, string token, string revision) => Element(new
    {
        documentToken = token,
        revision,
        mimeType = "image/png",
        pngBase64 = Convert.ToBase64String(image.Png),
        image.SourceBounds,
        image.Crop,
        image.Width,
        image.Height,
        image.DocumentPixelsPerOutputX,
        image.DocumentPixelsPerOutputY,
        mapping = "source pixel=floor(crop origin+(output pixel+0.5)*documentPixelsPerOutput), clamped to crop",
    });

    private static readonly IReadOnlyDictionary<int, PreparedPart> EmptyPreparedParts =
        new Dictionary<int, PreparedPart>();
    private sealed record FittingSource(OriginalAsset Original, PixelSize Dimensions,
        string DocumentToken, long Revision);
    private sealed record PreparedPart(DocumentRect Bounds, byte[] Mask);
}
