using Flamoris.Cutwork.Core;
using Flamoris.Mcp.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Flamoris.Cutwork.Tests;
[TestClass]
public sealed class ApplicationLifetimeTests
{
    [TestMethod]
    public async Task EmptyStartupKeepsGrantAcrossOpenAndClose()
    {
        var session = new EditorSession();
        using var mcp = await McpCoreHarness.CreateAsync(session, options: new() { ApplicationLifetime = true });
        string credential = mcp.Grant.ExportCredential();
        Assert.AreEqual(McpErrors.NoDocument, (await mcp.CallAsync("context", new { })).Error);
        session.Open(new(new("synthetic.png", new(2, 2), 8, new byte[16])));
        Assert.IsTrue(mcp.Grant.Authenticate(credential));
        string old = session.DocumentToken;
        session.Open(new(new("synthetic.png", new(2, 2), 8, new byte[16])));
        Assert.AreNotEqual(old, session.DocumentToken);
        Assert.AreEqual(McpErrors.StaleDocument, (await mcp.CallAsync("context", new { }, documentToken: old)).Error);
        Assert.IsTrue(mcp.Grant.IsActive);
        mcp.Host.Shutdown(); Assert.IsFalse(mcp.Grant.IsActive);
    }
}
