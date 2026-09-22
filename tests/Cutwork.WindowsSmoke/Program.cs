using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flamoris.Cutwork.Imaging.Persistence;
using Flamoris.Logging;
using Flamoris.Mcp.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class Program
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static readonly string CleanPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
    [STAThread]
    private static int Main(string[] args)
    {
        try { Run(args.Single()).GetAwaiter().GetResult(); return 0; }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static async Task Run(string source)
    {
        string temp = Path.Combine(Path.GetTempPath(), "cutwork-live-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        string package = Path.Combine(temp, "package"); CopyDirectory(source, package);
        string image = Path.Combine(temp,"synthetic.png"), project = Path.Combine(temp,"edited.flimg");
        var pixels = Enumerable.Range(0,32*32).SelectMany(i=>new byte[]{(byte)(i%32*7),(byte)(i/32*7),100,255}).ToArray();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32,32,96,96,PixelFormats.Bgra32,null,pixels,128)));
        using(var file=File.Create(image)) encoder.Save(file);
        string editor=Path.Combine(package,"Cutwork.exe"), bridge=Path.Combine(package,"mcp","Flamoris.Mcp.Bridge.exe");
        Check(File.Exists(editor)&&File.Exists(bridge),"Self-contained executables missing.");
        Check(!Directory.GetFiles(package,"*",SearchOption.AllDirectories).Any(p=>Path.GetFileName(p).Contains("Tests",StringComparison.Ordinal)||Path.GetFileName(p).Contains("WindowsSmoke",StringComparison.Ordinal)),"Test dependency in package.");
        await PackagedBridgeIdle(bridge);
        var start=new ProcessStartInfo(editor){UseShellExecute=false,WorkingDirectory=temp}; start.Environment["PATH"]=CleanPath;
        using var process=Process.Start(start)!;
        try
        {
            AutomationElement? main=null;
            await Until(()=>{process.Refresh();if(process.HasExited)throw new Exception("Editor exited.");if(process.MainWindowHandle==0)return false;main=AutomationElement.FromHandle(process.MainWindowHandle);return main is not null;});
            var window=main!;
            string? durableLayers=null; byte[]? durableComposite=null; byte[]? durableMask=null; Guid durablePart=default;
            await FileMenu(window,process.Id,"OpenMenuItem",image);
            Console.WriteLine("Opened synthetic artwork through WPF.");
            await Menu(window,"ViewMenu","ActualSizeMenuItem");
            await Menu(window,"McpMenu","McpReadMenu"); var readConnection=await Connection(process.Id);
            await using(var read=new ClientHandle(await Connect(bridge,readConnection)))
            {
                Check(read.Client.NegotiatedProtocolVersion=="2026-07-28","Protocol mismatch.");
                var tools=await read.Client.ListToolsAsync(cancellationToken:Deadline());Check(!tools.Any(t=>t.Name is "edit" or "undo"),"Read-only discovery exposed edits.");
                var context=await Context(read.Client);Check(context.GetProperty("width").GetInt32()==32,"Wrong live document.");
                var preview=await Query(read.Client,"image",new {source="Original",target=(string?)null,roi=(object?)null,maxEdge=32});
                Check(Png(preview).AsSpan().StartsWith(new byte[]{137,80,78,71}),"No PNG result.");
                var denied=await Edit(read.Client,context,new{type="layer.rename",target=Guid.NewGuid().ToString(),name="forbidden"});Check(denied.IsError==true,"Read-only direct edit accepted.");
            }
            Console.WriteLine("Read-only official client image and direct denial: PASS");
            await Menu(window,"McpMenu","McpEditMenu"); await OldPipeRejected(readConnection.Pipe); var connection=await Connection(process.Id);
            await using(var handle=new ClientHandle(await Connect(bridge,connection)))
            {
                var client=handle.Client; var before=await Context(client);
                uint beforeScreen=ScreenPixel(window,13,10);
                object[] fence=[new{x=4,y=4},new{x=20,y=4},new{x=20,y=20},new{x=4,y=20}];
                var preview=await Query(client,"part_preview",new {fence,step=0,maxEdge=32});
                Check(preview.IsError!=true&&Png(preview).Length>0,"Fitting preview failed.");Check((await Context(client)).GetProperty("revision").GetString()==before.GetProperty("revision").GetString(),"Preview mutated document.");
                var edit=await Edit(client,before,new{type="part.create",fence,step=0,name="MCP Face"},
                    new{type="mask.stroke",target="@0",points=new[]{new{x=8.5,y=8.5},new{x=9.5,y=8.5}},radius=1,polarity="Erase"},
                    new{type="clone.stroke",target=(string?)null,ownerPart="@0",global=false,source=new{x=4.5,y=4.5},points=new[]{new{x=10.5,y=10.5},new{x=15.5,y=10.5}},radius=2,mode="Fixed"},
                    new{type="patch.create",fence,transform=new{centerX=12.5,centerY=12.5,scale=1,rotationDegrees=0},name="MCP Patch"}, new{type="layer.visible",target="@0",visible=false});
                Check(edit.IsError!=true,Text(edit).ToString());
                var layers=await Layers(client);Check(layers.GetArrayLength()==4,"Part/mask/repair/patch batch failed.");
                await Until(()=>VisibleValue(window,"MCP Face")&&VisibleValue(window,"MCP Patch"));
                Console.WriteLine("Part/Mask/Clone/Patch and automatic WPF projection: PASS");
                var repaired=await Composite(client);
                await Until(()=>ScreenPixel(window,13,10)!=beforeScreen);uint repairedScreen=ScreenPixel(window,13,10);
                await ClickReady(window,"UndoToolbarButton");Check((await Layers(client)).GetArrayLength()==1,"WPF Undo failed.");
                await Until(()=>ScreenPixel(window,13,10)==beforeScreen);
                await ClickReady(window,"RedoToolbarButton");Check((await Layers(client)).GetArrayLength()==4,"WPF Redo failed.");
                await Until(()=>ScreenPixel(window,13,10)==repairedScreen);Console.WriteLine("Published canvas pixels update and restore through WPF Undo/Redo: PASS");
                Check(Enumerable.SequenceEqual(repaired, await Composite(client)),"WPF history did not restore pixels.");
                var stale=await Edit(client,before,new{type="layer.rename",target=layers[0].GetProperty("id").GetString(),name="stale"});Check(stale.IsError==true,"Stale write accepted.");
                var context=await Context(client);var failed=await Edit(client,context,new{type="part.create",fence,step=1,name="Rollback"},new{type="layer.delete",target=Guid.NewGuid().ToString()});
                Check(failed.IsError==true&&(await Layers(client)).GetArrayLength()==4,"Failed batch leaked state.");Check(Enumerable.SequenceEqual(repaired, await Composite(client)),"Failed batch changed pixels.");
                Console.WriteLine("Stale revision and mixed-batch rollback: PASS");
                // An ordinary WPF visibility edit must be observable by the external client.
                var check=Find(window,"LayerList").FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.CheckBox));
                Check(check is not null,"Layer visibility checkbox missing.");
                string oldRevision=(await Context(client)).GetProperty("revision").GetString()!;
                Click(check!);
                await UntilAsync(async () => (await Context(client)).GetProperty("revision").GetString()!=oldRevision);
                await ClickReady(window,"UndoToolbarButton");
                Console.WriteLine("Ordinary WPF visibility edit visible to MCP: PASS");
                // Save through real file dialog, then load independently to compare durable authored content.
                durableLayers=(await Layers(client)).GetRawText(); durableComposite=await Composite(client);
                durablePart=layers[0].GetProperty("id").GetGuid();
                durableMask=await Mask(client,durablePart);
                await FileMenu(window,process.Id,"SaveAsMenuItem",project);
                await Until(() => File.Exists(project));
                var store=new FlimgProjectStore(); var loaded=store.Load(project);
                Check(loaded.Layers.Count==4,"v2 save lost authored layers.");
                Check(loaded.Layers.OfType<Flamoris.Cutwork.Core.RepairLayer>().Single().OwnerPartId.HasValue,"Save lost repair ownership.");
                await Menu(window,"McpMenu","McpReadMenu");await Rejected(client);
            }
            await OldPipeRejected(connection.Pipe);connection=await Connection(process.Id);
            await using(var handle=new ClientHandle(await Connect(bridge,connection)))
            {
                await FileMenu(window,process.Id,"OpenProjectMenuItem",project);await Rejected(handle.Client);
            }
            await OldPipeRejected(connection.Pipe);
            await Menu(window,"McpMenu","McpReadMenu");connection=await Connection(process.Id);
            await using(var handle=new ClientHandle(await Connect(bridge,connection)))
            {
                Check((await Layers(handle.Client)).GetRawText()==durableLayers,"Reopen changed stable IDs, order, ownership, visibility or metadata.");
                Check(Enumerable.SequenceEqual(durableComposite!,await Composite(handle.Client)),"Reopen changed composed repair pixels.");
                Check(Enumerable.SequenceEqual(durableMask!,await Mask(handle.Client,durablePart)),"Reopen changed authored mask pixels.");
                Console.WriteLine("UI Save/v2 reopen preserves IDs, ownership/order, mask and composite pixels: PASS");
                // Reopen exact same persistent document identity.
                await FileMenu(window,process.Id,"OpenProjectMenuItem",project);await Rejected(handle.Client);
            }
            await OldPipeRejected(connection.Pipe);
            await Menu(window,"McpMenu","McpEditMenu");connection=await Connection(process.Id);
            await using(var handle=new ClientHandle(await Connect(bridge,connection)))
            {
                await Context(handle.Client);await Menu(window,"McpMenu","McpStopMenu");await Rejected(handle.Client);
            }
            await OldPipeRejected(connection.Pipe);
            await Menu(window,"McpMenu","McpReadMenu");connection=await Connection(process.Id);
            await BridgeEof(bridge,connection);
            await using(var handle=new ClientHandle(await Connect(bridge,connection)))
            {
                await Context(handle.Client);await Task.Run(()=>((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close());await process.WaitForExitAsync().WaitAsync(Limit);await Rejected(handle.Client);
            }
            Console.WriteLine("PASS: published editor+Flamoris.Mcp.Bridge outside source with System32-only PATH; Flamoris.Mcp.Core 1.1.0 / official SDK 2.2.0 / MCP 2026-07-28; Read only image and direct denial; Part+Mask+Clone+Patch; automatic WPF projection; WPF Undo/Redo exact pixel restoration; UI edit -> MCP; stale/rollback; UI Save/v2 reopen; downgrade/Stop/same-file reopen/close/bridge stdin EOF/old endpoint rejection.");
        }
        catch { DumpUi(process.Id); throw; }
        finally {if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}try{Directory.Delete(temp,true);}catch(Exception e) when(e is IOException or UnauthorizedAccessException){} }
    }
    private static void DumpUi(int pid)
    {
        foreach(var e in AutomationElement.RootElement.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ProcessIdProperty,pid)).Cast<AutomationElement>().Take(160))
        {
            try{Console.WriteLine($"UI: {e.Current.ControlType.ProgrammaticName} / {e.Current.AutomationId} / {e.Current.Name}");}catch(ElementNotAvailableException){}
        }
    }
    private static void CopyDirectory(string from,string to){Directory.CreateDirectory(to);foreach(var file in Directory.GetFiles(from))File.Copy(file,Path.Combine(to,Path.GetFileName(file)));foreach(var dir in Directory.GetDirectories(from))CopyDirectory(dir,Path.Combine(to,Path.GetFileName(dir)));}
    private static CancellationToken Deadline()=>new CancellationTokenSource(Limit).Token;
    private static async Task<McpClient> Connect(string bridge,ConnectionInfo connection)=>await McpClient.CreateAsync(new StdioClientTransport(new(){Command=bridge,Arguments=["--pipe",connection.Pipe],Name="Published Cutwork",EnvironmentVariables=new Dictionary<string,string?>{["PATH"]=CleanPath,[StdioBridge.CredentialEnvironmentVariable]=connection.Capability}}),cancellationToken:Deadline());
    private static async Task PackagedBridgeIdle(string bridge)
    {
        using var host=new IdleHost();
        using var boundary=new McpBoundary(host,[],new()
        {
            Enabled=true,
            Permission=McpPermission.ReadOnly,
            PipeName="flamoris-cutwork-smoke-"+Guid.NewGuid().ToString("N"),
            MaxConcurrentRequests=1,
            ReadTimeoutMs=100,
        },new McpDiagnostics(FlamorisLogger.Create()));
        using var grant=await boundary.EnableAsync(McpPermission.ReadOnly);
        using var lifetime=new CancellationTokenSource(Limit);
        var serving=new LocalMcpEndpoint(boundary).RunAsync(grant,lifetime.Token);
        await using var handle=new ClientHandle(await Connect(bridge,new(boundary.Options.PipeName,grant.ExportCredential())));
        await Task.Delay(350,lifetime.Token);
        Check(!handle.Client.Completion.IsCompleted,"Packaged bridge disconnected an idle client at ReadTimeoutMs.");
        var context=await Common(handle.Client);
        Check(context.GetProperty("productId").GetString()=="flamoris.cutwork.idle-smoke","Idle client could not use MCP after ReadTimeoutMs.");
        boundary.Disable();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        await handle.Client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine("Packaged bridge idle beyond ReadTimeoutMs and prompt revocation: PASS");
    }
    private static JsonElement Text(CallToolResult result)=>JsonDocument.Parse(result.Content.OfType<TextContentBlock>().First().Text).RootElement.Clone();
    private static async Task<JsonElement> Common(McpClient c)=>Text(await c.CallToolAsync("mcp.context",new Dictionary<string,object?>(),cancellationToken:Deadline()));
    private static Dictionary<string,object?> Envelope(JsonElement common,object input,string? documentToken=null,string? revision=null)
    {
        var guard=new Dictionary<string,object?>{{"runtimeId",common.GetProperty("runtimeId").GetString()},{"documentToken",documentToken??common.GetProperty("documentToken").GetString()}};
        if(revision is not null)guard["expectedRevision"]=revision;
        return new Dictionary<string,object?>{{"input",JsonSerializer.SerializeToElement(input)},{"guard",guard}};
    }
    private static async Task<JsonElement> Context(McpClient c){var common=await Common(c);return Text(await c.CallToolAsync("context",Envelope(common,new{}),cancellationToken:Deadline()));}
    private static async Task<CallToolResult> Query(McpClient c,string name,object input){var common=await Common(c);return await c.CallToolAsync(name,Envelope(common,input),cancellationToken:Deadline());}
    private static async Task<JsonElement> Layers(McpClient c)=>Text(await Query(c,"layers",new{offset=0,count=64})).GetProperty("layers");
    private static async Task<CallToolResult> Edit(McpClient c,JsonElement context,params object[] ops){var common=await Common(c);return await c.CallToolAsync("edit",Envelope(common,new{operations=ops},context.GetProperty("documentToken").GetString(),context.GetProperty("revision").GetString()),cancellationToken:Deadline());}
    private static byte[] Png(CallToolResult result)=>Convert.FromBase64String(Text(result).GetProperty("pngBase64").GetString()!);
    private static async Task<byte[]> Composite(McpClient c)=>Png(await Query(c,"image",new{source="Composite",target=(string?)null,roi=(object?)null,maxEdge=32}));
    private static async Task<byte[]> Mask(McpClient c,Guid id)=>Png(await Query(c,"image",new{source="Mask",target=id.ToString(),roi=(object?)null,maxEdge=32}));
    private static AutomationElement Find(AutomationElement root,string id)=>root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,id))??throw new Exception("Missing UI control: "+id);
    private static Task Invoke(AutomationElement e)=>Task.Run(()=>((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
    private static async Task ClickReady(AutomationElement root,string id){await Until(()=>Find(root,id).Current.IsEnabled);await Invoke(Find(root,id));}
    private static async Task Menu(AutomationElement root,string parent,string child){((ExpandCollapsePattern)Find(root,parent).GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();await Invoke(Find(root,child));}
    private static bool VisibleValue(AutomationElement root,string text)=>root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit)).Cast<AutomationElement>().Any(e=>e.TryGetCurrentPattern(ValuePattern.Pattern,out var p)&&((ValuePattern)p).Current.Value==text);
    private static async Task FileMenu(AutomationElement root,int pid,string item,string path){var opening=Menu(root,"FileMenu",item);await ChooseFile(pid,path);await opening;await Until(()=>root.Current.IsEnabled);}
    private static async Task ChooseFile(int pid,string path)
    {
        AutomationElement? filename=null;await Until(()=>(filename=AutomationElement.RootElement.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,pid),new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty,"1148"),new PropertyCondition(AutomationElement.AutomationIdProperty,"1001")))))is not null);
        var edit=filename!.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit))
            ?? (filename.TryGetCurrentPattern(ValuePattern.Pattern,out _)?filename:null)
            ?? throw new Exception("Native filename edit missing.");
        Console.WriteLine("Native filename field: "+edit.Current.AutomationId+" / "+edit.Current.Name+" / "+edit.Current.ClassName);
        TypeText(edit,path);
        await Until(()=>((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value==path);AutomationElement? dialog=edit;
        while(dialog is not null&&dialog.Current.ClassName!="#32770")dialog=TreeWalker.ControlViewWalker.GetParent(dialog);var accept=Find(dialog!,"1");Console.WriteLine("Native accept button: "+accept.Current.Name);await Invoke(accept);
    }
    private static async Task<ConnectionInfo> Connection(int pid)
    {
        AutomationElement? edit=null;await Until(()=>(edit=AutomationElement.RootElement.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,pid),new PropertyCondition(AutomationElement.AutomationIdProperty,"McpConnection"))))is not null);
        string json=((ValuePattern)edit!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;var server=JsonDocument.Parse(json).RootElement.GetProperty("mcpServers").GetProperty("cutwork");string pipe=server.GetProperty("args")[1].GetString()!;string capability=server.GetProperty("env").GetProperty(StdioBridge.CredentialEnvironmentVariable).GetString()!;
        AutomationElement? dialog=edit;while(dialog is not null&&dialog.Current.ControlType!=ControlType.Window)dialog=TreeWalker.ControlViewWalker.GetParent(dialog);
        await Task.Run(()=>((WindowPattern)dialog!.GetCurrentPattern(WindowPattern.Pattern)).Close());return new(pipe,capability);
    }
    private static async Task Rejected(McpClient c){bool rejected=false;try{var r=await Context(c);rejected=r.TryGetProperty("error",out _);}catch(Exception e)when(e is IOException or ModelContextProtocol.McpException or OperationCanceledException){rejected=true;}Check(rejected,"Old client retained access.");}
    private static async Task OldPipeRejected(string name)
    {
        // Revocation is synchronous, while the Core endpoint closes its pending listener
        // asynchronously in response to cancellation. Allow that close to settle, then
        // require the stale address itself to become unreachable.
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(5))
        {
            using var p=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            try{await p.ConnectAsync(200);}
            catch(TimeoutException){return;}
            await Task.Delay(50);
        }
        throw new Exception("Old endpoint remained reachable after revocation.");
    }
    private static async Task BridgeEof(string bridge,ConnectionInfo connection)
    {
        var start=new ProcessStartInfo(bridge){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true};start.ArgumentList.Add("--pipe");start.ArgumentList.Add(connection.Pipe);start.Environment["PATH"]=CleanPath;start.Environment[StdioBridge.CredentialEnvironmentVariable]=connection.Capability;
        using var p=Process.Start(start)!;p.StandardInput.Close();try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}finally{if(!p.HasExited)p.Kill(true);}
    }
    private sealed record ConnectionInfo(string Pipe,string Capability);
    private sealed class IdleHost:IMcpHost,IDisposable
    {
        private readonly string runtimeId=Guid.NewGuid().ToString("N");
        public HostSnapshot Snapshot=>new("flamoris.cutwork.idle-smoke","1.0.1",runtimeId,"idle-document",0);
        public event Action? Invalidating;
        public Task<T> InvokeAsync<T>(Func<T> action,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(action());
        }
        public void Dispose()=>Invalidating?.Invoke();
    }
    private sealed class ClientHandle(McpClient client):IAsyncDisposable{public McpClient Client=>client;public async ValueTask DisposeAsync(){try{await client.DisposeAsync();}catch(IOException){}}}
    private static async Task Until(Func<bool> predicate){var watch=Stopwatch.StartNew();while(!predicate()){if(watch.Elapsed>Limit)throw new TimeoutException("UI condition not observed.");await Task.Delay(100);}}
    private static async Task UntilAsync(Func<Task<bool>> predicate){var watch=Stopwatch.StartNew();while(!await predicate()){if(watch.Elapsed>Limit)throw new TimeoutException("UI change not observed by MCP.");await Task.Delay(100);}}
    private static void TypeText(AutomationElement edit,string text)
    {
        edit.SetFocus();
        var input = new List<KeyboardInput> { new(){Type=1,VirtualKey=0x11},new(){Type=1,VirtualKey=0x41},new(){Type=1,VirtualKey=0x41,Flags=2},new(){Type=1,VirtualKey=0x11,Flags=2} };
        foreach(char c in text){input.Add(new(){Type=1,ScanCode=c,Flags=4});input.Add(new(){Type=1,ScanCode=c,Flags=6});}
        Check(SendInput((uint)input.Count,input.ToArray(),Marshal.SizeOf<KeyboardInput>())==input.Count,"Native filename input failed.");
    }
    // The published acceptance target is Windows x64; INPUT has a 32-byte union at offset 8.
    [StructLayout(LayoutKind.Explicit,Size=40)]private struct KeyboardInput
    {
        [FieldOffset(0)]public uint Type;
        [FieldOffset(8)]public ushort VirtualKey;
        [FieldOffset(10)]public ushort ScanCode;
        [FieldOffset(12)]public uint Flags;
    }
    [DllImport("user32.dll",SetLastError=true)]private static extern uint SendInput(uint count,KeyboardInput[] inputs,int size);
    private static void Click(AutomationElement element)
    {
        // WPF TogglePattern only changes IsChecked; it does not raise the ordinary Click command.
        var p = element.GetClickablePoint(); Check(SetCursorPos((int)p.X,(int)p.Y),"Cannot position mouse.");
        MouseEvent(2,0,0,0,UIntPtr.Zero); MouseEvent(4,0,0,0,UIntPtr.Zero);
    }
    [DllImport("user32.dll")]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll",EntryPoint="mouse_event")]private static extern void MouseEvent(uint flags,uint x,uint y,uint data,UIntPtr extra);
    private static uint ScreenPixel(AutomationElement root,int x,int y)
    {
        var bounds=Find(root,"ImageSurface").Current.BoundingRectangle;
        Check(bounds.Width>0&&bounds.Height>0,"Canvas image has no screen bounds.");
        IntPtr dc=GetDC(IntPtr.Zero);try{return GetPixel(dc,(int)Math.Floor(bounds.X+(x+0.5)*bounds.Width/32),(int)Math.Floor(bounds.Y+(y+0.5)*bounds.Height/32));}finally{ReleaseDC(IntPtr.Zero,dc);}
    }
    [DllImport("user32.dll")]private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]private static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")]private static extern uint GetPixel(IntPtr dc,int x,int y);
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}
