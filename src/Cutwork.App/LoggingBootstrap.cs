using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Flamoris.Logging;

namespace Flamoris.Cutwork.App;

internal static class LoggingBootstrap
{
    internal const string SettingsFileName = "appsettings.json";

    internal static FlamorisLogger Create(string? settingsPath = null, string? basePath = null)
    {
        var options = LoadOptions(settingsPath ?? Path.Combine(AppContext.BaseDirectory, SettingsFileName));
        return FlamorisLogger.Create(options, basePath ?? DefaultBasePath(), message => Debug.WriteLine(message));
    }

    internal static LoggingOptions LoadOptions(string path)
    {
        try
        {
            if (!File.Exists(path)) return Defaults();
            using var input = File.OpenRead(path);
            var settings = JsonSerializer.Deserialize<ApplicationSettings>(input, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            return settings?.Logging ?? Defaults();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Logging configuration unavailable: {exception.Message}");
            return Defaults();
        }
    }

    internal static string DefaultBasePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        return Path.Combine(root, "FLAMORIS", "Cutwork");
    }

    private static LoggingOptions Defaults() => new()
    {
        Level = "debug",
        Categories = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["mcp"] = "info",
            ["mcp.transport"] = "debug",
            ["mcp.auth"] = "warn",
        },
        Outputs =
        [
            new() { Type = "console" },
            new()
            {
                Type = "file",
                Path = "logs/cutwork.log",
                Format = "text",
                Rotation = new() { Enabled = true, MaxFileSizeMb = 20, MaxFiles = 10 },
            },
        ],
    };

    private sealed class ApplicationSettings
    {
        public ApplicationSettings() { }
        public LoggingOptions? Logging { get; set; }
    }
}

internal static class CutworkLog
{
    internal static FlamorisLogger Current { get; set; } = LoggingBootstrap.Create();
}
