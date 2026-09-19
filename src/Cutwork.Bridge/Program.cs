using System.IO.Pipes;

var logger = BridgeLogging.Create();
if (args.Length != 2 || args[0] != "--pipe" || !(args[1].StartsWith("cutwork-", StringComparison.Ordinal) && Guid.TryParseExact(args[1][8..], "N", out _)))
{
    logger.Warn("mcp.transport", "Bridge rejected invalid startup arguments");
    Console.Error.WriteLine("Usage: Cutwork.Bridge --pipe <name displayed by the running editor>");
    return 2;
}
logger.Info("mcp.transport", "Bridge started", new Dictionary<string, object?>
{
    ["transport"] = "same-user-named-pipe",
});
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
try
{
    await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(10000, lifetime.Token);
    logger.Info("mcp.transport", "Bridge connected", new Dictionary<string, object?>
    {
        ["transport"] = "same-user-named-pipe",
    });
    var input = Console.OpenStandardInput().CopyToAsync(pipe, lifetime.Token);
    var output = pipe.CopyToAsync(Console.OpenStandardOutput(), lifetime.Token);
    await Task.WhenAny(input, output);
    lifetime.Cancel();
    pipe.Dispose();
    // Windows console stdin may not honor cancellation. A losing pump must not
    // hold the bridge alive after editor EOF or stdin closure.
    try { await Task.WhenAll(input, output).WaitAsync(TimeSpan.FromSeconds(2)); }
    catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException) { }
    logger.Info("mcp.transport", "Bridge stopped", new Dictionary<string, object?>
    {
        ["transport"] = "same-user-named-pipe",
    });
    return 0;
}
catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or System.Security.SecurityException)
{
    var category = e is UnauthorizedAccessException or System.Security.SecurityException
        ? "mcp.auth" : "mcp.transport";
    logger.Error(category, "Bridge connection failed", e, new Dictionary<string, object?>
    {
        ["transport"] = "same-user-named-pipe",
        ["exceptionType"] = e.GetType().Name,
    });
    Console.Error.WriteLine("MCP bridge connection closed or unavailable."); return 1;
}
