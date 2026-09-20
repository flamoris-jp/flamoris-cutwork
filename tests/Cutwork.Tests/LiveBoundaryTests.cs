using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LiveBoundaryTests
{
    [TestMethod]
    public async Task CoreNativePipeUsesProtectedOwnerOnlyAclAndLocalClient()
    {
        var type = typeof(McpBoundary).Assembly.GetType("Flamoris.Mcp.Core.WindowsLocalPipe")!;
        Assert.AreEqual(8U, (uint)type.GetField("RejectRemoteClients",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!);
        string name = "flamoris-test-" + Guid.NewGuid().ToString("N");
        using var server = (NamedPipeServerStream)type.GetMethod("Create",
            BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [name])!;
        using var identity = WindowsIdentity.GetCurrent();
        uint code = GetSecurityInfo(server.SafePipeHandle.DangerousGetHandle(), 6, 1 | 4,
            out _, out _, out _, out _, out var descriptor);
        Assert.AreEqual(0U, code);
        try
        {
            int size = (int)GetSecurityDescriptorLength(descriptor);
            var bytes = new byte[size];
            Marshal.Copy(descriptor, bytes, 0, size);
            var security = new RawSecurityDescriptor(bytes, 0);
            Assert.AreEqual(identity.Owner, security.Owner);
            Assert.IsTrue(security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
            Assert.AreEqual(1, security.DiscretionaryAcl!.Count);
            Assert.AreEqual(identity.Owner,
                ((CommonAce)security.DiscretionaryAcl[0]).SecurityIdentifier);
        }
        finally { LocalFree(descriptor); }
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var wait = server.WaitForConnectionAsync();
        await client.ConnectAsync(2000);
        await wait;
        Assert.IsTrue(server.IsConnected);
    }

    [TestMethod]
    public void CoreOptionsKeepCutworkFrameTimeoutAndConcurrencyBounds()
    {
        var options = new McpOptions
        {
            Enabled = true,
            PipeName = "flamoris-cutwork-" + Guid.NewGuid().ToString("N"),
            MaxRequestBytes = 4 * 1024 * 1024,
            MaxConcurrentRequests = 1,
            RequestTimeoutMs = 15_000,
            ReadTimeoutMs = 120_000,
            WriteTimeoutMs = 5_000,
        };
        options.Validate();
        Assert.ThrowsExactly<ArgumentException>(() => (options with { MaxRequestBytes = 17 * 1024 * 1024 }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (options with { MaxConcurrentRequests = 0 }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (options with { PipeName = "cutwork-legacy" }).Validate());
    }

    [TestMethod]
    public async Task NoOpMutationDoesNotInventHistoryAndCoreGrantIsRedacted()
    {
        var session = LiveMcpTests.Open();
        using var mcp = await McpCoreHarness.CreateAsync(session);
        var result = await mcp.CallAsync("edit", new
        {
            operations = new[]
            {
                new { type = "layer.rename", target = session.Document!.Base.Id.ToString(), name = "" },
            },
        });
        Assert.IsFalse(result.IsError, result.Error);
        Assert.AreEqual(0, session.UndoCount);
        Assert.AreEqual("CapabilityGrant [redacted]", mcp.Grant.ToString());
        Assert.AreEqual(64, mcp.Grant.ExportCredential().Length);
    }

    [TestMethod]
    public void StatusProjectionClearsForegroundActivityWhenEndpointIsLost()
    {
        var status = new StatusProjection();
        using var activity = status.BeginForeground();
        Assert.IsTrue(status.Current.ActivityVisible);
        typeof(StatusProjection).GetMethod("Connection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(status, [false, false, false, null]);
        Assert.IsFalse(status.Current.ActivityVisible);
        Assert.IsFalse(status.Current.IsGreen);
    }

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(IntPtr handle, int type, uint info,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
