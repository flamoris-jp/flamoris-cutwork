namespace Flamoris.Cutwork.Core;

/// <summary>A live gesture. Dispose/cancel restores touched ROIs; Commit seals one history entry.</summary>
public sealed class EditTransaction : IDisposable
{
    private readonly EditorSession _session;
    private bool _finished;
    private bool _batching;
    private long _retainedBytes;
    internal EditTransaction(EditorSession session, CutworkDocument document)
    {
        _session = session; Document = document;
        SelectionBefore = session.SelectedLayerId;
    }
    internal CutworkDocument Document { get; }
    internal Guid? SelectionBefore { get; }
    internal List<AppliedEdit> Edits { get; } = [];
    internal long RetainedBytes => _retainedBytes;

    public void Apply(EditCommand command)
    {
        if (_finished) throw new ObjectDisposedException(nameof(EditTransaction));
        try
        {
            ArgumentNullException.ThrowIfNull(command);
            var edit = command.Apply(Document);
            if (edit is null) return;
            Edits.Add(edit);
            _retainedBytes = checked(_retainedBytes + edit.RetainedBytes);
            if (_retainedBytes > _session.HistoryBudgetBytes)
                throw new EditException(EditError.HistoryBudgetExceeded);
            if (!_batching)
            {
                edit.Publish(Document);
                _session.NotifyChanged();
            }
        }
        catch
        {
            Cancel();
            throw;
        }
    }
    internal void ApplyBatch(Action apply)
    {
        if (_finished) throw new ObjectDisposedException(nameof(EditTransaction));
        ArgumentNullException.ThrowIfNull(apply);
        if (_batching) throw new InvalidOperationException("Nested edit batches are not supported.");
        var firstEdit = Edits.Count;
        _batching = true;
        try
        {
            apply();
            if (Edits.Count == firstEdit) return;
            PublishAll(Document, Edits.GetRange(firstEdit, Edits.Count - firstEdit));
            _session.NotifyChanged();
        }
        catch
        {
            Cancel();
            throw;
        }
        finally { _batching = false; }
    }
    public void Commit()
    {
        Commit(null);
    }
    public void Commit(Guid? selectionAfter)
    {
        if (_finished) throw new ObjectDisposedException(nameof(EditTransaction));
        if (selectionAfter is { } id) _session.SetTransactionSelection(id);
        _finished = true;
        _session.Finish(this, commit: true);
    }
    public void Cancel()
    {
        if (_finished) return;
        _finished = true;
        for (var i = Edits.Count - 1; i >= 0; i--) Edits[i].Undo();
        if (Edits.Count > 0) PublishAll(Document, Edits);
        _session.Finish(this, commit: false);
    }
    public void Dispose() => Cancel();

    internal static void PublishAll(CutworkDocument document, IReadOnlyList<AppliedEdit> edits)
    {
        var dirty = default(DocumentRect); var holes = default(DocumentRect);
        foreach (var edit in edits) { dirty = dirty.Union(edit.Dirty); holes = holes.Union(edit.Holes); }
        document.Publish(dirty, holes, edits.SelectMany(edit => edit.TouchedLayers()));
    }
}

internal sealed record HistoryEntry(AppliedEdit[] Edits, long Before, long After,
    Guid? SelectionBefore, Guid? SelectionAfter)
{
    internal long RetainedBytes => Edits.Sum(edit => edit.RetainedBytes);
}
