namespace Flamoris.Cutwork.Core;

public enum PreviewSource
{
    Original,
    Composite,
}

public sealed class EditorSession
{
    public CutworkDocument? Document { get; private set; }

    public PreviewSource PreviewSource { get; private set; } = PreviewSource.Composite;

    public void Open(CutworkDocument document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        PreviewSource = PreviewSource.Composite;
    }

    public void SetPreviewSource(PreviewSource source) => PreviewSource = source;
}
