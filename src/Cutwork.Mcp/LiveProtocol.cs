using System.IO;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Flamoris.Logging;

namespace Flamoris.Cutwork.Mcp;

public static class LiveProtocol
{
    public const string ProtocolVersion = "2025-03-26";
    public static async Task ServeAsync(Stream pipe, LiveAccess access,
        Func<Func<Task<CallToolResult>>, Task<CallToolResult>> dispatch,
        LiveEditor editor, FlamorisLogger? logger = null)
    {
        logger ??= access.Logger;
        logger?.Debug("mcp.protocol", "Protocol session starting", new Dictionary<string, object?>
        {
            ["protocolVersion"] = ProtocolVersion,
            ["permission"] = access.Permission.ToString(),
        });
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
                    catch (OperationCanceledException)
                    {
                        logger?.Warn("mcp.protocol", "Protocol request timed out or was cancelled", new Dictionary<string, object?>
                        {
                            ["reason"] = access.IsActive ? "deadline-or-client" : "access-revoked",
                        });
                        return LiveEditor.Error("cancelled");
                    }
                    catch (Exception exception)
                    {
                        logger?.Error("mcp.protocol", "Protocol request failed", properties: new Dictionary<string, object?>
                        {
                            ["exceptionType"] = exception.GetType().Name,
                        });
                        return LiveEditor.Error("unavailable");
                    }
                    finally { Interlocked.Exchange(ref admitted, 0); }
                }
            }
        };
        await using var server = McpServer.Create(transport, options);
        try { await server.RunAsync(stream.Closed); }
        catch (Exception exception)
        {
            LiveDiagnostics.TransportFailure(logger, exception, recoverable: false);
            throw;
        }
        finally { logger?.Debug("mcp.protocol", "Protocol session stopped"); }
    }
}
