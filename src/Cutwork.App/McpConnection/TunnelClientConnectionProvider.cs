using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.App.McpConnection;

public sealed class TunnelClientConnectionProvider(
    OpenAiTunnelClientSettings settings,
    IMcpConnectionMaterialSource materialSource,
    IProviderCredentialSource credentialSource,
    TunnelClientProcessHost processHost) : IMcpConnectionProvider
{
    public string Id => "openai-tunnel-client-v0.0.14";

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        McpConnectionMaterial material = materialSource.GetCurrent();
        material.Validate();
        string apiKey = await credentialSource.GetControlPlaneApiKeyAsync(cancellationToken);
        await processHost.StartAsync(settings, material, apiKey, cancellationToken);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        await processHost.StopAsync(CancellationToken.None);
        await StartAsync(cancellationToken);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken) =>
        processHost.StopAsync(cancellationToken);
}
