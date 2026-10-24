using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging.Persistence;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LiveProjectTests
{
    [TestMethod]
    public async Task SaveRequiresSeparateUiAuthorizationAndReportsJobReceipt()
    {
        var folder=Path.Combine(Path.GetTempPath(),"cutwork-save-job-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var s=LiveMcpTests.Open();var workspace=new ProjectWorkspace(s);workspace.Save(Path.Combine(folder,"project.flimg"));
            using var m=await McpCoreHarness.CreateAsync(s,workspace:workspace);
            var context=Value(await m.CallAsync("project_context",new{}));Assert.IsFalse(context.GetProperty("saveAuthorized").GetBoolean());
            workspace.AuthorizeAiSave();s.Execute(new RenameLayer(s.Document!.Base.Id,"AI saved"));
            context=Value(await m.CallAsync("project_context",new{}));var target=context.GetProperty("target");
            string requestId=Guid.NewGuid().ToString("D");
            var request=new{saveTargetId=target.GetProperty("saveTargetId").GetString(),expectedCheckpointId=(string?)null,
                expectedWorkingFileHash=target.GetProperty("expectedWorkingFileHash").GetString(),requestId,label="checkpoint test"};
            var job=Value(await m.CallAsync("project_save",request));Assert.AreEqual("running",job.GetProperty("state").GetString());
            JsonElement result=default;
            for(int i=0;i<500;i++)
            {
                result=Value(await m.CallAsync("project_save_status",new{requestId}));
                if(result.GetProperty("state").GetString()!="running")break;
                await Task.Delay(10);
            }
            Assert.AreEqual("completed",result.GetProperty("state").GetString(),result.ToString());
            Assert.IsTrue(result.GetProperty("receipt").GetProperty("checkpointSaved").GetBoolean());
            Assert.IsTrue(result.GetProperty("receipt").GetProperty("workingCopySaved").GetBoolean());Assert.IsFalse(s.IsDirty);Assert.IsFalse(workspace.IsSaving);
            Assert.AreEqual("AI saved",new FlimgProjectStore().Load(workspace.ProjectPath!).Base.Name);
        }
        finally{Directory.Delete(folder,true);}
    }
    private static JsonElement Value(McpResult result)
    {Assert.IsFalse(result.IsError,result.Error);return result.Value!.Value;}
}
