using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text.Json;
using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging.Persistence;

public sealed record CheckpointReceipt(string RequestId, string CheckpointId, string? ParentId,
    DateTimeOffset CreatedUtc, string Label, string Digest, string DocumentToken,
    long DocumentRevision, long HistoryStateRevision, bool SaveRequested,
    bool CheckpointSaved, bool WorkingCopySaved, string? Error);

public sealed record ProjectSaveRequest(string SaveTargetId, string? ExpectedCheckpointId,
    string ExpectedWorkingFileHash, string RequestId, string Label, bool SaveWorkingCopy,
    string DocumentToken, long DocumentRevision, long HistoryStateRevision, bool Resume = false);

public sealed class ProjectSaveException(string code) : Exception(code)
{ public string Code { get; } = code; }

/// <summary>Exact UI-authorized target; local full archives plus an atomic, bounded receipt index.</summary>
public sealed class ProjectCheckpoints
{
    public const int MaximumCheckpoints = 1000;
    public const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumSnapshotBytes = 256L * 1024 * 1024;
    private readonly string path, directory, versions;
    private string targetIdentity;
    private readonly Dictionary<string,string> directories;
    private string? versionsIdentity;
    private readonly FlimgProjectStore store;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string SaveTargetId { get; } = Guid.NewGuid().ToString("N");
    public string WorkingHash { get; private set; }
    private volatile bool revoked;
    public void Revoke() => revoked = true;

    public ProjectCheckpoints(string projectPath, FlimgProjectStore? store = null)
    {
        path=Path.GetFullPath(projectPath);directory=Path.GetDirectoryName(path)!;
        this.store=store??new FlimgProjectStore();
        RejectLinks(path);
        if(!File.Exists(path)) throw new ProjectSaveException("save_target_required");
        // Initial guarantee is local Windows volumes. Network volumes have different lock/rename semantics.
        if(path.StartsWith(@"\\",StringComparison.Ordinal)) throw new ProjectSaveException("unsupported_save_volume");
        versions=path+".revisions";
        RejectLinks(versions);
        directories=Ancestors(directory).ToDictionary(x=>x,Identity);
        if(Directory.Exists(versions))versionsIdentity=Identity(versions);
        targetIdentity=Identity(path);WorkingHash=Hash(path);
    }

    public object Context()
    {
        DemandTarget();
        var index=ReadIndex();
        return new {saveTargetId=SaveTargetId, expectedWorkingFileHash=WorkingHash,
            checkpointId=index.LastOrDefault()?.CheckpointId,checkpointCount=index.Count,
            maximumCheckpoints=MaximumCheckpoints,maximumBytes=MaximumBytes,
            storedBytes=StoredBytes(),canCreateCheckpoint=index.Count<MaximumCheckpoints && StoredBytes()<MaximumBytes};
    }

    public IReadOnlyList<CheckpointReceipt> List(int offset,int count)
    {DemandTarget();return ReadIndex().AsEnumerable().Reverse().Skip(offset).Take(Math.Clamp(count,1,20)).ToArray();}
    public CheckpointReceipt? Status(string requestId)
    {
        DemandTarget();var receipt=ReadIndex().SingleOrDefault(x=>x.RequestId==requestId);
        if(receipt is {SaveRequested:true,WorkingCopySaved:false} && Hash(path)==receipt.Digest)
            receipt=receipt with{WorkingCopySaved=true,Error=null};
        return receipt;
    }

