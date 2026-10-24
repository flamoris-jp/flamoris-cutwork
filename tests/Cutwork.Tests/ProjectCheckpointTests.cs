using System.Security.Cryptography;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging.Persistence;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class ProjectCheckpointTests
{
    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath {get;}=Path.Combine(Path.GetTempPath(),"cutwork-revisions-"+Guid.NewGuid().ToString("N"));
        public string PathName=>Path.Combine(DirectoryPath,"project.flimg");
        public EditorSession Session {get;}=LiveMcpTests.Open();
        public ProjectWorkspace Workspace {get;}
        public ProjectCheckpoints Store=>Workspace.Checkpoints!;
        public Fixture(){Directory.CreateDirectory(DirectoryPath);Workspace=new(Session);Workspace.Save(PathName);Workspace.AuthorizeAiSave();}
        public string Hash()=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PathName))).ToLowerInvariant();
        public ProjectSaveRequest Request(bool save=false,string? parent=null,string? id=null,bool resume=false)=>new(Store.SaveTargetId,parent,Hash(),
            id??Guid.NewGuid().ToString("D"),"test",save,Session.DocumentToken,Session.Document!.Revision,Session.CurrentRevision,resume);
        public CheckpointReceipt Save(ProjectSaveRequest r,Func<bool>? auth=null,CancellationToken ct=default)
        {var snapshot=Workspace.BeginAiSave();CheckpointReceipt? receipt=null;try{return receipt=Store.Save(snapshot,r,auth??(()=>true),ct);}finally{Workspace.EndAiSave(receipt);}}
        public void Dispose(){Workspace.RevokeAiSave();Directory.Delete(DirectoryPath,true);}
    }
    private static void Code(string code,Action action)
    {try{action();Assert.Fail("Expected "+code);}catch(ProjectSaveException e){Assert.AreEqual(code,e.Code);}}

    [TestMethod]
    public void AuthorizedContextWorksBeforeRevisionDirectoryExists()
    {
        using var f=new Fixture();
        Assert.IsFalse(Directory.Exists(f.PathName+".revisions"));
        var value=System.Text.Json.JsonSerializer.SerializeToElement(f.Store.Context());
        Assert.AreEqual(System.Text.Json.JsonValueKind.Null,value.GetProperty("checkpointId").ValueKind);
        Assert.AreEqual(f.Store.SaveTargetId,value.GetProperty("saveTargetId").GetString());
        Assert.AreEqual(0,f.Store.List(0,20).Count);
    }

    [TestMethod]
    public void CheckpointIsFullReopenableArchiveAndDoesNotChangeDirtyOrHistory()
    {
        using var f=new Fixture();f.Session.Execute(new RenameLayer(f.Session.Document!.Base.Id,"unsaved"));
        var before=File.ReadAllBytes(f.PathName);var undo=f.Session.UndoCount;
        var r=f.Save(f.Request());
        Assert.IsTrue(r.CheckpointSaved);Assert.IsFalse(r.WorkingCopySaved);Assert.IsTrue(f.Session.IsDirty);
        Assert.AreEqual(undo,f.Session.UndoCount);CollectionAssert.AreEqual(before,File.ReadAllBytes(f.PathName));
        var loaded=new FlimgProjectStore().Load(Path.Combine(f.PathName+".revisions",r.CheckpointId+".flimg"));
        Assert.AreEqual("unsaved",loaded.Base.Name);f.Session.Undo();Assert.AreEqual(1,f.Store.List(0,20).Count);
    }

    [TestMethod]
    public void SaveMarksOnlyMatchingStateAndUiSaveUsesSameAuthorizedTarget()
    {
        using var f=new Fixture();f.Session.Execute(new RenameLayer(f.Session.Document!.Base.Id,"saved"));
        var request=f.Request(true);var receipt=f.Save(request);
        Assert.IsTrue(receipt.WorkingCopySaved);Assert.IsFalse(f.Session.IsDirty);
        Assert.AreEqual("saved",new FlimgProjectStore().Load(f.PathName).Base.Name);
        f.Session.Execute(new RenameLayer(f.Session.Document.Base.Id,"UI save"));f.Workspace.Save();
        Assert.IsFalse(f.Session.IsDirty);Assert.IsNotNull(f.Workspace.Checkpoints);
        Assert.AreEqual(1,f.Store.List(0,20).Count);
    }

    [TestMethod]
    public void DuplicateRequestAfterRestartReturnsSameCheckpointAndDifferentContentIsRejected()
    {
        using var f=new Fixture();var request=f.Request();var first=f.Save(request);
        var restarted=new ProjectCheckpoints(f.PathName);var retried=request with{SaveTargetId=restarted.SaveTargetId};
        var duplicate=restarted.Save(DocumentSnapshot.Capture(f.Session.Document!),retried,()=>true,default);
        Assert.AreEqual(first.CheckpointId,duplicate.CheckpointId);Assert.AreEqual(1,restarted.List(0,20).Count);
        var other=DocumentSnapshot.Capture(f.Session.Document!);var s=new EditorSession();s.Open(other);s.Execute(new RenameLayer(other.Base.Id,"different"));
        Code("request_id_conflict",()=>restarted.Save(other,retried,()=>true,default));
    }

    [TestMethod]
    public void StaleParentAndExternalWorkingFileChangesDoNotAddVersions()
    {
        using var f=new Fixture();var first=f.Save(f.Request());
        Code("checkpoint_conflict",()=>f.Save(f.Request()));Assert.AreEqual(1,f.Store.List(0,20).Count);
        var request=f.Request(parent:first.CheckpointId);
        File.AppendAllText(f.PathName,"external");
        Code("working_file_conflict",()=>f.Save(request));Assert.AreEqual(1,f.Store.List(0,20).Count);
        Assert.IsFalse(f.Workspace.IsSaving);
    }

    [TestMethod]
    public void FileReplacementAndSaveAsRevokeOldAuthority()
    {
        using var f=new Fixture();var request=f.Request();var old=f.Store;
        var second=Path.Combine(f.DirectoryPath,"other.flimg");f.Workspace.Save(second);
        Assert.IsNull(f.Workspace.Checkpoints);Code("save_target_expired",()=>old.Context());
        f.Workspace.AuthorizeAiSave();Assert.AreNotEqual(request.SaveTargetId,f.Store.SaveTargetId);
    }

    [TestMethod]
    public void CancellationAndRevocationBeforeCommitLeaveNoPublishedVersion()
    {
        using var f=new Fixture();using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>f.Save(f.Request(),ct:cancelled.Token));
        Assert.AreEqual(0,f.Store.List(0,20).Count);Assert.IsFalse(f.Workspace.IsSaving);
        Code("save_authorization_expired",()=>f.Save(f.Request(),()=>false));Assert.AreEqual(0,f.Store.List(0,20).Count);
    }

    [TestMethod]
    public void WorkingFileFailureKeepsCheckpointAndExplicitResumeDoesNotAddVersion()
    {
        using var f=new Fixture();f.Session.Execute(new RenameLayer(f.Session.Document!.Base.Id,"resume me"));var request=f.Request(true);
        CheckpointReceipt receipt;
        using(var locked=new FileStream(f.PathName,FileMode.Open,FileAccess.Read,FileShare.Read))receipt=f.Save(request);
        Assert.IsTrue(receipt.CheckpointSaved);Assert.IsFalse(receipt.WorkingCopySaved);Assert.IsTrue(f.Session.IsDirty);
        var resumed=f.Save(request with{Resume=true});Assert.IsTrue(resumed.WorkingCopySaved);Assert.IsFalse(f.Session.IsDirty);
        Assert.AreEqual(receipt.CheckpointId,resumed.CheckpointId);Assert.AreEqual(1,f.Store.List(0,20).Count);
    }

    [TestMethod]
    public void IndexPublicationFailureLeavesOrphanAndNextCheckpointSkipsItsNumber()
    {
        using var f=new Fixture();var first=f.Save(f.Request());var request=f.Request(parent:first.CheckpointId);
        var index=Path.Combine(f.PathName+".revisions","index.json");
        using(var locked=new FileStream(index,FileMode.Open,FileAccess.Read,FileShare.Read))
            Assert.ThrowsExactly<FlimgException>(()=>f.Save(request));
        Assert.AreEqual(1,f.Store.List(0,20).Count);
        var third=f.Save(request);Assert.AreEqual("r000000003",third.CheckpointId);Assert.AreEqual(2,f.Store.List(0,20).Count);
    }

    [TestMethod]
    public void SaveIsExclusiveAndSnapshotCannotMarkNewerHistorySaved()
    {
        using var f=new Fixture();var request=f.Request(true);var snapshot=f.Workspace.BeginAiSave();
        Code("save_busy",()=>f.Workspace.BeginAiSave());Code("save_busy",()=>f.Workspace.Save());
        var receipt=f.Store.Save(snapshot,request,()=>true,default);
        f.Session.Execute(new RenameLayer(f.Session.Document!.Base.Id,"newer"));f.Workspace.EndAiSave(receipt);
        Assert.IsTrue(f.Session.IsDirty);Assert.IsFalse(f.Workspace.IsSaving);
    }

    [TestMethod]
    public void CorruptHeadArchiveIsNotAdvertisedAsValid()
    {
        using var f=new Fixture();var receipt=f.Save(f.Request());
        File.AppendAllText(Path.Combine(f.PathName+".revisions",receipt.CheckpointId+".flimg"),"bad");
        Code("checkpoint_digest_mismatch",()=>f.Store.Context());
    }

    [TestMethod]
    public void MissingPermissionAndReplacedTargetAreRejected()
    {
        using var f=new Fixture();var request=f.Request();
        Code("save_target_expired",()=>f.Save(request with{SaveTargetId=Guid.NewGuid().ToString("N")}));
        var original=f.PathName+".old";File.Move(f.PathName,original);File.Copy(original,f.PathName);
        Code("save_target_changed",()=>f.Store.Context());
    }
}
