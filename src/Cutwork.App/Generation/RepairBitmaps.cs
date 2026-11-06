using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.App.Generation;

internal static class RepairBitmaps
{
    internal static BitmapSource Bitmap(byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze(); return bitmap;
    }

    internal static byte[] Resize(byte[] pixels, int width, int height, int targetWidth, int targetHeight)
    {
        // One explicit nearest-pixel affine map in both directions; no implicit crop.
        var output = new byte[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++)
        for (int x = 0; x < targetWidth; x++)
        {
            int sx = Math.Min(width - 1, (int)((x + .5) * width / targetWidth));
            int sy = Math.Min(height - 1, (int)((y + .5) * height / targetHeight));
            pixels.AsSpan((sy * width + sx) * 4, 4).CopyTo(output.AsSpan((y * targetWidth + x) * 4, 4));
        }
        return output;
    }

    internal static byte[] Png(byte[] pixels, int width, int height)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Bitmap(pixels, width, height)));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    internal static byte[] Decode(byte[] png, int width, int height)
    {
        if (png.Length is < 33 or > 8388608 || !png.AsSpan(0, 8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})
            || !png.AsSpan(12,4).SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16,4)) != width
            || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20,4)) != height
            || (long)width * height > 1048576)
            throw new InvalidOperationException("repair_dimensions");
        using var input = new MemoryStream(png, false);
        var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != width || decoder.Frames[0].PixelHeight != height)
            throw new InvalidOperationException("repair_dimensions");
        var image = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(width * height * 4)]; image.CopyPixels(pixels, width * 4, 0); return pixels;
    }

    internal static byte[] Preview(CutworkDocument doc, GeneratedRepair request, RepairLayer? candidate)
    {
        var r = request.Region;
        if ((long)r.Width * r.Height * Math.Max(1, doc.Layers.Count) > 8000000)
            throw new InvalidOperationException("repair_region_limit");
        var pixels = new byte[r.Width * r.Height * 4];
        for (int y = r.Y; y < r.Bottom; y++)
        for (int x = r.X; x < r.Right; x++)
        {
            var pixel = pixels.AsSpan(((y-r.Y)*r.Width+x-r.X)*4,4);
            CompositeCache.CompositePixel(doc,x,y,CompositeCache.HoleAt(doc,x,y),pixel,request.PartId,candidate);
            if (pixel[3] > 0) for(int c=0;c<3;c++) pixel[c]=(byte)Math.Min(255,(pixel[c]*255+pixel[3]/2)/pixel[3]);
        }
        return pixels;
    }
}
