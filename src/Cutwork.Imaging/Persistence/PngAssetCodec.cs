using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging.Persistence;

internal static class PngAssetCodec
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    internal static byte[] EncodeBgra32(PixelSize size, ReadOnlySpan<byte> pixels, bool premultiplied = false)
    {
        var stride = checked(size.Width * 4);
        if (pixels.Length != checked(stride * size.Height)) throw new ArgumentException(nameof(pixels));
        return Encode(size, pixels.ToArray(), stride,
            premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgra32);
    }

    internal static byte[] EncodeGray8(PixelSize size, ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != checked(size.Width * size.Height)) throw new ArgumentException(nameof(pixels));
        return Encode(size, pixels.ToArray(), size.Width, PixelFormats.Gray8);
    }

    internal static byte[] DecodeBgra32(ReadOnlySpan<byte> png, PixelSize expected) =>
        Decode(png, expected, PixelFormats.Bgra32, checked(expected.Width * 4));

    internal static byte[] DecodeGray8(ReadOnlySpan<byte> png, PixelSize expected) =>
        Decode(png, expected, PixelFormats.Gray8, expected.Width);

    private static byte[] Encode(PixelSize size, byte[] pixels, int stride, PixelFormat format)
    {
        var bitmap = BitmapSource.Create(size.Width, size.Height, 96, 96, format, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static byte[] Decode(ReadOnlySpan<byte> png, PixelSize expected,
        PixelFormat targetFormat, int stride)
    {
        ValidateHeader(png, expected);
        try
        {
            using var input = new MemoryStream(png.ToArray(), writable: false);
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) throw new FlimgException(FlimgError.MalformedPng);
            BitmapSource source = decoder.Frames[0];
            if (source.PixelWidth != expected.Width || source.PixelHeight != expected.Height)
                throw new FlimgException(FlimgError.AssetDimensionMismatch);
            if (source.Format != targetFormat)
                source = new FormatConvertedBitmap(source, targetFormat, null, 0);
            var result = new byte[checked(stride * expected.Height)];
            source.CopyPixels(result, stride, 0);
            return result;
        }
        catch (FlimgException) { throw; }
        catch (Exception exception) when (exception is InvalidOperationException
            or NotSupportedException or FileFormatException or ArgumentException)
        {
            throw new FlimgException(FlimgError.MalformedPng, exception);
        }
    }

    private static void ValidateHeader(ReadOnlySpan<byte> png, PixelSize expected)
    {
        if (png.Length < 24 || !png[..8].SequenceEqual(Signature)
            || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new FlimgException(FlimgError.MalformedPng);
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        if (width != expected.Width || height != expected.Height)
            throw new FlimgException(FlimgError.AssetDimensionMismatch);
    }
}
