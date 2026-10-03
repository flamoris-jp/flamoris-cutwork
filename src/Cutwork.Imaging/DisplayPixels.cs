namespace Flamoris.Cutwork.Imaging;

/// <summary>WPF-independent byte conversion for disposable display buffers.</summary>
public static class DisplayPixels
{
    public static byte[] TintMask(ReadOnlySpan<byte> mask, byte blue, byte green, byte red, byte opacity)
    {
        var pixels = new byte[checked(mask.Length * 4)];
        for (var index = 0; index < mask.Length; index++)
        {
            // Mask overlays historically truncate rather than round like authored composition.
            var alpha = (byte)(mask[index] * opacity / 255);
            pixels[index * 4] = (byte)(blue * alpha / 255);
            pixels[index * 4 + 1] = (byte)(green * alpha / 255);
            pixels[index * 4 + 2] = (byte)(red * alpha / 255);
            pixels[index * 4 + 3] = alpha;
        }
        return pixels;
    }

    public static void PremultiplyInPlace(Span<byte> bgra32)
    {
        if (bgra32.Length % 4 != 0)
            throw new ArgumentException("Display pixels must contain complete BGRA pixels.", nameof(bgra32));
        for (var offset = 0; offset < bgra32.Length; offset += 4)
        {
            var alpha = bgra32[offset + 3];
            bgra32[offset] = CompositeCache.Multiply(bgra32[offset], alpha);
            bgra32[offset + 1] = CompositeCache.Multiply(bgra32[offset + 1], alpha);
            bgra32[offset + 2] = CompositeCache.Multiply(bgra32[offset + 2], alpha);
        }
    }
}
