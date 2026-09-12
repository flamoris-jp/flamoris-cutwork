using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Flamoris.Cutwork.App.Rendering;
using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.App.Controls;

public partial class DocumentCanvas : UserControl
{
    private readonly WriteableBitmapSurface _bitmapSurface = new();
    private EditorSession? _session;
    private ViewportPoint _lastPanPoint;
    private bool _isPanning;
    private bool _autoFit = true;

    public DocumentCanvas()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDpi();
        SizeChanged += (_, _) => OnViewportSizeChanged();
        MouseMove += Canvas_MouseMove;
        MouseLeave += (_, _) => HideCrosshair();
        MouseDown += Canvas_MouseDown;
        MouseUp += Canvas_MouseUp;
        LostMouseCapture += (_, _) => EndPan();
        MouseWheel += Canvas_MouseWheel;
    }

    public event EventHandler<DocumentPointerEventArgs>? PointerDocumentPositionChanged;

    public event EventHandler? ViewportChanged;

    public string EmptyText
    {
        get => EmptyMessage.Text;
        set => EmptyMessage.Text = value;
    }

    public long BitmapGeneration => _bitmapSurface.Generation;

    public double Zoom => _session?.Viewport.Zoom ?? 1.0;

    public void AttachSession(EditorSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public void Present(CutworkDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _bitmapSurface.PresentOriginal(document.Original);
        ImageSurface.Source = _bitmapSurface.Bitmap;
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
        if (_session?.Document is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        var anchor = new ViewportPoint(position.X, position.Y);
        var factor = Math.Pow(1.0015, e.Delta);

        // In Phase 1 both plain and Ctrl+wheel route to viewport zoom. A future
        // tool router can consume plain wheel while preserving this Ctrl route.
        _ = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _autoFit = false;
        _session.Viewport.ZoomAt(anchor, factor);
        ApplyViewport();
        UpdateCrosshair(position);
        e.Handled = true;
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var spaceHeld = Keyboard.IsKeyDown(Key.Space);
        if (e.ChangedButton != MouseButton.Middle && !(e.ChangedButton == MouseButton.Left && spaceHeld))
        {
            return;
        }

        var position = e.GetPosition(this);
        _lastPanPoint = new ViewportPoint(position.X, position.Y);
        _isPanning = true;
        _autoFit = false;
        CaptureMouse();
        Mouse.OverrideCursor = Cursors.Hand;
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (_isPanning && _session is not null)
        {
            var current = new ViewportPoint(position.X, position.Y);
            _session.Viewport.PanBy(current.X - _lastPanPoint.X, current.Y - _lastPanPoint.Y);
            _lastPanPoint = current;
            ApplyViewport();
        }

        UpdateCrosshair(position);
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning && (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left))
        {
            EndPan();
            e.Handled = true;
        }
    }

    private void EndPan()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
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
        ViewportChanged?.Invoke(this, EventArgs.Empty);
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
}

public sealed class DocumentPointerEventArgs(DocumentPoint? position) : EventArgs
{
    public DocumentPoint? Position { get; } = position;
}
