using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.App.McpConnection;

/// <summary>
/// Cutwork-owned adaptation of the Core 1.1 reference controller. Host mutations that
/// succeeded are reconciled even when caller cancellation wins at the boundary.
/// </summary>
public sealed class McpConnectionController(ManagedConnectionLifecycle lifecycle)
{
    public ManagedConnectionStatus Current => lifecycle.Current;
    public event Action? Changed
    {
        add => lifecycle.Changed += value;
        remove => lifecycle.Changed -= value;
    }

    public async Task EnableAsync(Func<CancellationToken, Task> issueGrant,
        bool startManagedHelper, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issueGrant);
        await issueGrant(cancellationToken);
        await lifecycle.MarkEnabledAsync(CancellationToken.None);
        if (startManagedHelper) await lifecycle.StartAsync(CancellationToken.None);
    }

    public Task StartManagedConnectionAsync(CancellationToken cancellationToken = default) =>
        lifecycle.StartAsync(cancellationToken);

    public async Task RefreshAfterGrantRotationAsync(Func<CancellationToken, Task> rotateGrant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rotateGrant);
        await rotateGrant(cancellationToken);
        await lifecycle.RefreshAsync(CancellationToken.None);
    }

    public Task StopManagedConnectionAsync(CancellationToken cancellationToken = default) =>
        lifecycle.StopAsync(cancellationToken);

    public Task DisableAsync(CancellationToken cancellationToken = default) =>
        lifecycle.DisableAsync(cancellationToken);

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        lifecycle.ShutdownAsync(cancellationToken);

    public void ProjectExternalClient(bool connected) =>
        lifecycle.SetExternalClientConnected(connected);
}
