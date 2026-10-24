using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Mcp;

public sealed partial class LiveEditor
{
    private RepairCandidateSet? repairCandidates;
    private sealed record PreparedClone(Guid? Target, Guid? Owner, DocumentRect Bounds, byte[] Pixels,
        DocumentRect SourceBounds, int ChangedPixels);
    private sealed record RepairCandidateSet(string Token,long Revision,CapabilityGrant Grant,long Created,
        Dictionary<string,PreparedClone> Values);

    private PreparedClone PrepareClone(JsonElement op,List<Guid?> ids,LiveBudget budget,CancellationToken ct)
    {
        Guid? target=ResolveNullable(op.GetProperty("target"),ids), owner=ResolveNullable(op.GetProperty("ownerPart"),ids);
        bool global=op.GetProperty("global").GetBoolean();
        if((target.HasValue?1:0)+(owner.HasValue?1:0)+(global?1:0)!=1) throw new LiveException("clone_target_required");
        if(owner is {} part && Document.GetLayer(part) is not PartLayer) throw new LiveException("invalid_owner");
        var existing=target is {} id ? Document.GetLayer(id) as RepairLayer ?? throw new LiveException("invalid_target") : null;
        var points=Points(op.GetProperty("points")); double radius=op.GetProperty("radius").GetDouble();
        var batches=LiveLimits.Stroke(points,radius,Document.Dimensions,budget);
        var source=Point(op.GetProperty("source")); LiveLimits.Point(source,Document.Dimensions);
        double Value(string key,double fallback)=>op.TryGetProperty(key,out var v)?v.GetDouble():fallback;
        var mapping=new CloneStrokeMapping(Enum.Parse<CloneSamplingMode>(op.GetProperty("mode").GetString()!),
            source,points[0],Value("sourceScale",1),Value("sourceRotationDegrees",0),Value("hardness",1),Value("opacity",1),
            op.TryGetProperty("sourceKind",out var kind)?Enum.Parse<CloneSourceKind>(kind.GetString()!):CloneSourceKind.Original);
        mapping.Validate();
        var roi=batches.SelectMany(x=>x).Aggregate(default(DocumentRect),(r,p)=>r.Union(MaskBrushKernel.DabBounds(p,radius,Document.Dimensions)));
        LiveLimits.Surface(roi);
        var growth=(existing?.Bounds ?? default).Union(roi); LiveLimits.Surface(growth);
        var sourceBounds=TransformedCloneStroke.SourceBounds(batches.SelectMany(x=>x).ToArray(),mapping,radius,Document.Dimensions);
        if(mapping.SourceKind==CloneSourceKind.CompositeAtStrokeStart && !sourceBounds.IsEmpty)
        {
            LiveLimits.Surface(sourceBounds);
            budget.Add(LiveLimits.Area(sourceBounds)*Math.Max(1L,Document.Layers.Count*2L),LiveLimits.Area(sourceBounds)*4);
        }
        // Includes per-pixel maximum coverage, scratch pixels, final result and history.
        budget.Add(LiveLimits.Area(roi)*8,LiveLimits.Area(growth)*144);
        budget.ReserveHistory(512+LiveLimits.Area(growth)*8);
        budget.Dirty(roi,Document.Layers.Count+1);
        var scratchSession=new EditorSession(); scratchSession.Open(new CutworkDocument(Document.Original));
        var scratch=new RepairLayer(roi,existing?.CopyPixelsWithTransparentOutside(roi) ?? new byte[checked(roi.Width*roi.Height*4)]);
        scratchSession.Execute(new AddLayer(scratch));
        var before=scratch.CopyPixels(roi);
        var kernel=new CloneRepairKernel();
        ICloneStroke? stroke=mapping.IsLegacy?null:new TransformedCloneStroke(Document,mapping,radius,sourceBounds);
        foreach(var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            var patch=stroke?.Apply(scratch,batch) ?? kernel.CreatePatch(Document.Original,scratch,batch,mapping,radius);
            if(!patch.Region.IsEmpty && patch.HasChanges) scratchSession.Execute(new RasterPatch(scratch.Id,patch.Region,patch.StraightBgra.Span));
        }
        var after=scratch.CopyPixels(roi); int changed=0;
        for(int i=0;i<after.Length;i+=4) if(!after.AsSpan(i,4).SequenceEqual(before.AsSpan(i,4))) changed++;
        return new(target,owner,roi,after,sourceBounds,changed);
    }

