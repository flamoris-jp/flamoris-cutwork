using System.Windows.Controls;
using Flamoris.Cutwork.App.Rendering;
using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.App.Controls;

public partial class DocumentCanvas : UserControl
{
    private readonly WriteableBitmapSurface _bitmapSurface = new();

    public DocumentCanvas()
    {
        InitializeComponent();
    }

    public string EmptyText
    {
        get => EmptyMessage.Text;
        set => EmptyMessage.Text = value;
    }

    public long BitmapGeneration => _bitmapSurface.Generation;

    public void Present(CutworkDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _bitmapSurface.PresentOriginal(document.Original);
        ImageSurface.Source = _bitmapSurface.Bitmap;
        EmptyMessage.Visibility = System.Windows.Visibility.Collapsed;
    }
}
