using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Mcp;
using Flamoris.Logging;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Tests;

internal sealed class McpCoreHarness : IDisposable
{
    private McpCoreHarness(EditorSession session, McpPermission permission, Func<bool> busy,
        Func<Action, CancellationToken, Task> dispatch, Action<bool> editing, FlamorisLogger logger,
        IPartBoundaryFitter? partFitter, McpOptions? options, TimeProvider? timeProvider)
    {
        Session = session;
        Host = new(session, busy, dispatch);
        Editor = new(session, permission, busy, editing, logger, partFitter,
            currentGrant: () => Grant, timeProvider: timeProvider);
        var configured = (options ?? new McpOptions()) with
        {
            Enabled = true,
            Permission = permission,
            PipeName = options?.PipeName ?? "flamoris-test-" + Guid.NewGuid().ToString("N"),
            MaxConcurrentRequests = 1,
        };
        Boundary = new(Host, CutworkMcpTools.Create(Editor), configured, new McpDiagnostics(logger));
    }

    public EditorSession Session { get; }
    public CutworkMcpHost Host { get; }
    public LiveEditor Editor { get; }
    public McpBoundary Boundary { get; }
    public CapabilityGrant Grant { get; private set; } = null!;

    public static async Task<McpCoreHarness> CreateAsync(EditorSession session,
        McpPermission permission = McpPermission.Edit, Func<bool>? busy = null,
        Func<Action, CancellationToken, Task>? dispatch = null, Action<bool>? editing = null,
        FlamorisLogger? logger = null, IPartBoundaryFitter? partFitter = null,
        McpOptions? options = null, TimeProvider? timeProvider = null)
    {
        var harness = new McpCoreHarness(session, permission, busy ?? (() => false),
            dispatch ?? ((action, token) =>
            {
                token.ThrowIfCancellationRequested();
                action();
                return Task.CompletedTask;
            }), editing ?? (_ => { }), logger ?? FlamorisLogger.Create(), partFitter, options, timeProvider);
        harness.Grant = await harness.Boundary.EnableAsync(permission);
        return harness;
    }

    public Task<McpResult> CallAsync(string name, object input, long? expectedRevision = null,
        string? runtimeId = null, string? documentToken = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = Host.Snapshot;
        var guard = new RequestGuard(runtimeId ?? snapshot.RuntimeId,
            documentToken ?? snapshot.DocumentToken,
            LiveSchema.IsEdit(name) ? expectedRevision ?? snapshot.Revision : expectedRevision);
        return Boundary.InvokeAsync(Grant, name, JsonSerializer.SerializeToElement(input), guard,
            cancellationToken);
    }

    public async Task ReenableAsync(McpPermission permission = McpPermission.Edit)
    {
        Grant.Dispose();
        Grant = await Boundary.EnableAsync(permission);
    }

    public void Dispose()
    {
        Boundary.Dispose();
        Grant?.Dispose();
        Host.Dispose();
    }
}
