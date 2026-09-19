using System.Diagnostics;
using System.Text.Json;
using Flamoris.Logging;

internal static class BridgeLogging
{
    internal static FlamorisLogger Create()
    {
        var options = Load();
        // stdout is the MCP protocol stream. A console sink would corrupt frames.
        options.Outputs = options.Outputs
            .Where(output => string.Equals(output.Type, "file", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (options.Outputs.Count == 0)
            options.Outputs.Add(new() { Type = "file", Path = "logs/cutwork.log" });
        foreach (var output in options.Outputs) output.Path = BridgePath(output.Path);
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        var basePath = Path.Combine(root, "FLAMORIS", "Cutwork");
        return FlamorisLogger.Create(options, basePath, Debug.WriteLine);
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
        var extension = Path.GetExtension(path);
        return Path.Combine(Path.GetDirectoryName(path) ?? string.Empty,
            Path.GetFileNameWithoutExtension(path) + "-mcp-bridge" + extension);
    }

    private sealed class Settings
    {
        public Settings() { }
        public LoggingOptions? Logging { get; set; }
    }
}
