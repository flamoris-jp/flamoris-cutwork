using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging.Persistence;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class FlimgWorkLogTests
{
    private static EditorSession Logged()
    {
        var s=LiveMcpTests.Open();s.AppendWorkLog(Guid.NewGuid(),"肌→頬→髪の順で補修する。","口は良好。目と眉の継ぎ目は未完成。\n再生確認はまだ。",DateTimeOffset.UtcNow);
        return s;
    }
    [TestMethod]
    public void V3ContainsReadableAppendOnlyTextAndRoundTripsWithoutPixelChanges()
    {
        var s=Logged();var original=s.Document!.Original.CopyPixelBytes();var bytes=FlimgRoundTripTests.Write(s.Document);
        StringAssert.Contains(FlimgRoundTripTests.ReadManifest(bytes),"\"schemaVersion\": 3");
        using(var stream=new MemoryStream(bytes))using(var zip=new ZipArchive(stream))
        using(var reader=new StreamReader(zip.GetEntry("work-log.jsonl")!.Open(),Encoding.UTF8))
        {string text=reader.ReadToEnd();StringAssert.Contains(text,"肌→頬→髪");Assert.IsTrue(text.EndsWith('\n'));}
        var restored=FlimgRoundTripTests.Read(bytes);Assert.AreEqual(s.Document.WorkLog.Single(),restored.WorkLog.Single());
        CollectionAssert.AreEqual(original,restored.Original.CopyPixelBytes());
        var loaded=new EditorSession();loaded.Open(restored);Assert.IsFalse(loaded.IsDirty);
        loaded.AppendWorkLog(Guid.NewGuid(),"訂正を追記","髪の境界を再確認した。",DateTimeOffset.UtcNow);
        var twice=FlimgRoundTripTests.Read(FlimgRoundTripTests.Write(restored));Assert.AreEqual(2,twice.WorkLog.Count);
        Assert.AreEqual(1,twice.WorkLog[0].Sequence);Assert.AreEqual(2,twice.WorkLog[1].Sequence);
    }
    [TestMethod]
    public void V2WithoutLogStillLoadsAndResavesAsV2()
    {
        var old=FlimgRoundTripTests.FullDocument();var bytes=FlimgRoundTripTests.Write(old);
        StringAssert.Contains(FlimgRoundTripTests.ReadManifest(bytes),"\"schemaVersion\": 2");
        var loaded=FlimgRoundTripTests.Read(bytes);Assert.AreEqual(0,loaded.WorkLog.Count);
        CollectionAssert.AreEqual(bytes,FlimgRoundTripTests.Write(loaded));
    }
    [TestMethod]
    public void SaveCheckpointAndReopenPreserveWorkLogAndDirtyState()
    {
        string folder=Path.Combine(Path.GetTempPath(),"cutwork-log-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var s=Logged();var w=new ProjectWorkspace(s);string path=Path.Combine(folder,"work.flimg");w.Save(path);
            Assert.IsFalse(s.IsDirty);w.AuthorizeAiSave();s.AppendWorkLog(Guid.NewGuid(),"次の補修","まぶたの取り残しを確認。",DateTimeOffset.UtcNow);
            var target=w.Checkpoints!;var c=System.Text.Json.JsonSerializer.SerializeToElement(target.Context());
            var request=new ProjectSaveRequest(target.SaveTargetId,null,c.GetProperty("expectedWorkingFileHash").GetString()!,Guid.NewGuid().ToString("D"),"log",true,
                s.DocumentToken,s.Document!.Revision,s.CurrentRevision);
            var snapshot=w.BeginAiSave();var receipt=target.Save(snapshot,request,()=>true,default);w.EndAiSave(receipt);
            Assert.IsTrue(receipt.WorkingCopySaved);Assert.IsFalse(s.IsDirty);
            var stored=new FlimgProjectStore().Load(path);Assert.AreEqual(2,stored.WorkLog.Count);
            var checkpoint=new FlimgProjectStore().Load(Path.Combine(path+".revisions",receipt.CheckpointId+".flimg"));
            CollectionAssert.AreEqual(stored.WorkLog.ToArray(),checkpoint.WorkLog.ToArray());
        }
        finally{Directory.Delete(folder,true);}
    }
    [TestMethod]
    public void TamperedTextChecksumCountSequenceUtf8AndV2DowngradeAreRejected()
    {
        var source=FlimgRoundTripTests.Write(Logged().Document!);
        Assert.ThrowsExactly<FlimgException>(()=>FlimgRoundTripTests.Read(Mutate(source,(m,b)=>(m,Encoding.UTF8.GetBytes("tampered")),false)));
        foreach(var kind in new[]{"count","sequence","utf8","downgrade","duplicate","unknown"})
        {
            var invalid=Mutate(source,(m,b)=>
            {
                if(kind=="count")m["workLog"]!["count"]=2;
                if(kind=="downgrade")m["schemaVersion"]=2;
                if(kind=="sequence"||kind=="unknown")
                {var row=JsonNode.Parse(Encoding.UTF8.GetString(b).TrimEnd('\n'))!;row[kind=="sequence"?"sequence":"unknown"]=9;b=Encoding.UTF8.GetBytes(row.ToJsonString()+"\n");}
                if(kind=="utf8")b=[255,10];
                if(kind=="duplicate")b=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("\"sequence\":1","\"sequence\":1,\"sequence\":1"));
                return(m,b);
            },true);
            Assert.ThrowsExactly<FlimgException>(()=>FlimgRoundTripTests.Read(invalid),kind);
        }
    }
    private static byte[] Mutate(byte[] bytes,Func<JsonNode,byte[],(JsonNode,byte[])> change,bool updateHash)
    {
        using var input=new MemoryStream(bytes);using var zip=new ZipArchive(input);var entries=new Dictionary<string,byte[]>();
        foreach(var e in zip.Entries){using var s=e.Open();using var o=new MemoryStream();s.CopyTo(o);entries[e.FullName]=o.ToArray();}
        var (manifest,text)=change(JsonNode.Parse(entries["manifest.json"])!,entries["work-log.jsonl"]);
        entries["work-log.jsonl"]=text;if(updateHash)manifest["workLog"]!["sha256"]=Convert.ToHexString(SHA256.HashData(text)).ToLowerInvariant();
        entries["manifest.json"]=Encoding.UTF8.GetBytes(manifest.ToJsonString());
        using var output=new MemoryStream();using(var result=new ZipArchive(output,ZipArchiveMode.Create,true))
            foreach(var p in entries){using var s=result.CreateEntry(p.Key).Open();s.Write(p.Value);}
        return output.ToArray();
    }
}
