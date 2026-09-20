using Flamoris.Cutwork.Core;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Mcp;

/// <summary>
/// Projects the existing Cutwork session onto MCP Core. It never owns or replaces
/// the document, session, transaction or history.
/// </summary>
public sealed class CutworkMcpHost : IMcpHost, IDisposable
{
    private readonly EditorSession session;
    private readonly Func<bool> humanBusy;
    private readonly Func<Action, CancellationToken, Task> dispatch;
    private readonly string runtimeId = Guid.NewGuid().ToString("N");
    private bool shuttingDown;

    public CutworkMcpHost(EditorSession session, Func<bool> humanBusy,
        Func<Action, CancellationToken, Task> dispatch)
    {
        this.session = session;
        this.humanBusy = humanBusy;
        this.dispatch = dispatch;
        session.DocumentReplacing += DocumentReplacing;
    }

    public HostSnapshot Snapshot
    {
        get
        {
            var document = session.Document;
            return new("flamoris.cutwork", "0.1.0", runtimeId,
                document is null ? "" : session.DocumentToken,
                document?.Revision ?? 0,
                Available: !shuttingDown && document is not null,
                HumanEditing: session.HasActiveTransaction || humanBusy());
        }
    }

    public event Action? Invalidating;

    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        T result = default!;
        await dispatch(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = action();
        }, cancellationToken);
        return result;
    }

    private void DocumentReplacing(object? sender, EventArgs e) => PublishInvalidating();

    public void Shutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        PublishInvalidating();
    }

    private void PublishInvalidating()
    {
        var handlers = Invalidating;
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch { /* a boundary observer cannot block replacement */ }
    }

    public void Dispose()
    {
        Shutdown();
        session.DocumentReplacing -= DocumentReplacing;
    }
}