    public CheckpointReceipt Save(CutworkDocument snapshot,ProjectSaveRequest request,
        Func<bool> stillAuthorized,CancellationToken ct)
    {
        Validate(request);DemandTarget();
        Directory.CreateDirectory(versions);RejectLinks(versions);versionsIdentity??=Identity(versions);
        // The same exclusive lock is used by ordinary UI saves once this target is authorized.
        using var gate=AcquireLock();
        DemandTarget();var index=ReadIndex();
        var old=index.SingleOrDefault(x=>x.RequestId==request.RequestId);
        if(old is not null)
        {
            if(old.DocumentToken!=request.DocumentToken || old.DocumentRevision!=request.DocumentRevision
                || old.HistoryStateRevision!=request.HistoryStateRevision || old.Label!=request.Label
                || old.SaveRequested!=request.SaveWorkingCopy || old.ParentId!=request.ExpectedCheckpointId)
                throw new ProjectSaveException("request_id_conflict");
            // Canonical archive bytes also reject a reused ID with different content.
            string retry=Path.Combine(versions,".retry-"+Guid.NewGuid().ToString("N")+".flimg");
            try
            {
                ct.ThrowIfCancellationRequested();DemandAuthorization(stillAuthorized);
                store.Save(snapshot,retry);
                if(Hash(retry)!=old.Digest)throw new ProjectSaveException("request_id_conflict");
            }
            finally{if(File.Exists(retry))File.Delete(retry);}
            if(old.SaveRequested && !old.WorkingCopySaved && Hash(path)==old.Digest)
            {
                old=old with{WorkingCopySaved=true,Error=null};index[index.FindIndex(x=>x.RequestId==old.RequestId)]=old;
                WorkingHash=old.Digest;WriteIndex(index);
            }
            if(request.Resume && old.SaveRequested && !old.WorkingCopySaved)
            {
                if(index[^1].CheckpointId!=old.CheckpointId || Hash(path)!=request.ExpectedWorkingFileHash
                    || request.ExpectedWorkingFileHash!=WorkingHash)throw new ProjectSaveException("working_file_conflict");
                DemandAuthorization(stillAuthorized);ct.ThrowIfCancellationRequested();DemandTarget();
                var archive=Path.Combine(versions,old.CheckpointId+".flimg");
                if(Hash(archive)!=old.Digest)throw new ProjectSaveException("digest_mismatch");
                new AtomicFileWriter(new LocalAtomicProjectFileSystem()).Write(path,output=>
                {using var input=File.OpenRead(archive);input.CopyTo(output);},p=>
                {if(Hash(p)!=old.Digest)throw new ProjectSaveException("digest_mismatch");});
                WorkingHash=old.Digest;targetIdentity=Identity(path);
                old=old with{WorkingCopySaved=true,Error=null};index[index.FindIndex(x=>x.RequestId==old.RequestId)]=old;WriteIndex(index);
            }
            return old;
        }
        if(index.LastOrDefault()?.CheckpointId!=request.ExpectedCheckpointId)throw new ProjectSaveException("checkpoint_conflict");
        if(Hash(path)!=request.ExpectedWorkingFileHash || request.ExpectedWorkingFileHash!=WorkingHash)
            throw new ProjectSaveException("working_file_conflict");
        if(index.Count>=MaximumCheckpoints)throw new ProjectSaveException("checkpoint_count_limit");
        string temp=Path.Combine(versions,".pending-"+Guid.NewGuid().ToString("N")+".flimg");
        string? final=null;
        try
        {
            ct.ThrowIfCancellationRequested();DemandAuthorization(stillAuthorized);
            store.Save(snapshot,temp); // closed, flushed and decoded by the existing store
            string digest=Hash(temp);long length=new FileInfo(temp).Length;
            if(StoredBytes()+length>MaximumBytes) throw new ProjectSaveException("checkpoint_capacity");
            // Include orphan files when allocating; a failed index publication never reuses a number.
            long next=Directory.EnumerateFiles(versions,"r*.flimg").Select(p=>
                long.TryParse(Path.GetFileNameWithoutExtension(p).AsSpan(1),out long n)?n:0).DefaultIfEmpty().Max()+1;
            string id="r"+next.ToString("D9",System.Globalization.CultureInfo.InvariantCulture);
            final=Path.Combine(versions,id+".flimg");
            ct.ThrowIfCancellationRequested();DemandTarget();DemandAuthorization(stillAuthorized);
            if(Hash(path)!=request.ExpectedWorkingFileHash)throw new ProjectSaveException("working_file_conflict");
            File.Move(temp,final); // create-new; existing archives are immutable
            var receipt=new CheckpointReceipt(request.RequestId,id,request.ExpectedCheckpointId,DateTimeOffset.UtcNow,
                request.Label,digest,request.DocumentToken,request.DocumentRevision,request.HistoryStateRevision,
                request.SaveWorkingCopy,true,false,null);
            index.Add(receipt);
            WriteIndex(index); // commit point; cancellation beyond here returns a receipt
            if(!request.SaveWorkingCopy)return receipt;
            try
            {
                DemandAuthorization(stillAuthorized);DemandTarget();
                if(Hash(path)!=request.ExpectedWorkingFileHash)throw new ProjectSaveException("working_file_conflict");
                // Copy the verified archive, not a second rendering/serialization.
                new AtomicFileWriter(new LocalAtomicProjectFileSystem()).Write(path,output=>
                {using var input=File.OpenRead(final);input.CopyTo(output);},p=>
                {if(Hash(p)!=digest)throw new ProjectSaveException("digest_mismatch");});
                WorkingHash=digest;targetIdentity=Identity(path);
                receipt=receipt with {WorkingCopySaved=true};
                index[^1]=receipt;WriteIndex(index);
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or FlimgException or ProjectSaveException)
            {
                // The committed archive/receipt remains available even if working-copy replacement fails.
                receipt=receipt with {Error=e is ProjectSaveException se?se.Code:"working_copy_io_failure"};
                index[^1]=receipt;
                try{WriteIndex(index);}catch(Exception x)when(x is IOException or UnauthorizedAccessException or FlimgException){}
            }
            return receipt;
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }

    public void SaveFromUi(CutworkDocument document)
    {
        DemandTarget();Directory.CreateDirectory(versions);versionsIdentity??=Identity(versions);using var gate=AcquireLock();DemandTarget();
        if(Hash(path)!=WorkingHash)throw new ProjectSaveException("working_file_conflict");
        store.Save(document,path);WorkingHash=Hash(path);targetIdentity=Identity(path);
    }

    private void Validate(ProjectSaveRequest r)
    {
        if(r.SaveTargetId!=SaveTargetId || revoked)throw new ProjectSaveException("save_target_expired");
        if(!Guid.TryParseExact(r.RequestId,"D",out var id)||id==Guid.Empty||r.Label.Length>128)
            throw new ProjectSaveException("invalid_save_request");
    }
    private void DemandTarget()
    {
        if(revoked)throw new ProjectSaveException("save_target_expired");
        RejectLinks(path);RejectLinks(versions);
        foreach(var pair in directories)if(!Directory.Exists(pair.Key)||Identity(pair.Key)!=pair.Value)
            throw new ProjectSaveException("save_target_changed");
        if(versionsIdentity is not null && (!Directory.Exists(versions)||Identity(versions)!=versionsIdentity))
            throw new ProjectSaveException("save_target_changed");
        if(!File.Exists(path)||Identity(path)!=targetIdentity)throw new ProjectSaveException("save_target_changed");
    }
    private void DemandAuthorization(Func<bool> check)
    {if(revoked||!check())throw new ProjectSaveException("save_authorization_expired");}
    private FileStream AcquireLock()
    {
        RejectLinks(Path.Combine(versions,"store.lock"));
        try{return new FileStream(Path.Combine(versions,"store.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){throw new ProjectSaveException("save_busy");}
    }
    private List<CheckpointReceipt> ReadIndex()
    {
        var file=Path.Combine(versions,"index.json");RejectLinks(file);
        if(!File.Exists(file))return [];
        if(new FileInfo(file).Length>4*1024*1024)throw new ProjectSaveException("checkpoint_index_limit");
        var items=JsonSerializer.Deserialize<List<CheckpointReceipt>>(File.ReadAllText(file),Json)
            ?? throw new ProjectSaveException("checkpoint_index_invalid");
        if(items.Count>MaximumCheckpoints || items.Select(x=>x.RequestId).Distinct().Count()!=items.Count
            || items.Select(x=>x.CheckpointId).Distinct().Count()!=items.Count)
            throw new ProjectSaveException("checkpoint_index_invalid");
        foreach(var item in items)
        {
            if(item.CheckpointId.Length!=10 || item.CheckpointId[0]!='r' || !item.CheckpointId.AsSpan(1).ToString().All(char.IsAsciiDigit))
                throw new ProjectSaveException("checkpoint_index_invalid");
            string archive=Path.Combine(versions,item.CheckpointId+".flimg");RejectLinks(archive);
            if(!File.Exists(archive))throw new ProjectSaveException("checkpoint_missing");
        }
        if(items.LastOrDefault() is {} head && Hash(Path.Combine(versions,head.CheckpointId+".flimg"))!=head.Digest)
            throw new ProjectSaveException("checkpoint_digest_mismatch");
        return items;
    }
    private void WriteIndex(List<CheckpointReceipt> items)
    {
        RejectLinks(Path.Combine(versions,"index.json"));
        new AtomicFileWriter(new LocalAtomicProjectFileSystem()).Write(Path.Combine(versions,"index.json"),s=>
            JsonSerializer.Serialize(s,items,Json),p=>
            {using var input=File.OpenRead(p);_ = JsonSerializer.Deserialize<List<CheckpointReceipt>>(input,Json)??throw new ProjectSaveException("checkpoint_index_invalid");});
    }
    private long StoredBytes()=>Directory.Exists(versions)?Directory.EnumerateFiles(versions,"*.flimg").Sum(p=>new FileInfo(p).Length):0;
    private static string Hash(string file)
    {RejectLinks(file);using var s=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();}
    // Creation identity is preserved by File.Replace on supported local Windows volumes.
    private static string Identity(string file)
    {
        if(!OperatingSystem.IsWindows())return File.GetCreationTimeUtc(file).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var handle=CreateFile(file,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
        if(handle.IsInvalid || !GetFileInformationByHandle(handle,out var info))
            throw new ProjectSaveException("save_identity_unavailable");
        return $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}";
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;
        public uint VolumeSerialNumber,SizeHigh,SizeLow,Links,FileIndexHigh,FileIndexLow;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInformation information);
    private static IEnumerable<string> Ancestors(string path)
    {for(var current=new DirectoryInfo(path);current is not null;current=current.Parent)yield return current.FullName;}
    private static void RejectLinks(string path)
    {
        var full=Path.GetFullPath(path);
        foreach(var ancestor in Ancestors(Path.GetDirectoryName(full)!))
            if((File.GetAttributes(ancestor)&FileAttributes.ReparsePoint)!=0)throw new ProjectSaveException("save_target_link");
        if((File.Exists(full)||Directory.Exists(full)) && (File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new ProjectSaveException("save_target_link");
        // A dangling link also has a LinkTarget even though Exists is false.
        if(new FileInfo(full).LinkTarget is not null)throw new ProjectSaveException("save_target_link");
    }
}
