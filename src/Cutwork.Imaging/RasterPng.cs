using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging.Persistence;

namespace Flamoris.Cutwork.Imaging;

/// <summary>Shared PNG boundary for previews, persistence and portable acceptance fixtures.</summary>
public static class RasterPng
{
    public static byte[] Encode(PixelSize size, ReadOnlySpan<byte> bgra, bool premultiplied = false) =>
        PngAssetCodec.EncodeBgra32(size, bgra, premultiplied);
}
