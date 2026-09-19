using System.Diagnostics;
using System.Text.Json;
using Flamoris.Logging;

internal static class BridgeLogging
{
    private static readonly object ConsoleFallbackGate = new();

    internal static FlamorisLogger Create(LoggingOptions? configuredOptions = null, string? configuredBasePath = null)
    {
        var options = configuredOptions ?? Load();
        // stdout is the MCP protocol stream. A console sink would corrupt frames.
        options.Outputs = options.Outputs
            .Where(output => string.Equals(output.Type, "file", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (options.Outputs.Count == 0)
            options.Outputs.Add(new() { Type = "file", Path = "logs/cutwork.log" });
        foreach (var output in options.Outputs) output.Path = BridgePath(output.Path);
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        var basePath = configuredBasePath ?? Path.Combine(root, "FLAMORIS", "Cutwork");

        // Flamoris.Logging 1.0.0 falls back to a ConsoleLogSink when every
        // configured sink fails. Capture that fallback on stderr so MCP frames
        // on stdout remain valid even when the file sink cannot be constructed.
        lock (ConsoleFallbackGate)
        {
            var protocolOutput = Console.Out;
            try
            {
                Console.SetOut(Console.Error);
                return FlamorisLogger.Create(options, basePath, message => Debug.WriteLine(message));
            }
            finally
            {
                Console.SetOut(protocolOutput);
            }
        }
    }

    private static LoggingOptions Load()
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "appsettings.json"));
            if (!File.Exists(path)) return Defaults();
            using var input = File.OpenRead(path);
            return JsonSerializer.Deserialize<Settings>(input, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            })?.Logging ?? Defaults();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Bridge logging configuration unavailable: {exception.Message}");
            return Defaults();
        }
    }

    private static LoggingOptions Defaults() => new()
    {
        Level = "debug",
        Outputs = [new() { Type = "file", Path = "logs/cutwork.log" }],
    };

    private static string BridgePath(string? configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? "logs/cutwork.log" : configuredPath;
        try
        {
            var extension = Path.GetExtension(path);
            return Path.Combine(Path.GetDirectoryName(path) ?? string.Empty,
                Path.GetFileNameWithoutExtension(path) + "-mcp-bridge" + extension);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            // Leave invalid paths for FlamorisLogger.Create to reject. Its
            // console fallback is redirected to stderr by Create above.
            return path;
        }
    }

    private sealed class Settings
    {
        public Settings() { }
        public LoggingOptions? Logging { get; set; }
    }
}
