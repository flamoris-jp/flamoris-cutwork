using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Flamoris.Cutwork.App.Rendering;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.App.Controls;

public partial class DocumentCanvas : UserControl
{
    private readonly WriteableBitmapSurface _bitmapSurface = new();
    private readonly WriteableBitmapSurface _originalSurface = new();
    private CompositeCache? _composite;
    private CutworkDocument? _presentedDocument;
    private bool _refreshQueued;
    private EditorSession? _session;
    private CanvasInputRouter? _inputRouter;
    private PartToolController? _partTool;
    private readonly List<Ellipse> _partFencePoints = [];
    private long _presentedPartMaskRevision = -1;
    private bool _autoFit = true;

    public DocumentCanvas()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDpi();
        SizeChanged += (_, _) => OnViewportSizeChanged();
        MouseMove += Canvas_MouseMove;
        MouseLeave += (_, _) => ClearPointerOverlay();
        MouseDown += Canvas_MouseDown;
        MouseUp += Canvas_MouseUp;
        LostMouseCapture += (_, _) => ApplyInputEffects(_inputRouter?.LostPointerCapture() ?? CanvasInputEffects.None);
        MouseWheel += Canvas_MouseWheel;
        PreviewKeyDown += Canvas_PreviewKeyDown;
    }

    public event EventHandler<DocumentPointerEventArgs>? PointerDocumentPositionChanged;

    public event EventHandler? ViewportChanged;

    public event EventHandler<CanvasEditRejectedEventArgs>? EditRejected;

    public string EmptyText
    {
        get => EmptyMessage.Text;
        set => EmptyMessage.Text = value;
    }

    public long BitmapGeneration => _bitmapSurface.Generation;

    public double Zoom => _session?.Viewport.Zoom ?? 1.0;

    public void AttachSession(EditorSession session)
    {
        if (_session is not null) _session.Changed -= SessionChanged;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.Changed += SessionChanged;
    }

    public void AttachInputRouter(CanvasInputRouter router, PartToolController partTool)
    {
        if (_partTool is not null) _partTool.Changed -= PartToolChanged;
        _inputRouter = router ?? throw new ArgumentNullException(nameof(router));
        _partTool = partTool ?? throw new ArgumentNullException(nameof(partTool));
        _partTool.Changed += PartToolChanged;
        RenderPartOverlay();
    }

    private void PartToolChanged(object? sender, EventArgs e) => RenderPartOverlay();

    private void SessionChanged(object? sender, EventArgs e)
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _refreshQueued = false;
            RefreshPreview();
        }));
    }

    public void RefreshPreview()
    {
        if (_session?.Document is null || !ReferenceEquals(_session.Document, _presentedDocument)) return;
        if (_session.PreviewSource == PreviewSource.Original)
        {
            if (_originalSurface.Bitmap is null) _originalSurface.PresentOriginal(_presentedDocument.Original);
            ImageSurface.Source = _originalSurface.Bitmap;
        }
        else
        {
            var update = _composite?.RenderPending();
            if (update is not null) _bitmapSurface.Apply(update);
            ImageSurface.Source = _bitmapSurface.Bitmap;
        }
    }

    public void Present(CutworkDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _composite?.Dispose();
        _presentedDocument = document;
        _composite = new CompositeCache(document);
        _originalSurface.Clear();
        _bitmapSurface.Initialize(document.Dimensions);
        RefreshPreview();
        ImageSurface.Width = document.Dimensions.Width;
        ImageSurface.Height = document.Dimensions.Height;
        EmptyMessage.Visibility = System.Windows.Visibility.Collapsed;
        _autoFit = true;
        Fit();
    }

    public void Fit()
    {
        if (_session?.Document is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        _autoFit = true;
        _session.Viewport.Fit(_session.Document.Dimensions, new ViewportSize(ActualWidth, ActualHeight));
        ApplyViewport();
    }

    public void ActualSize()
    {
        if (_session?.Document is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        _autoFit = false;
        _session.Viewport.ActualSize(_session.Document.Dimensions, new ViewportSize(ActualWidth, ActualHeight));
        ApplyViewport();
    }

    public void RefreshDpi()
    {
        if (_session is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var center = new ViewportPoint(ActualWidth / 2.0, ActualHeight / 2.0);
        _session.Viewport.SetDpiScale(dpi.DpiScaleX, dpi.DpiScaleY, center);
        if (_autoFit)
        {
            Fit();
        }
        else
        {
            ApplyViewport();
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RefreshDpi();
    }

    private void OnViewportSizeChanged()
    {
        if (_autoFit)
        {
            Fit();
        }
    }

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var position = e.GetPosition(this);
        var effects = _inputRouter?.Wheel(
            new ViewportPoint(position.X, position.Y), e.Delta, CurrentModifiers()) ?? CanvasInputEffects.None;
        ApplyInputEffects(effects);
        UpdateCrosshair(position);
        e.Handled = effects.HasFlag(CanvasInputEffects.Handled);
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var position = e.GetPosition(this);
        var effects = _inputRouter?.PointerDown(new(
            new ViewportPoint(position.X, position.Y), MapButton(e.ChangedButton), e.ClickCount, CurrentModifiers()))
            ?? CanvasInputEffects.None;
        ApplyInputEffects(effects);
        e.Handled = effects.HasFlag(CanvasInputEffects.Handled);
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        var effects = _inputRouter?.PointerMove(
            new ViewportPoint(position.X, position.Y), CurrentModifiers()) ?? CanvasInputEffects.None;
        ApplyInputEffects(effects);
        UpdateCrosshair(position);
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(this);
        var effects = _inputRouter?.PointerUp(new(
            new ViewportPoint(position.X, position.Y), MapButton(e.ChangedButton), e.ClickCount, CurrentModifiers()))
            ?? CanvasInputEffects.None;
        ApplyInputEffects(effects);
        e.Handled = effects.HasFlag(CanvasInputEffects.Handled);
    }

    private void Canvas_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.Enter => CanvasToolKey.Enter,
            Key.Escape => CanvasToolKey.Escape,
            _ => (CanvasToolKey?)null,
        };
        if (key is null) return;
        CanvasInputEffects effects;
        try { effects = _inputRouter?.KeyDown(key.Value, CurrentModifiers()) ?? CanvasInputEffects.None; }
        catch (EditException exception)
        {
            EditRejected?.Invoke(this, new CanvasEditRejectedEventArgs(exception));
            effects = CanvasInputEffects.Handled;
        }
        ApplyInputEffects(effects);
        e.Handled = effects.HasFlag(CanvasInputEffects.Handled);
    }

    private void ApplyInputEffects(CanvasInputEffects effects)
    {
        if (effects.HasFlag(CanvasInputEffects.ViewportChanged))
        {
            _autoFit = false;
            ApplyViewport();
        }
        if (effects.HasFlag(CanvasInputEffects.CapturePointer))
        {
            CaptureMouse();
            Mouse.OverrideCursor = Cursors.Hand;
        }
        if (effects.HasFlag(CanvasInputEffects.ReleasePointer))
        {
            if (IsMouseCaptured) ReleaseMouseCapture();
            Mouse.OverrideCursor = null;
        }
    }

    private void ApplyViewport()
    {
        if (_session is null)
        {
            return;
        }

        var projection = _session.Viewport.Projection;
        ImageSurface.RenderTransform = new MatrixTransform(
            projection.ScaleX,
            0,
            0,
            projection.ScaleY,
            projection.OffsetX,
            projection.OffsetY);
        RenderPartOverlay();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenderPartOverlay()
    {
        foreach (var point in _partFencePoints) OverlaySurface.Children.Remove(point);
        _partFencePoints.Clear();
        PartFenceLine.Points.Clear();
        PartFenceLine.Visibility = Visibility.Collapsed;
        PartMaskOverlay.Visibility = Visibility.Collapsed;
        if (_session is null || _partTool is null) return;

        var snapshot = _partTool.Snapshot();
        foreach (var documentPoint in snapshot.Fence)
        {
            var point = _session.Viewport.DocumentToViewport(documentPoint);
            PartFenceLine.Points.Add(new Point(point.X, point.Y));
            var marker = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(235, 70, 170)),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(marker, point.X - 3.5);
            Canvas.SetTop(marker, point.Y - 3.5);
            OverlaySurface.Children.Add(marker);
            _partFencePoints.Add(marker);
        }
        if (snapshot.State == PartToolState.DrawingFence && snapshot.HoverPoint is { } hover)
        {
            var point = _session.Viewport.DocumentToViewport(hover);
            PartFenceLine.Points.Add(new Point(point.X, point.Y));
        }
        if (snapshot.Fence.Count > 0)
        {
            if (snapshot.State == PartToolState.FittingPreview)
            {
                var first = _session.Viewport.DocumentToViewport(snapshot.Fence[0]);
                PartFenceLine.Points.Add(new Point(first.X, first.Y));
            }
            PartFenceLine.Visibility = Visibility.Visible;
        }

        if (snapshot.State != PartToolState.FittingPreview || snapshot.Mask.IsEmpty || snapshot.MaskBounds.IsEmpty)
        {
            PartMaskOverlay.Source = null;
            _presentedPartMaskRevision = -1;
            return;
        }
        if (_presentedPartMaskRevision != snapshot.MaskRevision)
        {
            var mask = snapshot.Mask.Span;
            var overlayPixels = new byte[checked(mask.Length * 4)];
            for (var index = 0; index < mask.Length; index++)
            {
                var alpha = (byte)(mask[index] * 88 / 255);
                overlayPixels[index * 4] = (byte)(170 * alpha / 255);
                overlayPixels[index * 4 + 1] = (byte)(70 * alpha / 255);
                overlayPixels[index * 4 + 2] = (byte)(235 * alpha / 255);
                overlayPixels[index * 4 + 3] = alpha;
            }
            var bitmap = new WriteableBitmap(snapshot.MaskBounds.Width, snapshot.MaskBounds.Height, 96, 96,
                PixelFormats.Pbgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, snapshot.MaskBounds.Width, snapshot.MaskBounds.Height),
                overlayPixels, snapshot.MaskBounds.Width * 4, 0);
            bitmap.Freeze();
            PartMaskOverlay.Source = bitmap;
            PartMaskOverlay.Width = snapshot.MaskBounds.Width;
            PartMaskOverlay.Height = snapshot.MaskBounds.Height;
            _presentedPartMaskRevision = snapshot.MaskRevision;
        }
        var projection = _session.Viewport.Projection;
        PartMaskOverlay.RenderTransform = new MatrixTransform(
            projection.ScaleX, 0, 0, projection.ScaleY,
            projection.OffsetX + snapshot.MaskBounds.X * projection.ScaleX,
            projection.OffsetY + snapshot.MaskBounds.Y * projection.ScaleY);
        PartMaskOverlay.Visibility = Visibility.Visible;
    }

    private static CanvasPointerButton MapButton(MouseButton button) => button switch
    {
        MouseButton.Left => CanvasPointerButton.Left,
        MouseButton.Middle => CanvasPointerButton.Middle,
        MouseButton.Right => CanvasPointerButton.Right,
        _ => CanvasPointerButton.None,
    };

    private static CanvasModifiers CurrentModifiers()
    {
        var result = CanvasModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) result |= CanvasModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) result |= CanvasModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) result |= CanvasModifiers.Alt;
        if (Keyboard.IsKeyDown(Key.Space)) result |= CanvasModifiers.Space;
        return result;
    }

    private void UpdateCrosshair(Point position)
    {
        if (_session?.Document is null)
        {
            HideCrosshair();
            return;
        }

        var documentPoint = _session.Viewport.ViewportToDocument(new ViewportPoint(position.X, position.Y));
        var dimensions = _session.Document.Dimensions;
        var inside = documentPoint.X >= 0
            && documentPoint.Y >= 0
            && documentPoint.X < dimensions.Width
            && documentPoint.Y < dimensions.Height;
        if (!inside)
        {
            HideCrosshair();
            PointerDocumentPositionChanged?.Invoke(this, new DocumentPointerEventArgs(null));
            return;
        }

        const double halfSize = 7.0;
        CrosshairHorizontal.X1 = position.X - halfSize;
        CrosshairHorizontal.X2 = position.X + halfSize;
        CrosshairHorizontal.Y1 = position.Y;
        CrosshairHorizontal.Y2 = position.Y;
        CrosshairVertical.X1 = position.X;
        CrosshairVertical.X2 = position.X;
        CrosshairVertical.Y1 = position.Y - halfSize;
        CrosshairVertical.Y2 = position.Y + halfSize;
        CrosshairHorizontal.Visibility = Visibility.Visible;
        CrosshairVertical.Visibility = Visibility.Visible;
        PointerDocumentPositionChanged?.Invoke(this, new DocumentPointerEventArgs(documentPoint));
    }

    private void HideCrosshair()
    {
        CrosshairHorizontal.Visibility = Visibility.Collapsed;
        CrosshairVertical.Visibility = Visibility.Collapsed;
    }

    private void ClearPointerOverlay()
    {
        HideCrosshair();
        PointerDocumentPositionChanged?.Invoke(this, new DocumentPointerEventArgs(null));
    }
}

public sealed class DocumentPointerEventArgs(DocumentPoint? position) : EventArgs
{
    public DocumentPoint? Position { get; } = position;
}

public sealed class CanvasEditRejectedEventArgs(EditException exception) : EventArgs
{
    public EditException Exception { get; } = exception;
}