    private Guid? ApplyClone(PreparedClone p,EditTransaction tx)
    {
        if(p.ChangedPixels==0) return p.Target;
        var repair=p.Target is {} id ? Document.GetLayer(id) as RepairLayer ?? throw new LiveException("invalid_target")
            : new RepairLayer(p.Bounds,p.Pixels,ownerPartId:p.Owner);
        if(p.Target is null) tx.Apply(new AddLayer(repair));
        else tx.Apply(new RasterPatch(repair.Id,p.Bounds,p.Pixels));
        return repair.Id;
    }

    private JsonElement CloneCandidates(JsonElement args,CancellationToken ct)
    {
        DemandIdle(ct);
        var grant=currentGrant?.Invoke() ?? throw new LiveException(McpErrors.Unauthorized);
        if(!grant.IsActive) throw new LiveException(McpErrors.Unauthorized);
        repairCandidates=null;
        var roi=ReadRoi(args); LiveLimits.Surface(roi);
        if(!DocumentRect.FromSize(Document.Dimensions).Contains(roi)) throw new LiveException("invalid_roi");
        var budget=new LiveBudget(session.HistoryBudgetBytes);
        int count=args.GetProperty("variants").GetArrayLength(),edge=args.GetProperty("maxEdge").GetInt32();
        long snapshot=DocumentSnapshot.EstimatedBytes(Document);
        budget.Add(LiveLimits.Area(roi)*Math.Max(1L,Document.Layers.Count*2L)*(count+1),
            snapshot*2+LiveLimits.Area(roi)*(count+1)*64);
        var before=CompositePixels(Document,roi,ct);
        var values=new Dictionary<string,PreparedClone>(); var previews=new List<object>(); int pngBytes=0;
        foreach(var op in args.GetProperty("variants").EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var p=PrepareClone(op,[],budget,ct);
            if(!roi.Contains(p.Bounds)) throw new LiveException("candidate_roi_required");
            var copied=DocumentSnapshot.Capture(Document); var temp=new EditorSession(); temp.Open(copied);
            using(var tx=temp.BeginTransaction())
            {
                if(p.Target is {} id) tx.Apply(new RasterPatch(id,p.Bounds,p.Pixels));
                else if(p.ChangedPixels>0) tx.Apply(new AddLayer(new RepairLayer(p.Bounds,p.Pixels,ownerPartId:p.Owner)));
                tx.Commit();
            }
            var after=CompositePixels(copied,roi,ct); var diff=new byte[before.Length];
            for(int i=0;i<diff.Length;i+=4) {for(int c=0;c<3;c++) diff[i+c]=(byte)Math.Abs(after[i+c]-before[i+c]);diff[i+3]=255;}
            string candidateId=Guid.NewGuid().ToString("N"); values.Add(candidateId,p);
            var images=new[]{LiveImages.Pixels(before,roi,edge),LiveImages.Pixels(after,roi,edge),LiveImages.Pixels(diff,roi,edge)};
            pngBytes+=images.Sum(x=>x.Png.Length); if(pngBytes>LiveLimits.CandidatePngBytes) throw new LiveException("png_limit");
            previews.Add(new {candidateId,p.ChangedPixels,p.SourceBounds, destinationBounds=p.Bounds,
                pixelSha256=Convert.ToHexString(SHA256.HashData(p.Pixels)).ToLowerInvariant(),
                before=Image(images[0],session.DocumentToken,Revision()),after=Image(images[1],session.DocumentToken,Revision()),
                difference=Image(images[2],session.DocumentToken,Revision())});
        }
        ct.ThrowIfCancellationRequested();
        repairCandidates=new(session.DocumentToken,Document.Revision,grant,clock.GetTimestamp(),values);
        return Element(new {documentToken=session.DocumentToken,revision=Revision(),expiresInSeconds=300,candidates=previews,
            workUnits=budget.Work,workingBytes=budget.Bytes});
    }

