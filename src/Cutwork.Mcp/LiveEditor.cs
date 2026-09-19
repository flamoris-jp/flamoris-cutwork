using System.Globalization;
using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using ModelContextProtocol.Protocol;

namespace Flamoris.Cutwork.Mcp;

/// <summary>All entry/continuation calls run on the shared application dispatcher.</summary>
public sealed class LiveEditor(EditorSession session, LiveAccess access, Func<bool> humanBusy,
    Func<Task> yield, Action<bool> editing)
{
    private bool running;
    public EditorSession Session => session;
    private CutworkDocument Document => session.Document ?? throw new LiveException("no_document");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private void Guard(CancellationToken ct, bool edit = false)
    {
        access.Demand(edit, ct);
        if (humanBusy()) throw new LiveException("busy");
    }
    public async Task<CallToolResult> CallAsync(string name, JsonElement args, CancellationToken ct = default)
    {
        try
        {
            LiveSchema.Validate(name, args);
            access.Demand(LiveSchema.IsEdit(name), ct);
            if (name == "context") return Result(Context());
            if (running || session.HasActiveTransaction || humanBusy()) throw new LiveException("busy");
            running = true;
            try
            {
                if (LiveSchema.IsEdit(name))
                {
                    if (args.GetProperty("documentToken").GetString() != session.DocumentToken) throw new LiveException("document_conflict");
                    if (args.GetProperty("expectedRevision").GetString() != Revision()) throw new LiveException("revision_conflict");
                }
                if (name == "layers")
                {
                    var all = Document.Layers;
                    return Result(new { documentToken = session.DocumentToken, revision = Revision(), total = all.Count,
                        layers = all.Select((l, i) => new { id = l.Id, kind = l.Kind.ToString(), name = Bound(l.Name), semanticName = Bound(l.SemanticName),
                            bounds = l.Bounds, visible = l.Visible, compositorIndex = i, partOrder = (l as PartLayer)?.PartOrder,
                            ownerPartId = (l as RepairLayer)?.OwnerPartId }).Skip(args.GetProperty("offset").GetInt32()).Take(args.GetProperty("count").GetInt32()).ToArray() });
                }
                if (name == "image")
                {
                    string revision = Revision(), token = session.DocumentToken;
                    var r = args.GetProperty("roi");
                    DocumentRect? roi = r.ValueKind == JsonValueKind.Null ? null : new(r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(), r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32());
                    var image = LiveImages.Capture(Document, args.GetProperty("source").GetString()!, ResolveNullable(args.GetProperty("target"), []), roi, args.GetProperty("maxEdge").GetInt32(), ct);
                    await yield(); Guard(ct);
                    return Image(image, token, revision);
                }
                if (name == "part_preview")
                {
                    string revision = Revision(), token = session.DocumentToken;
                    var prepared = await Fit(args, new LiveBudget(), ct);
                    var image = LiveImages.Mask(prepared.Bounds, prepared.Mask, args.GetProperty("maxEdge").GetInt32());
                    await yield(); Guard(ct);
                    return Image(image, token, revision);
                }
                editing(true);
                try
                {
                    Guard(ct, true);
                    if (name is "undo" or "redo")
                    {
                        var cost = session.NextHistoryWork(name == "redo");
                        if (cost.EditCount > LiveLimits.Dabs * LiveLimits.Operations) throw new LiveException("history_work_limit");
                        new LiveBudget().Add(LiveLimits.Area(cost.DirtyRegion) * Math.Max(1L, (Document.Layers.Count + (long)cost.TouchedLayerCount) * 2), cost.RetainedBytes * 2 + cost.SurfaceBytes);
                        Guard(ct, true);
                        if (name == "undo") session.Undo(); else session.Redo();
                        return Result(Context());
                    }
                    var budget = new LiveBudget(session.HistoryBudgetBytes);
                    var ids = new List<Guid?>();
                    using var transaction = session.BeginTransaction();
                    foreach (var op in args.GetProperty("operations").EnumerateArray())
                    {
                        await yield(); Guard(ct, true);
                        ids.Add(await Apply(op, ids, transaction, budget, ct));
                    }
                    await yield(); Guard(ct, true);
                    transaction.Commit(ids.LastOrDefault(id => id.HasValue && Document.Layers.Any(l => l.Id == id)) ?? session.SelectedLayerId);
                    return Result(new { documentToken = session.DocumentToken, revision = Revision(), affectedIds = ids });
                }
                finally { editing(false); }
            }
            finally { running = false; }
        }
        catch (LiveException e) { return Error(e.Code); }
        catch (OperationCanceledException) { return Error("cancelled"); }
        catch (EditException e) { return Error(e.Error.ToString()); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException or FormatException)
        { return Error("invalid_operation"); }
    }
    private object Context() => new { documentToken = session.DocumentToken, revision = Revision(), historyStateRevision = session.CurrentRevision.ToString(CultureInfo.InvariantCulture),
        permission = access.Permission.ToString(), width = Document.Dimensions.Width, height = Document.Dimensions.Height,
        coordinateSpace = "document-pixels; half-open rectangles; pixel centers x+0.5,y+0.5",
        busy = running || humanBusy() || session.HasActiveTransaction, dirty = session.IsDirty, selectedLayerId = session.SelectedLayerId,
        canUndo = session.CanUndo, canRedo = session.CanRedo, undoCount = session.UndoCount, redoCount = session.RedoCount };
    private string Revision() => Document.Revision.ToString(CultureInfo.InvariantCulture);
    private static string? Bound(string? s) => s is { Length: > 256 } ? s[..256] : s;
    private async Task<(DocumentRect Bounds, byte[] Mask)> Fit(JsonElement op, LiveBudget budget, CancellationToken ct, bool authored = false)
    {
        var points = Points(op.GetProperty("fence"));
        var bounds = LiveLimits.Fence(points, Document.Dimensions, budget);
        if (authored) budget.ReserveHistory(128 + LiveLimits.Area(bounds) + op.GetProperty("name").GetString()!.Length * 2L);
        var original = Document.Original; int step = op.GetProperty("step").GetInt32();
        var result = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var fit = new GuriguriPartFitter().Create(original, points);
            if (fit.PolygonPixelCount < PartToolController.MinimumFencePixels) throw new LiveException("fence_too_small");
            var mask = fit.Adjust(step); ct.ThrowIfCancellationRequested();
            return (fit.Bounds, mask);
        }, ct);
        Guard(ct);
        return result;
    }
    private async Task<Guid?> Apply(JsonElement op, List<Guid?> ids, EditTransaction tx, LiveBudget budget, CancellationToken ct)
    {
        string type = op.GetProperty("type").GetString()!;
        Guid Target() => Resolve(op.GetProperty("target"), ids);
        // Notifications drive the real WPF compositor too. Bound dirty-area work on metadata
        // operations, not only the brush kernel's own buffers.
        if (type is "layer.visible" or "layer.reorder" or "layer.delete" or "patch.transform")
        {
            var layer = Document.GetLayer(Target());
            budget.Dirty(layer.Bounds, Document.Layers.Count);
        }
        if (Document.Layers.Count >= 4096 && type is "part.create" or "patch.create" or "clone.stroke") throw new LiveException("layer_limit");
        if (type is "layer.rename" or "layer.semantic")
        {
            var existing = Document.GetLayer(Target());
            long bytes = 128 + ((long)existing.Name.Length + (existing.SemanticName?.Length ?? 0) + 256) * 2;
            budget.Add(0, bytes); budget.ReserveHistory(bytes);
        }
        else if (type is "layer.visible" or "layer.reorder") budget.ReserveHistory(128);
        else if (type == "patch.transform") budget.ReserveHistory(192);
        switch (type)
        {
            case "part.create":
                var fit = await Fit(op, budget, ct, authored: true);
                var part = new PartLayer(fit.Bounds, fit.Mask, op.GetProperty("name").GetString()!);
                budget.Dirty(part.Bounds, Document.Layers.Count + 1);
                tx.Apply(new AddLayer(part)); return part.Id;
            case "layer.rename": tx.Apply(new RenameLayer(Target(), op.GetProperty("name").GetString()!)); return Target();
            case "layer.semantic": tx.Apply(new SetLayerSemanticName(Target(), op.GetProperty("semanticName").GetString())); return Target();
            case "layer.visible": tx.Apply(new SetLayerVisibility(Target(), op.GetProperty("visible").GetBoolean())); return Target();
            case "layer.reorder": tx.Apply(new ReorderLayer(Target(), op.GetProperty("index").GetInt32())); return Target();
            case "layer.delete":
                var doomed = Document.GetLayer(Target());
                var removed = Document.Layers.Where(l => l.Id == doomed.Id || (l as RepairLayer)?.OwnerPartId == doomed.Id).ToArray();
                var retained = removed.Sum(l => 192L + l.Name.Length * 2L + (l.SemanticName?.Length ?? 0) * 2L
                    + LiveLimits.Area(l is PatchLayer p ? p.SourceBounds : l.Bounds) * (l is PartLayer ? 1L : 4L)
                    + (l is PatchLayer patchLayer ? patchLayer.SourcePolygon.Count * 16L : 0));
                budget.Dirty(removed.Aggregate(default(DocumentRect), (r, l) => r.Union(l.Bounds)), Document.Layers.Count);
                budget.Add(0, retained); budget.ReserveHistory(retained); tx.Apply(new DeleteLayer(doomed.Id)); return doomed.Id;
            case "mask.stroke":
                var maskPart = Document.GetLayer(Target()) as PartLayer ?? throw new LiveException("invalid_target");
                double radius = op.GetProperty("radius").GetDouble();
                var batches = LiveLimits.Stroke(Points(op.GetProperty("points")), radius, Document.Dimensions, budget);
                var polarity = Enum.Parse<MaskPolarity>(op.GetProperty("polarity").GetString()!);
                ValidateGrowth(maskPart.Bounds, batches, radius, budget, 1);
                foreach (var batch in batches)
                {
                    Guard(ct, true);
                    MaskBrushKernel.Apply(tx, maskPart, batch, polarity, radius, Document.Dimensions);
                    await yield();
                }
                return maskPart.Id;
            case "clone.stroke": return await Clone(op, ids, tx, budget, ct);
            case "patch.create":
                var fence = Points(op.GetProperty("fence"));
                var sourceBounds = LiveLimits.Fence(fence, Document.Dimensions, budget);
                budget.ReserveHistory(192 + LiveLimits.Area(sourceBounds) * 4 + fence.Length * 16L + op.GetProperty("name").GetString()!.Length * 2L);
                var source = new PatchSourceSampler().Freeze(Document.Original, fence);
                var patch = new PatchLayer(source.SourceBounds, source.StraightBgra.Span, Transform(op), source.SourcePolygon, op.GetProperty("name").GetString()!);
                LiveLimits.Surface(patch.Bounds); budget.Dirty(patch.Bounds, Document.Layers.Count + 1); tx.Apply(new AddLayer(patch)); return patch.Id;
            case "patch.transform":
                var oldPatch = Document.GetLayer(Target()) as PatchLayer ?? throw new LiveException("invalid_target");
                var transform = Transform(op);
                var transformedBounds = PatchLayer.CalculateBounds(oldPatch.SourceSize, transform);
                LiveLimits.Surface(transformedBounds);
                budget.Dirty(oldPatch.Bounds.Union(transformedBounds), Document.Layers.Count);
                tx.Apply(new SetPatchTransform(oldPatch.Id, transform)); return oldPatch.Id;
            default: throw new LiveException("unknown_operation");
        }
    }
    private void ValidateGrowth(DocumentRect bounds, IReadOnlyList<IReadOnlyList<DocumentPoint>> batches, double radius, LiveBudget budget, int channels)
    {
        foreach (var batch in batches)
        {
            var roi = batch.Aggregate(default(DocumentRect), (r, p) => r.Union(MaskBrushKernel.DabBounds(p, radius, Document.Dimensions)));
            budget.ReserveHistory(128 + LiveLimits.Area(roi) * channels * 2);
            var next = bounds.Union(roi); LiveLimits.Surface(next);
            if (next != bounds) budget.Add(LiveLimits.Area(next), LiveLimits.Area(next) * channels * 2);
            budget.Dirty(roi, Document.Layers.Count + 1);
            bounds = next;
        }
    }
    private async Task<Guid?> Clone(JsonElement op, List<Guid?> ids, EditTransaction tx, LiveBudget budget, CancellationToken ct)
    {
        var target = ResolveNullable(op.GetProperty("target"), ids);
        var owner = ResolveNullable(op.GetProperty("ownerPart"), ids);
        bool global = op.GetProperty("global").GetBoolean();
        if ((target.HasValue ? 1 : 0) + (owner.HasValue ? 1 : 0) + (global ? 1 : 0) != 1) throw new LiveException("clone_target_required");
        if (owner is { } partId && Document.GetLayer(partId) is not PartLayer) throw new LiveException("invalid_owner");
        var points = Points(op.GetProperty("points")); double radius = op.GetProperty("radius").GetDouble();
        var batches = LiveLimits.Stroke(points, radius, Document.Dimensions, budget);
        var anchor = Point(op.GetProperty("source")); LiveLimits.Point(anchor, Document.Dimensions);
        var mapping = new CloneStrokeMapping(Enum.Parse<CloneSamplingMode>(op.GetProperty("mode").GetString()!), anchor, points[0]);
        var repair = target is { } id ? Document.GetLayer(id) as RepairLayer ?? throw new LiveException("invalid_target")
            : new RepairLayer(new((int)Math.Floor(points[0].X), (int)Math.Floor(points[0].Y), 1, 1), new byte[4], ownerPartId: owner);
        ValidateGrowth(repair.Bounds, batches, radius, budget, 4);
        if (target is null) budget.ReserveHistory(132 + repair.Name.Length * 2L);
        bool added = target.HasValue;
        var kernel = new CloneRepairKernel();
        foreach (var batch in batches)
        {
            Guard(ct, true);
            var patch = kernel.CreatePatch(Document.Original, repair, batch, mapping, radius);
            if (patch.HasChanges)
            {
                if (!added) { tx.Apply(new AddLayer(repair)); added = true; }
                tx.Apply(new RasterPatch(repair.Id, patch.Region, patch.StraightBgra.Span));
            }
            await yield();
        }
        return added ? repair.Id : null;
    }
    private static PatchTransform Transform(JsonElement op)
    {
        var t = op.GetProperty("transform");
        return new(t.GetProperty("centerX").GetDouble(), t.GetProperty("centerY").GetDouble(), t.GetProperty("scale").GetDouble(), t.GetProperty("rotationDegrees").GetDouble());
    }
    private static DocumentPoint Point(JsonElement p) => new(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble());
    private static DocumentPoint[] Points(JsonElement p) => p.EnumerateArray().Select(Point).ToArray();
    private static Guid? ResolveNullable(JsonElement p, List<Guid?> ids) => p.ValueKind == JsonValueKind.Null ? null : Resolve(p, ids);
    private static Guid Resolve(JsonElement p, List<Guid?> ids)
    {
        string value = p.GetString()!;
        if (value.StartsWith('@'))
        {
            int index = int.Parse(value[1..], CultureInfo.InvariantCulture);
            if (index >= ids.Count || ids[index] is not { } result) throw new LiveException("invalid_reference");
            return result;
        }
        return Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw new LiveException("invalid_id");
    }
    public static CallToolResult Result(object value) => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, Json) }] };
    public static CallToolResult Error(string code) => new() { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = code }) }] };
    private static CallToolResult Image(LiveImage image, string token, string revision) => new() { Content = [
        new TextContentBlock { Text = JsonSerializer.Serialize(new { documentToken = token, revision, image.SourceBounds, image.Crop,
            image.Width, image.Height, image.DocumentPixelsPerOutputX, image.DocumentPixelsPerOutputY,
            mapping = "source pixel=floor(crop origin+(output pixel+0.5)*documentPixelsPerOutput), clamped to crop" }, Json) },
        ImageContentBlock.FromBytes(image.Png, "image/png") ] };
}
