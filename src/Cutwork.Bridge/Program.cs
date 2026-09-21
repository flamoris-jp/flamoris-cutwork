using Flamoris.Mcp.Core;

using var output = Console.OpenStandardOutput();
Console.SetOut(Console.Error);
var logger = BridgeLogging.Create();
string? capability = Environment.GetEnvironmentVariable(StdioBridge.CredentialEnvironmentVariable);
Environment.SetEnvironmentVariable(StdioBridge.CredentialEnvironmentVariable, null);

if (args.Length != 2 || args[0] != "--pipe" || !McpOptions.ValidPipeName(args[1]))
{
    logger.Warn("mcp.transport", "Bridge rejected invalid startup arguments");
    Console.Error.WriteLine("{\"error\":\"invalid_request\"}");
    return 2;
}

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    lifetime.Cancel();
};

try
{
    logger.Info("mcp.transport", "Bridge started", new Dictionary<string, object?>
    {
        ["transport"] = "stdioBridge",
    });
    await StdioBridge.RunAsync(args[1], capability, Console.OpenStandardInput(), output,
        cancellationToken: lifetime.Token);
    logger.Info("mcp.transport", "Bridge stopped", new Dictionary<string, object?>
    {
        ["transport"] = "stdioBridge",
    });
    return 0;
}
catch (McpFault fault)
{
    logger.Warn("mcp.transport", "Bridge connection rejected", new Dictionary<string, object?>
    {
        ["outcome"] = fault.Code,
    });
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { error = fault.Code }));
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("{\"error\":\"cancelled\"}");
    return 1;
}
catch
{
    logger.Error("mcp.transport", "Bridge connection failed");
    Console.Error.WriteLine("{\"error\":\"transport_unavailable\"}");
    return 1;
}
