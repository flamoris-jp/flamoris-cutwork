using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class WorkLogTests
{
    private static EditorSession Open()
    {var s=new EditorSession();s.Open(new(new("test",new(8,8),32,new byte[256])));return s;}
    private static ArtworkWorkLogEntry Append(EditorSession s,Guid? id=null,string decision="肌を先に補う",string observed="口は良好、髪の継ぎ目は未完成")=>
        s.AppendWorkLog(id??Guid.NewGuid(),decision,observed,DateTimeOffset.UtcNow);

    [TestMethod]
    public void AppendMarksDirtyAdvancesRevisionWithoutAddingUndoAndSurvivesUndoRedo()
    {
        var s=Open();s.Execute(new RenameLayer(s.Document!.Base.Id,"art edit"));
        int undo=s.UndoCount;long revision=s.Document.Revision;
        var e=Append(s);Assert.AreEqual(undo,s.UndoCount);Assert.IsTrue(s.IsDirty);Assert.IsTrue(s.Document.Revision>revision);
        Assert.AreEqual(1,e.Sequence);Assert.AreEqual(revision,e.DocumentRevision);
        s.Undo();Assert.AreEqual(1,s.Document.WorkLog.Count);Assert.IsTrue(s.IsDirty);
        s.Redo();s.MarkSaved();Assert.IsFalse(s.IsDirty);
        Append(s);Assert.IsTrue(s.IsDirty);s.Undo();Assert.AreEqual(2,s.Document.WorkLog.Count);Assert.IsTrue(s.IsDirty);
    }

    [TestMethod]
    public void SameRequestIsIdempotentAndConflictingTextIsRejected()
    {
        var s=Open();var id=Guid.NewGuid();var first=Append(s,id);long revision=s.Document!.Revision;
        var repeated=Append(s,id);Assert.AreEqual(first,repeated);Assert.AreEqual(revision,s.Document.Revision);
        Assert.ThrowsExactly<WorkLogException>(()=>Append(s,id,"different"));Assert.AreEqual(1,s.Document.WorkLog.Count);
    }

    [TestMethod]
    public void InvalidTextOversizeAndActiveGestureCannotAppend()
    {
        var s=Open();Assert.ThrowsExactly<WorkLogException>(()=>Append(s,decision:"",observed:" "));
        Assert.ThrowsExactly<WorkLogException>(()=>Append(s,decision:"\uD800"));
        Assert.ThrowsExactly<WorkLogException>(()=>Append(s,decision:new string('x',ArtworkWorkLog.MaximumEntryBytes)));
        using(var tx=s.BeginTransaction())Assert.ThrowsExactly<EditException>(()=>Append(s));
        Assert.AreEqual(0,s.Document!.WorkLog.Count);
    }

    [TestMethod]
    public void SnapshotAndRestorePreserveImmutableLogWithNoSharedAppendList()
    {
        var s=Open();var e=Append(s);var snapshot=DocumentSnapshot.Capture(s.Document!);Append(s);
        Assert.AreEqual(1,snapshot.WorkLog.Count);Assert.AreEqual(e,snapshot.WorkLog[0]);
        var loaded=new EditorSession();loaded.Open(snapshot);Assert.IsFalse(loaded.IsDirty);
        Append(loaded);Assert.AreEqual(2,loaded.Document!.WorkLog.Count);Assert.AreEqual(2,s.Document!.WorkLog.Count);
        Assert.IsTrue(DocumentSnapshot.EstimatedBytes(snapshot)>=snapshot.WorkLogBytes);
    }

    [TestMethod]
    public void RestoreRejectsSequenceDuplicatesAndEntryQuota()
    {
        var s=Open();var e=Append(s);
        Assert.ThrowsExactly<WorkLogException>(()=>ArtworkWorkLog.Validate([e,e]));
        Assert.ThrowsExactly<WorkLogException>(()=>ArtworkWorkLog.Validate([e with{Sequence=2}]));
        Assert.ThrowsExactly<WorkLogException>(()=>ArtworkWorkLog.Validate([e with{RecordedUtc=default}]));
        Assert.ThrowsExactly<WorkLogException>(()=>ArtworkWorkLog.Validate(Enumerable.Repeat(e,ArtworkWorkLog.MaximumEntries+1).ToArray()));
    }
}
