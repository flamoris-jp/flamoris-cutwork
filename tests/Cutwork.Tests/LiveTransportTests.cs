using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LiveTransportTests
{
    [TestMethod]
    public async Task Core101IdleConnectionRemainsUsablePastFrameTimeoutAndRevokeClosesIt()
    {
        var session = LiveMcpTests.Open();
        using var mcp = await McpCoreHarness.CreateAsync(session, options: new()
        {
            ReadTimeoutMs = 100,
        });
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = new LocalMcpEndpoint(mcp.Boundary).RunAsync(mcp.Grant, lifetime.Token);
        await using var pipe = await ConnectAsync(mcp, lifetime.Token);

        await Task.Delay(350, lifetime.Token);
        await WriteLineAsync(pipe, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2026-07-28",
                capabilities = new { },
                clientInfo = new { name = "cutwork-idle-test", version = "1.0" },
            },
        }, lifetime.Token);
        using (var initialized = JsonDocument.Parse(await ReadLineAsync(pipe, lifetime.Token)))
        {
            Assert.AreEqual(1, initialized.RootElement.GetProperty("id").GetInt32());
            Assert.IsFalse(initialized.RootElement.TryGetProperty("error", out _));
        }
        await WriteLineAsync(pipe, new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
        }, lifetime.Token);
        await WriteLineAsync(pipe, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new { name = "mcp.context", arguments = new { } },
        }, lifetime.Token);
        string context = await ReadLineAsync(pipe, lifetime.Token);
        StringAssert.Contains(context, "flamoris.cutwork");
        StringAssert.Contains(context, session.DocumentToken);

        mcp.Boundary.Disable();
        await serving.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await ClosedAsync(pipe), "Revocation did not interrupt the idle connection.");
    }

    [TestMethod]
    public async Task Core101StartedPartialFrameStillClosesWithinReadTimeout()
    {
        var session = LiveMcpTests.Open();
        using var mcp = await McpCoreHarness.CreateAsync(session, options: new()
        {
            ReadTimeoutMs = 100,
        });
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = new LocalMcpEndpoint(mcp.Boundary).RunAsync(mcp.Grant, lifetime.Token);
        await using var pipe = await ConnectAsync(mcp, lifetime.Token);

        await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":"), lifetime.Token);
        await pipe.FlushAsync(lifetime.Token);
        Assert.IsTrue(await ClosedAsync(pipe), "A started partial frame exceeded its read deadline.");

        mcp.Boundary.Disable();
        await serving.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(
        McpCoreHarness mcp, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", mcp.Boundary.Options.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2000, cancellationToken);
        await WriteLineAsync(pipe, new
        {
            version = 1,
            capability = mcp.Grant.ExportCredential(),
        }, cancellationToken);
        using var reply = JsonDocument.Parse(await ReadLineAsync(pipe, cancellationToken));
        Assert.AreEqual(1, reply.RootElement.GetProperty("version").GetInt32());
        return pipe;
    }

    private static async Task WriteLineAsync(Stream stream, object value,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<string> ReadLineAsync(Stream stream,
        CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var single = new byte[1];
        while (line.Length <= LiveLimits.FrameBytes)
        {
            int count = await stream.ReadAsync(single, cancellationToken);
            if (count == 0) throw new IOException("Connection closed before a complete frame.");
            if (single[0] == (byte)'\n') return Encoding.UTF8.GetString(line.ToArray());
            line.WriteByte(single[0]);
        }
        throw new IOException("Response frame exceeded the Cutwork limit.");
    }

    private static async Task<bool> ClosedAsync(Stream stream)
    {
        try
        {
            int count = await stream.ReadAsync(new byte[1]).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            return count == 0;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return true;
        }
    }
}