    private JsonElement CommitRepairCandidate(JsonElement args,CancellationToken ct)
    {
        var set=repairCandidates;
        if(set is null || set.Token!=session.DocumentToken || set.Revision!=Document.Revision || !set.Grant.IsActive
            || !ReferenceEquals(set.Grant,currentGrant?.Invoke()) || clock.GetElapsedTime(set.Created)>=LiveLimits.CandidateLifetime
            || !set.Values.TryGetValue(args.GetProperty("candidateId").GetString()!,out var p))
        {repairCandidates=null;throw new LiveException("stale_candidate");}
        var budget=new LiveBudget(session.HistoryBudgetBytes); budget.ReserveHistory(512+LiveLimits.Area(p.Bounds)*8);
        budget.Dirty(p.Bounds,Document.Layers.Count+1); ct.ThrowIfCancellationRequested();
        using var tx=session.BeginTransaction(); var id=ApplyClone(p,tx); tx.Commit(id??session.SelectedLayerId);
        repairCandidates=null;
        return Element(new {documentToken=session.DocumentToken,revision=Revision(),affectedId=id});
    }

    private JsonElement RepairInspect(JsonElement args,CancellationToken ct)
    {
        var roi=ReadRoi(args);LiveLimits.Surface(roi);
        if(!DocumentRect.FromSize(Document.Dimensions).Contains(roi)) throw new LiveException("invalid_roi");
        var part=Document.GetLayer(Resolve(args.GetProperty("target"),[])) as PartLayer ?? throw new LiveException("invalid_target");
        new LiveBudget().Add(LiveLimits.Area(roi)*Math.Max(1L,Document.Layers.Count*2L),LiveLimits.Area(roi)*32);
        var holes=new byte[roi.Width*roi.Height]; var contour=new List<object>();
        long b=0,g=0,r=0,a=0,n=0; int holeCount=0;
        for(int y=roi.Y;y<roi.Bottom;y++)
        {
            ct.ThrowIfCancellationRequested();
            for(int x=roi.X;x<roi.Right;x++)
            {
                byte m=part.MaskAt(x,y); holes[(y-roi.Y)*roi.Width+x-roi.X]=m;
                if(m>0)
                {
                    holeCount++;
                    if(contour.Count<4096 && (part.MaskAt(x-1,y)==0 || part.MaskAt(x+1,y)==0
                        || part.MaskAt(x,y-1)==0 || part.MaskAt(x,y+1)==0)) contour.Add(new{x,y});
                }
                else {var p=Document.Original.PixelAt(x,y);b+=p[0];g+=p[1];r+=p[2];a+=p[3];n++;}
            }
        }
        return Element(new {documentToken=session.DocumentToken,revision=Revision(),roi,holePixels=holeCount,
            contour,contourLimit=4096, surroundingOriginal=new{count=n,meanB=n==0?0:(double)b/n,meanG=n==0?0:(double)g/n,
                meanR=n==0?0:(double)r/n,meanAlpha=n==0?0:(double)a/n},
            mask=Image(LiveImages.Mask(roi,holes,args.GetProperty("maxEdge").GetInt32()),session.DocumentToken,Revision()),
            original=Image(LiveImages.Capture(Document,"Original",null,roi,args.GetProperty("maxEdge").GetInt32(),ct),session.DocumentToken,Revision()),
            composite=Image(LiveImages.Capture(Document,"Composite",null,roi,args.GetProperty("maxEdge").GetInt32(),ct),session.DocumentToken,Revision())});
    }

