namespace Flamoris.Cutwork.Core;

public readonly record struct DocumentPoint(double X, double Y);

public readonly record struct ViewportPoint(double X, double Y);

public readonly record struct ViewportSize(double Width, double Height);

public readonly record struct ViewportProjection(double ScaleX, double ScaleY, double OffsetX, double OffsetY);

public sealed class ViewportTransform
{
    public const double MinimumZoom = 0.01;
    public const double MaximumZoom = 64.0;

    public double Zoom { get; private set; } = 1.0;

    public double PanX { get; private set; }

    public double PanY { get; private set; }

    public double DpiScaleX { get; private set; } = 1.0;

    public double DpiScaleY { get; private set; } = 1.0;

    public ViewportProjection Projection => new(
        Zoom / DpiScaleX,
        Zoom / DpiScaleY,
        PanX,
        PanY);

    public DocumentPoint ViewportToDocument(ViewportPoint point) => new(
        (point.X - PanX) * DpiScaleX / Zoom,
        (point.Y - PanY) * DpiScaleY / Zoom);

    public ViewportPoint DocumentToViewport(DocumentPoint point) => new(
        PanX + point.X * Zoom / DpiScaleX,
        PanY + point.Y * Zoom / DpiScaleY);

    public void Fit(PixelSize document, ViewportSize viewport, double marginDips = 16.0)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        var availableWidthPixels = Math.Max(1.0, (viewport.Width - marginDips * 2.0) * DpiScaleX);
        var availableHeightPixels = Math.Max(1.0, (viewport.Height - marginDips * 2.0) * DpiScaleY);
        Zoom = ClampZoom(Math.Min(availableWidthPixels / document.Width, availableHeightPixels / document.Height));
        Center(document, viewport);
    }

    public void ActualSize(PixelSize document, ViewportSize viewport)
    {
        Zoom = 1.0;
        Center(document, viewport);
    }

    public void ZoomAt(ViewportPoint anchor, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        var documentAnchor = ViewportToDocument(anchor);
        Zoom = ClampZoom(Zoom * factor);
        PanX = anchor.X - documentAnchor.X * Zoom / DpiScaleX;
        PanY = anchor.Y - documentAnchor.Y * Zoom / DpiScaleY;
    }

    public void PanBy(double deltaX, double deltaY)
    {
        PanX += deltaX;
        PanY += deltaY;
    }

    public void SetDpiScale(double scaleX, double scaleY, ViewportPoint anchor)
    {
        if (!double.IsFinite(scaleX) || scaleX <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scaleX));
        }

        if (!double.IsFinite(scaleY) || scaleY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scaleY));
        }

        var documentAnchor = ViewportToDocument(anchor);
        DpiScaleX = scaleX;
        DpiScaleY = scaleY;
        PanX = anchor.X - documentAnchor.X * Zoom / DpiScaleX;
        PanY = anchor.Y - documentAnchor.Y * Zoom / DpiScaleY;
    }

    public void Reset()
    {
        Zoom = 1.0;
        PanX = 0.0;
        PanY = 0.0;
    }

    private void Center(PixelSize document, ViewportSize viewport)
    {
        var displayWidth = document.Width * Zoom / DpiScaleX;
        var displayHeight = document.Height * Zoom / DpiScaleY;
        PanX = (viewport.Width - displayWidth) / 2.0;
        PanY = (viewport.Height - displayHeight) / 2.0;
    }

    private static double ClampZoom(double value) => Math.Clamp(value, MinimumZoom, MaximumZoom);
}
