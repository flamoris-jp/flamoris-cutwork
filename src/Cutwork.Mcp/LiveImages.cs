using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Mcp;

public sealed record LiveImage(byte[] Png, DocumentRect SourceBounds, DocumentRect Crop,
    int Width, int Height, double DocumentPixelsPerOutputX, double DocumentPixelsPerOutputY);

public static class LiveImages
{
    public static LiveImage Capture(CutworkDocument document, string source, Guid? target,
        DocumentRect? roi, int maxEdge, CancellationToken cancellationToken)
    {
        PartLayer? part = null;
        if (source is "Part" or "Mask") part = document.GetLayer(target ?? throw new LiveException("target_required")) as PartLayer
            ?? throw new LiveException("invalid_target");
        else if (target is not null) throw new LiveException("invalid_target");
        var bounds = part?.Bounds ?? DocumentRect.FromSize(document.Dimensions);
        var crop = roi ?? bounds;
        if (!bounds.Contains(crop) || crop.IsEmpty) throw new LiveException("invalid_roi");
        var (width, height) = Size(crop, maxEdge);
        if ((long)width * height * (source == "Composite" ? Math.Max(1, document.Layers.Count * 2) : 1) > LiveLimits.WorkUnits)
            throw new LiveException("image_work_limit");
        var pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int dy = crop.Y + Math.Min(crop.Height - 1, (int)((y + 0.5) * crop.Height / height));
            for (int x = 0; x < width; x++)
            {
                int dx = crop.X + Math.Min(crop.Width - 1, (int)((x + 0.5) * crop.Width / width));
                var pixel = pixels.AsSpan((y * width + x) * 4, 4);
                if (source == "Composite") CompositeCache.CompositePixel(document, dx, dy, CompositeCache.HoleAt(document, dx, dy), pixel);
                else if (source == "Mask") { pixel.Fill(part!.MaskAt(dx, dy)); pixel[3] = 255; }
                else
                {
                    document.Original.PixelAt(dx, dy).CopyTo(pixel);
                    if (part is not null) pixel[3] = CompositeCache.Multiply(pixel[3], part.MaskAt(dx, dy));
                }
            }
        }
        return Encode(pixels, source == "Composite", bounds, crop, width, height);
    }
    public static LiveImage Mask(DocumentRect bounds, byte[] mask, int maxEdge)
    {
        var (width, height) = Size(bounds, maxEdge);
        var pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int sx = Math.Min(bounds.Width - 1, (int)((x + 0.5) * bounds.Width / width));
            int sy = Math.Min(bounds.Height - 1, (int)((y + 0.5) * bounds.Height / height));
            pixels.AsSpan((y * width + x) * 4, 4).Fill(mask[sy * bounds.Width + sx]);
            pixels[(y * width + x) * 4 + 3] = 255;
        }
        return Encode(pixels, false, bounds, bounds, width, height);
    }
    private static (int, int) Size(DocumentRect crop, int edge)
    {
        if (edge is < 1 or > LiveLimits.PreviewEdge) throw new LiveException("preview_limit");
        double scale = Math.Min(1, (double)edge / Math.Max(crop.Width, crop.Height));
        return (Math.Max(1, (int)Math.Floor(crop.Width * scale)), Math.Max(1, (int)Math.Floor(crop.Height * scale)));
    }
    private static LiveImage Encode(byte[] pixels, bool premultiplied, DocumentRect bounds, DocumentRect crop, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96,
            premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new LimitedOutput(); encoder.Save(output);
        return new(output.ToArray(), bounds, crop, width, height, (double)crop.Width / width, (double)crop.Height / height);
    }
    private sealed class LimitedOutput : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > LiveLimits.PngBytes) throw new LiveException("png_limit");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > LiveLimits.PngBytes) throw new LiveException("png_limit");
            base.Write(buffer);
        }
    }
}
