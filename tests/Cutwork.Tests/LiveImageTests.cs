using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Mcp;
using ModelContextProtocol.Protocol;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LiveImageTests
{
    private static readonly object[] Fence = [new{x=4,y=4},new{x=20,y=4},new{x=20,y=20},new{x=4,y=20}];
    [TestMethod]
    public Task PurePreviewReturnsSameMaskAsCreateWithoutTouchingSession() => Sta(async () =>
    {
        var s=LiveMcpTests.Open();using var lease=new LiveAccess(s,LivePermission.Edit);
        var e=new LiveEditor(s,lease,()=>false,()=>Task.CompletedTask,_=>{});
        var selection=s.SelectedLayerId;var token=s.DocumentToken;var rev=s.Document!.Revision;
        var preview=await e.CallAsync("part_preview",JsonSerializer.SerializeToElement(new{fence=Fence,step=2,maxEdge=1024}));
        Assert.IsFalse(preview.IsError==true);Assert.AreEqual(rev,s.Document.Revision);Assert.AreEqual(selection,s.SelectedLayerId);
        Assert.AreEqual(0,s.UndoCount);Assert.IsFalse(s.IsDirty);
        var image=preview.Content.OfType<ImageContentBlock>().Single();var rendered=Decode(image.DecodedData.ToArray());
        var create=await e.CallAsync("edit",JsonSerializer.SerializeToElement(new{documentToken=token,expectedRevision=rev.ToString(),operations=new[]{new{type="part.create",fence=Fence,step=2,name="pure"}}}));
        Assert.IsFalse(create.IsError==true);var p=s.Document.Layers.OfType<PartLayer>().Single();var mask=p.CopyMask(p.Bounds);
        for(int i=0;i<mask.Length;i++)Assert.AreEqual(mask[i],rendered[i*4]);
    });
    [TestMethod]
    public Task RoiCompositeMatchesExistingCacheAndExactCoordinateMapping() => Sta(() =>
    {
        var s=LiveMcpTests.Open();s.Execute(new AddLayer(new PartLayer(new(4,4,8,8),Enumerable.Repeat((byte)255,64).ToArray())));
        s.Execute(new SetLayerVisibility(s.Document!.Layers.OfType<PartLayer>().Single().Id,false));
        using var cache=new CompositeCache(s.Document);cache.RenderPending();var all=cache.CopyPixels();
        var image=LiveImages.Capture(s.Document,"Composite",null,new(4,4,8,8),4,default);
        Assert.AreEqual(2d,image.DocumentPixelsPerOutputX);Assert.AreEqual(new DocumentRect(4,4,8,8),image.Crop);
        var actual=Decode(image.Png);
        for(int y=0;y<4;y++)for(int x=0;x<4;x++)
            CollectionAssert.AreEqual(all.Skip(((4+y*2+1)*32+4+x*2+1)*4).Take(4).ToArray(),actual.Skip((y*4+x)*4).Take(4).ToArray());
        return Task.CompletedTask;
    });
    [TestMethod]
    public Task PreparedImageCannotEscapeAfterStopReplacementOrDowngrade() => Sta(async () =>
    {
        foreach(string change in new[]{"stop","replace","downgrade"})
        {
            var s=LiveMcpTests.Open();using var old=new LiveAccess(s,LivePermission.Edit);LiveAccess? fresh=null;
            var e=new LiveEditor(s,old,()=>false,()=>{
                if(change=="replace")s.Open(new CutworkDocument(s.Document!.Original));else old.Revoke();
                fresh=new LiveAccess(s,LivePermission.ReadOnly);return Task.CompletedTask;
            },_=>{});
            var r=await e.CallAsync("image",JsonSerializer.SerializeToElement(new{source="Original",target=(string?)null,roi=(object?)null,maxEdge=32}));
            Assert.IsTrue(r.IsError==true);Assert.IsFalse(r.Content.OfType<ImageContentBlock>().Any());Assert.IsFalse(old.IsActive);fresh?.Dispose();
        }
    });
    private static byte[] Decode(byte[] png)
    {
        using var stream=new MemoryStream(png);var frame=new PngBitmapDecoder(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad).Frames[0];
        var converted=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);var pixels=new byte[frame.PixelWidth*frame.PixelHeight*4];converted.CopyPixels(pixels,frame.PixelWidth*4,0);return pixels;
    }
    private static Task Sta(Func<Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{
            var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async ()=>{try{await action();done.TrySetResult();}catch(Exception e){done.TrySetException(e);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});
            Dispatcher.Run();
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
}
