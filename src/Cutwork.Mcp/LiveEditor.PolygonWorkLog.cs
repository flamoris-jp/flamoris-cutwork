using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Mcp;

public sealed partial class LiveEditor
{
    private JsonElement ReadWorkLog(JsonElement args)
    {
        int after=args.GetProperty("afterSequence").GetInt32(), count=args.GetProperty("count").GetInt32();
        var entries=new List<ArtworkWorkLogEntry>();int bytes=0;
        foreach(var entry in Document.WorkLog.Where(x=>x.Sequence>after).Take(count))
        {
            int size=ArtworkWorkLog.Encode(entry).Length+1;
            if(bytes+size>64*1024)break;
            entries.Add(entry);bytes+=size;
        }
        return Element(new {documentToken=session.DocumentToken,revision=Revision(),total=Document.WorkLog.Count,
            nextAfterSequence=entries.LastOrDefault()?.Sequence??after,entries,returnedBytes=bytes,
            dirty=session.IsDirty,maximumEntries=ArtworkWorkLog.MaximumEntries,maximumBytes=ArtworkWorkLog.MaximumBytes});
    }

    private async Task<JsonElement> PolygonPartAsync(RequestContext context,string name,JsonElement args,CancellationToken ct)
    {
        var source=await CaptureFittingSourceAsync(context,ct);
        var fence=Points(args.GetProperty("fence"));DocumentRect bounds;
        try{bounds=PolygonPart.Validate(fence,source.Dimensions);}
        catch(ArgumentException e){throw new LiveException(e.Message);}
        long area=LiveLimits.Area(bounds);
        // Large mask work has its own bounded lane. Ordinary brush/fitter budgets stay unchanged.
        void DemandBudget()
        {
            if(area*(fence.Length+Math.Max(1L,Document.Layers.Count*2L))>PolygonPart.MaximumWork
                || area*8>PolygonPart.MaximumWorkingBytes)throw new LiveException("polygon_work_limit");
            if(name=="part_polygon_create" && 512+area+args.GetProperty("name").GetString()!.Length*2L>session.HistoryBudgetBytes)
                throw new LiveException("history_limit");
            if(name=="part_polygon_create" && Document.Layers.Count>=4096)throw new LiveException("layer_limit");
        }
        await context.ReadAsync(()=>{DemandIdle(ct);DemandBudget();return Empty;});
        var prepared=await Task.Run(()=>
        {
            try{return PolygonPart.Create(fence,source.Dimensions,ct);}
            catch(ArgumentException e){throw new LiveException(e.Message);}
        },ct);
        // Recheck identity, revision, busy, authorization and current budgets before disclosure/commit.
        JsonElement Complete()
        {
            DemandIdle(ct);
            if(session.DocumentToken!=source.DocumentToken)throw new LiveException(McpErrors.StaleDocument);
            if(Document.Revision!=source.Revision)throw new LiveException(McpErrors.StaleRevision);
            DemandBudget();
            if(name=="part_polygon_preview")
                return Element(new{documentToken=session.DocumentToken,revision=Revision(),prepared.Bounds,
                    prepared.PixelCount,method="exact-polygon",preview=Image(LiveImages.Mask(prepared.Bounds,prepared.Mask,
                        args.GetProperty("maxEdge").GetInt32()),session.DocumentToken,Revision())});
            editing(true);
            try
            {
                var part=new PartLayer(prepared.Bounds,prepared.Mask,args.GetProperty("name").GetString()!);
                using var tx=session.BeginTransaction();tx.Apply(new AddLayer(part));ct.ThrowIfCancellationRequested();tx.Commit(part.Id);
                return Element(new{documentToken=session.DocumentToken,revision=Revision(),partId=part.Id,
                    part.Bounds,prepared.PixelCount,method="exact-polygon"});
            }
            finally{editing(false);}
        }
        return name=="part_polygon_create"?await context.CommitAsync(Complete):await context.ReadAsync(Complete);
    }
}
