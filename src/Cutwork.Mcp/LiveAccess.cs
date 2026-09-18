using Flamoris.Cutwork.Core;

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
    public LiveAccess(EditorSession session, LivePermission permission)
    {
        this.session = session;
        document = session.Document ?? throw new LiveException("no_document");
        DocumentToken = session.DocumentToken;
        Permission = permission;
        session.DocumentReplacing += Replacing;
        session.Changed += Changed;
    }
    public string DocumentToken { get; }
    public LivePermission Permission { get; }
    public CancellationToken Token => revoked.Token;
    public bool IsActive => active;
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
        if (edit && Permission != LivePermission.Edit) throw new LiveException("read_only");
    }
    public void Revoke()
    {
        if (!active) return;
        active = false;
        session.DocumentReplacing -= Replacing;
        session.Changed -= Changed;
        revoked.Cancel();
    }
    public void Dispose() => Revoke();
}
