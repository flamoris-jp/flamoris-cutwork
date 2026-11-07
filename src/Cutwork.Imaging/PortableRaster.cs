#if !WINDOWS
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging.Persistence;
using SkiaSharp;

namespace Flamoris.Cutwork.Imaging;

// Skia owns PNG/JPEG compression and decoding; Cutwork owns straight BGRA/Gray8 semantics.
internal static class PortableRaster
{
    internal static OriginalAsset Import(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024 * 1024) throw new ArgumentException("Image input limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new ArgumentException("Invalid PNG/JPEG.");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg))
            throw new ArgumentException("Expected PNG/JPEG.");
        var size = new PixelSize(codec.Info.Width, codec.Info.Height);
        CheckSize(size);
        if (codec.EncodedFormat == SKEncodedImageFormat.Png) ValidatePng(bytes);
        var pixels = Pixels(codec, size, gray: false);
        return new OriginalAsset(Path.GetFileName(path), size, checked(size.Width * 4), pixels);
    }

    internal static byte[] Decode(ReadOnlySpan<byte> png, PixelSize size, bool gray)
    {
        CheckSize(size);
        ValidatePng(png);
        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data) ?? throw new ArgumentException("Invalid PNG.");
        if (codec.Info.Width != size.Width || codec.Info.Height != size.Height)
            throw new ArgumentException("PNG dimensions differ.");
        return Pixels(codec, size, gray);
    }

    private static byte[] Pixels(SKCodec codec, PixelSize size, bool gray)
    {
        var info = new SKImageInfo(size.Width, size.Height,
            gray ? SKColorType.Gray8 : SKColorType.Bgra8888, gray ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
        var pixels = new byte[checked(size.Width * size.Height * (gray ? 1 : 4))];
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            if (codec.GetPixels(info, pinned.AddrOfPinnedObject()) != SKCodecResult.Success)
                throw new ArgumentException("Invalid image pixels.");
        }
        finally { pinned.Free(); }
        return pixels;
    }

    internal static byte[] Encode(PixelSize size, ReadOnlySpan<byte> pixels, bool gray, bool premultiplied = false)
    {
        CheckSize(size);
        if (pixels.Length != checked(size.Width * size.Height * (gray ? 1 : 4)))
            throw new ArgumentException(nameof(pixels));
        var straight = pixels.ToArray();
        if (premultiplied && !gray)
            for (var i = 0; i < straight.Length; i += 4)
            {
                var alpha = straight[i + 3];
                for (var c = 0; c < 3; c++)
                    straight[i + c] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, (straight[i + c] * 255 + alpha / 2) / alpha);
            }
        var info = new SKImageInfo(size.Width, size.Height,
            gray ? SKColorType.Gray8 : SKColorType.Bgra8888, gray ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        for (var y = 0; y < size.Height; y++)
            Marshal.Copy(straight, y * size.Width * (gray ? 1 : 4),
                bitmap.GetPixels() + y * bitmap.RowBytes, size.Width * (gray ? 1 : 4));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("PNG encoding failed.");
        return encoded.ToArray();
    }

    private static void CheckSize(PixelSize size)
    {
        if (size.Width < 1 || size.Height < 1 || size.Width > FlimgArchiveCodec.MaximumDimension
            || size.Height > FlimgArchiveCodec.MaximumDimension || (long)size.Width * size.Height > FlimgArchiveCodec.MaximumPixels)
            throw new ArgumentException("Image dimensions exceed limits.");
    }

    private static void ValidatePng(ReadOnlySpan<byte> png)
    {
        if (png.Length < 33 || !png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new ArgumentException("Invalid PNG signature.");
        int offset = 8;
        bool data = false, end = false;
        while (offset < png.Length)
        {
            if (png.Length - offset < 12) throw new ArgumentException("Truncated PNG.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
            if (length > png.Length - offset - 12) throw new ArgumentException("Truncated PNG chunk.");
            var chunk = png.Slice(offset + 4, checked((int)length + 4));
            uint crc = uint.MaxValue;
            foreach (byte b in chunk)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset + 8 + (int)length, 4)))
                throw new ArgumentException("PNG CRC mismatch.");
            var type = chunk[..4];
            if (offset == 8 && (!type.SequenceEqual("IHDR"u8) || length != 13)) throw new ArgumentException("Missing IHDR.");
            if (type.SequenceEqual("acTL"u8)) throw new ArgumentException("Animated PNG is not a still image.");
            if (type.SequenceEqual("IDAT"u8)) data = true;
            offset += checked((int)length + 12);
            if (type.SequenceEqual("IEND"u8)) { end = true; if (length != 0 || offset != png.Length) throw new ArgumentException("Invalid PNG end."); }
        }
        if (!data || !end) throw new ArgumentException("Incomplete PNG.");
    }
}
#endif
