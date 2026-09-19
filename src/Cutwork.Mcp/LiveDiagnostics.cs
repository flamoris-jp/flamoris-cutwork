using Flamoris.Logging;

namespace Flamoris.Cutwork.Mcp;

/// <summary>Shared, stateless MCP diagnostic events. Never receives request payloads or pipe names.</summary>
public static class LiveDiagnostics
{
    public static void TransportFailure(FlamorisLogger? logger, Exception exception, bool recoverable)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var properties = new Dictionary<string, object?>
        {
            ["transport"] = "same-user-named-pipe",
            ["exceptionType"] = exception.GetType().Name,
        };
        if (recoverable) logger?.Warn("mcp.transport", "Client connection lost", properties);
        else logger?.Error("mcp.transport", "MCP transport failed", properties: properties);
    }
}
