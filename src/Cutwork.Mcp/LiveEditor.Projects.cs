using System.Text.Json;
using Flamoris.Cutwork.Imaging.Persistence;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Mcp;

public sealed partial class LiveEditor
{
    private sealed record SaveJob(string RequestId, string JobId, string DocumentToken, long Revision,
        string State, CheckpointReceipt? Receipt=null,string? Error=null);
    private SaveJob? saveJob;
    private Task? saveWork;
    private CancellationTokenSource? saveCancellation;
    private JsonElement ProjectQuery(string name,JsonElement args,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(name=="project_save_cancel")
        {
            if(permission!=McpPermission.Edit)throw new LiveException(McpErrors.Forbidden);
            if(saveJob?.RequestId!=args.GetProperty("requestId").GetString())throw new ProjectSaveException("save_job_unknown");
            saveCancellation?.Cancel();return Element(new{cancellationRequested=true});
        }
        if(name=="project_context")
            return Element(new {documentToken=session.DocumentToken,revision=Revision(),
                saveAuthorized=workspace?.Checkpoints is not null,busy=workspace?.IsSaving==true,
                target=workspace?.Checkpoints?.Context()});
        var checkpoints=workspace?.Checkpoints ?? throw new ProjectSaveException("save_not_authorized");
        if(name=="project_checkpoints_list")return Element(new{checkpoints=checkpoints.List(args.GetProperty("offset").GetInt32(),args.GetProperty("count").GetInt32())});
        string id=args.GetProperty("requestId").GetString()!;
        if(saveJob is {} job && job.RequestId==id && job.DocumentToken==session.DocumentToken)return Element(job);
        return Element(new {state="stored",receipt=checkpoints.Status(id)});
    }

    private async Task<JsonElement> StartProjectSaveAsync(RequestContext context,string name,JsonElement args,CancellationToken ct)
    {
        SaveJob job=null!;ProjectCheckpoints checkpoints=null!;ProjectSaveRequest request=null!;
        Core.CutworkDocument snapshot=null!;CapabilityGrant grant=null!;
        await context.CommitAsync(()=>
        {
            DemandIdle(ct);
            if(workspace is null || dispatchCompletion is null || workspace.Checkpoints is null)
                throw new ProjectSaveException("save_not_authorized");
            grant=currentGrant?.Invoke() ?? throw new LiveException(McpErrors.Unauthorized);
            if(!grant.IsActive)throw new LiveException(McpErrors.Unauthorized);
            checkpoints=workspace.Checkpoints;
            var parent=args.GetProperty("expectedCheckpointId");
            request=new(args.GetProperty("saveTargetId").GetString()!,parent.ValueKind==JsonValueKind.Null?null:parent.GetString(),
                args.GetProperty("expectedWorkingFileHash").GetString()!,args.GetProperty("requestId").GetString()!,
                args.GetProperty("label").GetString()!,name=="project_save",session.DocumentToken,Document.Revision,session.CurrentRevision,args.TryGetProperty("resume",out var resume)&&resume.GetBoolean());
            if(request.SaveTargetId!=checkpoints.SaveTargetId)throw new ProjectSaveException("save_target_expired");
            if(saveJob is {} current && current.RequestId==request.RequestId && current.State=="running")
                return Element(current);
            snapshot=workspace.BeginAiSave();
            saveCancellation?.Dispose();saveCancellation=new CancellationTokenSource(TimeSpan.FromMinutes(5));
            job=new(request.RequestId,Guid.NewGuid().ToString("N"),session.DocumentToken,Document.Revision,"running");
            saveJob=job;editing(true);return Element(job);
        });
        // Request cancellation/disconnect is honored before commit. A committed receipt remains queryable.
        saveWork=Task.Run(async()=>
        {
            CheckpointReceipt? receipt=null;string? error=null;
            try
            {
                receipt=checkpoints.Save(snapshot,request,()=>grant.IsActive,saveCancellation!.Token);
            }
            catch(OperationCanceledException){error="save_cancelled";}
            catch(ProjectSaveException e){error=e.Code;}
            catch(Exception e)when(e is System.IO.IOException or UnauthorizedAccessException or FlimgException or JsonException)
            {error="save_io_failure";}
            catch(Exception){error="save_internal_failure";}
            finally
            {
                await dispatchCompletion!(()=>
                {
                    try{workspace!.EndAiSave(receipt);saveJob=job with {State=error is null?"completed":"failed",Receipt=receipt,Error=error};}
                    finally{editing(false);}
                });
            }
        });
        return Element(job);
    }
}