    private Guid FinishRepair(JsonElement op,Guid id,EditTransaction tx,LiveBudget budget,CancellationToken ct)
    {
        var repair=Document.GetLayer(id) as RepairLayer ?? throw new LiveException("invalid_target");
        var points=Points(op.GetProperty("points"));double radius=op.GetProperty("radius").GetDouble(),strength=op.GetProperty("strength").GetDouble();
        var batches=LiveLimits.Stroke(points,radius,Document.Dimensions,budget);ValidateGrowth(repair.Bounds,batches,radius,budget,4);
        var kernel=new RepairFinishingKernel(); var previous=points[0];bool smudge=op.GetProperty("type").GetString()=="smudge.stroke";
        var samples=batches.SelectMany(x=>x).ToList();
        if(samples[^1]!=points[^1])samples.Add(points[^1]);
        if(samples.Count>LiveLimits.Dabs)throw new LiveException("dab_limit");
        foreach(var sample in samples)
        {
            var read=MaskBrushKernel.DabBounds(sample,radius+2,Document.Dimensions)
                .Union(MaskBrushKernel.DabBounds(previous,radius+2,Document.Dimensions));
            LiveLimits.Surface(read);budget.Add(LiveLimits.Area(read)*40,LiveLimits.Area(read)*8);
            budget.ReserveHistory(128+LiveLimits.Area(read)*8);previous=sample;
        }
        previous=points[0];
        foreach(var sample in samples)
        {
            ct.ThrowIfCancellationRequested();
            var patch=smudge?kernel.Smudge(repair,previous,sample,radius,strength,Document.Dimensions)
                :kernel.Blur(repair,sample,radius,strength,Document.Dimensions);previous=sample;
            if(!patch.Region.IsEmpty && patch.HasChanges) tx.Apply(new RasterPatch(id,patch.Region,patch.StraightBgra.Span));
        }
        return id;
    }

    private Guid BakePatch(Guid id,EditTransaction tx,LiveBudget budget,CancellationToken ct)
    {
        var patch=Document.GetLayer(id) as PatchLayer ?? throw new LiveException("invalid_target");
        var roi=patch.Bounds;LiveLimits.Surface(roi);
        if(!DocumentRect.FromSize(Document.Dimensions).Contains(roi)) throw new LiveException("invalid_roi");
        budget.Add(LiveLimits.Area(roi)*4,LiveLimits.Area(roi)*24);budget.ReserveHistory(512+LiveLimits.Area(roi)*8);
        budget.Dirty(roi,Document.Layers.Count+1);
        var pixels=new byte[roi.Width*roi.Height*4];
        for(int y=roi.Y;y<roi.Bottom;y++) {ct.ThrowIfCancellationRequested();for(int x=roi.X;x<roi.Right;x++)
        {var p=patch.SampleAt(x,y);if(!p.IsEmpty)p.CopyTo(pixels.AsSpan(((y-roi.Y)*roi.Width+x-roi.X)*4,4));}}
        var repair=new RepairLayer(roi,pixels,patch.Name);int index=Document.Layers.ToList().FindIndex(l=>l.Id==id);
        tx.Apply(new DeleteLayer(id));tx.Apply(new AddLayer(repair,index));
        if(!patch.Visible)tx.Apply(new SetLayerVisibility(repair.Id,false));
        if(patch.SemanticName is {} semantic)tx.Apply(new SetLayerSemanticName(repair.Id,semantic));
        return repair.Id;
    }

    private static DocumentRect ReadRoi(JsonElement args)
    {var r=args.GetProperty("roi");return new(r.GetProperty("x").GetInt32(),r.GetProperty("y").GetInt32(),r.GetProperty("width").GetInt32(),r.GetProperty("height").GetInt32());}
    private static byte[] CompositePixels(CutworkDocument doc,DocumentRect roi,CancellationToken ct)
    {
        var pixels=new byte[roi.Width*roi.Height*4];
        for(int y=roi.Y;y<roi.Bottom;y++){ct.ThrowIfCancellationRequested();for(int x=roi.X;x<roi.Right;x++)
        {var p=pixels.AsSpan(((y-roi.Y)*roi.Width+x-roi.X)*4,4);CompositeCache.CompositePixel(doc,x,y,CompositeCache.HoleAt(doc,x,y),p);
            if(p[3]>0)for(int c=0;c<3;c++)p[c]=(byte)Math.Min(255,(p[c]*255+p[3]/2)/p[3]);}}
        return pixels;
    }
}
