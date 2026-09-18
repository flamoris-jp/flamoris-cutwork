namespace Flamoris.Cutwork.Core;

public enum PreviewSource
{
    Original,
    Composite,
}

public sealed class EditorSession
{
    private readonly List<HistoryEntry> _undo = [];
    private readonly List<HistoryEntry> _redo = [];
    private EditTransaction? _active;
    private long _nextStateRevision;

    public EditorSession(long historyBudgetBytes = 128L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyBudgetBytes);
        HistoryBudgetBytes = historyBudgetBytes;
    }

    public ViewportTransform Viewport { get; } = new();

    public CutworkDocument? Document { get; private set; }

    public PreviewSource PreviewSource { get; private set; } = PreviewSource.Composite;

    public Guid? SelectedLayerId { get; private set; }
    public long CurrentRevision { get; private set; }
    public long SavedRevision { get; private set; }
    public bool IsDirty => CurrentRevision != SavedRevision || _active?.Edits.Count > 0;
    public bool CanUndo => _active is null && _undo.Count > 0;
    public bool CanRedo => _active is null && _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public long HistoryBudgetBytes { get; }
    public long HistoryBytes => _undo.Sum(entry => entry.RetainedBytes) + _redo.Sum(entry => entry.RetainedBytes);
    public event EventHandler? Changed;
    public event EventHandler? DocumentReplacing;
    public string DocumentToken { get; private set; } = Guid.NewGuid().ToString("N");
    public bool HasActiveTransaction => _active is not null;

    public void Open(CutworkDocument document)
    {
        EnsureIdle();
        ArgumentNullException.ThrowIfNull(document);
        document.Claim(this);
        DocumentReplacing?.Invoke(this, EventArgs.Empty);
        DocumentToken = Guid.NewGuid().ToString("N");
        if (!ReferenceEquals(Document, document)) Document?.Release(this);
        Document = document;
        PreviewSource = PreviewSource.Composite;
        Viewport.Reset();
        _undo.Clear(); _redo.Clear();
        CurrentRevision = SavedRevision = _nextStateRevision = 0;
        SelectedLayerId = document.Base.Id;
        NotifyChanged();
    }

    public void SetPreviewSource(PreviewSource source)
    {
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        PreviewSource = source;
        NotifyChanged();
    }

    public void SelectLayer(Guid id)
    {
        RequireDocument().GetLayer(id);
        SelectedLayerId = id;
        NotifyChanged();
    }
    public void MarkSaved()
    {
        EnsureIdle(); RequireDocument();
        SavedRevision = CurrentRevision;
        NotifyChanged();
    }
    public EditTransaction BeginTransaction()
    {
        EnsureIdle();
        _active = new EditTransaction(this, RequireDocument());
        NotifyChanged();
        return _active;
    }
    public void Execute(params EditCommand[] commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        using var transaction = BeginTransaction();
        foreach (var command in commands) transaction.Apply(command);
        transaction.Commit();
    }
    public void Undo()
    {
        EnsureIdle();
        if (_undo.Count == 0) return;
        var document = RequireDocument();
        var entry = _undo[^1];
        for (var i = entry.Edits.Length - 1; i >= 0; i--) entry.Edits[i].Undo();
        _undo.RemoveAt(_undo.Count - 1); _redo.Add(entry);
        CurrentRevision = entry.Before;
        SelectedLayerId = entry.SelectionBefore;
        EditTransaction.PublishAll(document, entry.Edits);
        NotifyChanged();
    }
    public void Redo()
    {
        EnsureIdle();
        if (_redo.Count == 0) return;
        var document = RequireDocument();
        var entry = _redo[^1];
        foreach (var edit in entry.Edits) edit.Redo();
        _redo.RemoveAt(_redo.Count - 1); _undo.Add(entry);
        CurrentRevision = entry.After;
        SelectedLayerId = entry.SelectionAfter;
        EditTransaction.PublishAll(document, entry.Edits);
        NotifyChanged();
    }
    internal void Finish(EditTransaction transaction, bool commit)
    {
        if (!ReferenceEquals(_active, transaction)) throw new EditException(EditError.TransactionActive);
        _active = null;
        if (commit && transaction.Edits.Count > 0)
        {
            NormalizeSelection();
            var entry = new HistoryEntry(transaction.Edits.ToArray(), CurrentRevision, ++_nextStateRevision,
                transaction.SelectionBefore, SelectedLayerId);
            CurrentRevision = entry.After;
            _redo.Clear(); _undo.Add(entry);
            while (_undo.Count > 0 && HistoryBytes > HistoryBudgetBytes) _undo.RemoveAt(0);
        }
        else if (!commit) SelectedLayerId = transaction.SelectionBefore;
        NotifyChanged();
    }
    internal void NotifyChanged()
    {
        NormalizeSelection();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    internal void SetTransactionSelection(Guid id)
    {
        if (_active is null) throw new EditException(EditError.TransactionActive);
        RequireDocument().GetLayer(id);
        SelectedLayerId = id;
    }
    private void NormalizeSelection()
    {
        if (Document is not null && !Document.Layers.Any(layer => layer.Id == SelectedLayerId))
            SelectedLayerId = Document.Base.Id;
    }
    private void EnsureIdle()
    {
        if (_active is not null) throw new EditException(EditError.TransactionActive);
    }
    private CutworkDocument RequireDocument() => Document ?? throw new EditException(EditError.NoDocument);
}
