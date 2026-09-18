using System.IO;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Flamoris.Cutwork.Mcp;

public static class LiveProtocol
{
    public const string ProtocolVersion = "2025-03-26";
    public static async Task ServeAsync(Stream pipe, LiveAccess access,
        Func<Func<Task<CallToolResult>>, Task<CallToolResult>> dispatch,
        LiveEditor editor)
    {
        await using var stream = new BoundedProtocolStream(pipe, access.Token);
        await using var transport = new StreamServerTransport(stream, stream, "Cutwork");
        int admitted = 0;
        var options = new McpServerOptions {
            ServerInfo = new() { Name = "flamoris-cutwork", Version = "0.1.0" },
            ProtocolVersion = ProtocolVersion, InitializationTimeout = TimeSpan.FromSeconds(15), ScopeRequests = false,
            Capabilities = new() { Tools = new() },
            Handlers = new() {
                ListToolsHandler = (_, ct) => {
                    access.Token.ThrowIfCancellationRequested(); ct.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(new ListToolsResult { Tools = LiveSchema.Tools(access.Permission) }); },
                CallToolHandler = async (request, ct) => {
                    if (Interlocked.CompareExchange(ref admitted, 1, 0) != 0) return LiveEditor.Error("busy");
                    try {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, stream.Closed, access.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(15));
                        var args = JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
                        var result = await dispatch(() => editor.CallAsync(request.Params?.Name ?? "", args, deadline.Token));
                        deadline.Token.ThrowIfCancellationRequested();
                        return result;
                    }
                    catch (OperationCanceledException) { return LiveEditor.Error("cancelled"); }
                    catch (Exception) { return LiveEditor.Error("unavailable"); }
                    finally { Interlocked.Exchange(ref admitted, 0); }
                }
            }
        };
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(stream.Closed);
    }
}
