using Flamoris.Cutwork.Core;
using Flamoris.Logging;

namespace Flamoris.Cutwork.Mcp;

public enum LivePermission { ReadOnly, Edit }
public sealed class LiveException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Transient grant, never a document/history authority. Access on the UI dispatcher.</summary>
public sealed class LiveAccess : IDisposable
{
    private readonly EditorSession session;
    private readonly CutworkDocument document;
    private readonly CancellationTokenSource revoked = new();
    private bool active = true;
    public LiveAccess(EditorSession session, LivePermission permission, FlamorisLogger? logger = null)
    {
        this.session = session;
        document = session.Document ?? throw new LiveException("no_document");
        DocumentToken = session.DocumentToken;
        Permission = permission;
        Logger = logger;
        session.DocumentReplacing += Replacing;
        session.Changed += Changed;
        Logger?.Info("mcp.session", "Session access granted", new Dictionary<string, object?>
        {
            ["permission"] = Permission.ToString(),
            ["documentToken"] = DocumentToken,
        });
    }
    public string DocumentToken { get; }
    public LivePermission Permission { get; }
    public CancellationToken Token => revoked.Token;
    public bool IsActive => active;
    internal FlamorisLogger? Logger { get; }
    private void Replacing(object? sender, EventArgs e) => Revoke();
    private void Changed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(session.Document, document) || session.DocumentToken != DocumentToken) Revoke();
    }
    public void Demand(bool edit = false, CancellationToken cancellationToken = default)
    {
        Changed(null, EventArgs.Empty);
        if (!active) throw new LiveException("revoked");
        cancellationToken.ThrowIfCancellationRequested();
        if (edit && Permission != LivePermission.Edit)
        {
            Logger?.Warn("mcp.auth", "Edit permission denied", new Dictionary<string, object?>
            {
                ["permission"] = Permission.ToString(),
                ["documentToken"] = DocumentToken,
            });
            throw new LiveException("read_only");
        }
    }
    public void Revoke()
    {
        if (!active) return;
        active = false;
        session.DocumentReplacing -= Replacing;
        session.Changed -= Changed;
        revoked.Cancel();
        Logger?.Info("mcp.session", "Session access revoked", new Dictionary<string, object?>
        {
            ["permission"] = Permission.ToString(),
            ["documentToken"] = DocumentToken,
        });
    }
    public void Dispose() => Revoke();
}
