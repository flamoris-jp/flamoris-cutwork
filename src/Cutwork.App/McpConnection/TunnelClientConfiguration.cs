using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Flamoris.Cutwork.App.McpConnection;

/// <summary>Exact configuration shape verified for OpenAI tunnel-client v0.0.14.</summary>
public static class TunnelClientConfiguration
{
    private static readonly JsonSerializerOptions YamlScalarOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    public const string ApiKeyEnvironmentVariable = "CONTROL_PLANE_API_KEY";
    public const string CapabilityEnvironmentVariable = "FLAMORIS_MCP_CAPABILITY";
    public const string SupportedVersion = "0.0.14";

    public static string BuildYaml(OpenAiTunnelClientSettings settings, string pipeName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (!Flamoris.Mcp.Core.McpOptions.ValidPipeName(pipeName))
            throw new ArgumentException("Invalid local pipe name.", nameof(pipeName));

        string command = WindowsCommandLine.Quote(settings.BridgeExecutable)
            + " --pipe " + WindowsCommandLine.Quote(pipeName);
        string baseUrl = settings.ControlPlaneBaseUrl.AbsoluteUri.TrimEnd('/');
        return $$"""
            config_version: 1

            control_plane:
              base_url: {{YamlString(baseUrl)}}

            tunnel_id: {{YamlString(settings.TunnelId)}}
            api_key: {{YamlString("env:" + ApiKeyEnvironmentVariable)}}

            health:
              listen_addr: {{YamlString(settings.HealthListenAddress)}}

            open_browser: false

            log:
              level: info
              format: json

            mcp:
              commands:
                - channel: main
                  command: {{YamlString(command)}}
            """;
    }

    private static string YamlString(string value) => JsonSerializer.Serialize(value, YamlScalarOptions);
}

public static class WindowsCommandLine
{
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder(value.Length + 2).Append('"');
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', checked(backslashes * 2 + 1)).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', checked(backslashes * 2)).Append('"');
        return result.ToString();
    }
}
