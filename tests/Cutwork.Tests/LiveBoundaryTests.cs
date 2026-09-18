using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Mcp;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LiveBoundaryTests
{
    [TestMethod]
    public async Task NativePipeUsesProtectedOwnerOnlyAclAndLocalClient()
    {
        var type=typeof(Flamoris.Cutwork.App.MainWindow).Assembly.GetType("Flamoris.Cutwork.App.WindowsLocalPipe")!;
        Assert.AreEqual(8U,(uint)type.GetField("RejectRemoteClients",BindingFlags.Static|BindingFlags.NonPublic)!.GetRawConstantValue()!);
        string name="cutwork-"+Guid.NewGuid().ToString("N");
        using var server=(NamedPipeServerStream)type.GetMethod("Create")!.Invoke(null,[name])!;
        using var identity=WindowsIdentity.GetCurrent();
        uint code=GetSecurityInfo(server.SafePipeHandle.DangerousGetHandle(),6,1|4,out _,out _,out _,out _,out var descriptor);
        Assert.AreEqual(0U,code);
        try
        {
            int size=(int)GetSecurityDescriptorLength(descriptor);var bytes=new byte[size];Marshal.Copy(descriptor,bytes,0,size);
            var security=new RawSecurityDescriptor(bytes,0);Assert.AreEqual(identity.Owner,security.Owner);
            Assert.IsTrue(security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));Assert.AreEqual(1,security.DiscretionaryAcl!.Count);
            Assert.AreEqual(identity.Owner,((CommonAce)security.DiscretionaryAcl[0]).SecurityIdentifier);
        }
        finally{LocalFree(descriptor);}
        using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var wait=server.WaitForConnectionAsync();await client.ConnectAsync(2000);await wait;
        Assert.IsTrue(server.IsConnected);
    }
    [TestMethod]
    public async Task CancellationBeforeSealRestoresPixelsAndNoOpDoesNotCreateHistory()
    {
        var s=LiveMcpTests.Open();using var lease=new LiveAccess(s,LivePermission.Edit);using var ct=new CancellationTokenSource();int barrier=0;
        var editor=new LiveEditor(s,lease,()=>false,()=>{if(++barrier==2)ct.Cancel();return Task.CompletedTask;},_=>{});
        var r=await editor.CallAsync("edit",JsonSerializer.SerializeToElement(new{documentToken=s.DocumentToken,expectedRevision="0",operations=new[]{new{type="layer.rename",target=s.Document!.Base.Id.ToString(),name="cancel"}}}),ct.Token);
        Assert.IsTrue(r.IsError);Assert.AreEqual("",s.Document!.Base.Name);Assert.AreEqual(0,s.UndoCount);
        var fresh=new LiveEditor(s,lease,()=>false,()=>Task.CompletedTask,_=>{});
        var noop=await fresh.CallAsync("edit",JsonSerializer.SerializeToElement(new{documentToken=s.DocumentToken,expectedRevision=s.Document.Revision.ToString(),operations=new[]{new{type="layer.rename",target=s.Document.Base.Id.ToString(),name=""}}}));
        Assert.IsFalse(noop.IsError==true);Assert.AreEqual(0,s.UndoCount);
    }
    [TestMethod]
    public async Task BlockedReadAndEofCancelConnectionWithoutUnboundedWait()
    {
        using var revoked=new CancellationTokenSource();using var blocked=new WaitingStream();using var stream=new BoundedProtocolStream(blocked,revoked.Token);
        var read=stream.ReadAsync(new byte[100]).AsTask();await blocked.Entered.Task;revoked.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async()=>await read);
        using var empty=new BoundedProtocolStream(new MemoryStream(),default);
        Assert.AreEqual(0,await empty.ReadAsync(new byte[1]));Assert.IsTrue(empty.Closed.IsCancellationRequested);
    }
    [TestMethod]
    public async Task PipeliningAndDeepFramesCloseOnlyConnection()
    {
        string input=string.Join("\n",Enumerable.Range(0,9).Select(i=>$"{{\"jsonrpc\":\"2.0\",\"id\":{i},\"method\":\"ping\"}}"))+"\n";
        using var stream=new BoundedProtocolStream(new MemoryStream(Encoding.UTF8.GetBytes(input)),default);
        var buffer=new byte[4096];for(int i=0;i<8;i++)Assert.IsTrue(await stream.ReadAsync(buffer)>0);
        await Assert.ThrowsExactlyAsync<IOException>(async()=>{ _ = await stream.ReadAsync(buffer); });
        Assert.IsTrue(stream.Closed.IsCancellationRequested);
        string deep="{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"params\":{\"deep\":"+new string('[',65)+"0"+new string(']',65)+"}}\n";
        using var nested=new BoundedProtocolStream(new MemoryStream(Encoding.UTF8.GetBytes(deep)),default);
        await Assert.ThrowsAsync<JsonException>(async()=>{ _ = await nested.ReadAsync(buffer); });
    }
    private sealed class WaitingStream:Stream
    {
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){Entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);return 0;}
        public override bool CanRead=>true;public override bool CanWrite=>false;public override bool CanSeek=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
        public override void Flush(){}public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();
    }
    [DllImport("advapi32.dll")]private static extern uint GetSecurityInfo(IntPtr handle,int type,uint info,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
}
