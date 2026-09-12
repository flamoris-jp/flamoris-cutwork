using System.IO;
using System.Security;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging;

public sealed class ImageImportService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
    };

    public ImageImportResult Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!SupportedExtensions.Contains(Path.GetExtension(path)))
        {
            return ImageImportResult.Failure(ImageImportError.UnsupportedFormat);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return ImageImportResult.Failure(ImageImportError.InvalidImage);
            }

            BitmapSource source = decoder.Frames[0];
            if (source.PixelWidth < 1 || source.PixelHeight < 1)
            {
                return ImageImportResult.Failure(ImageImportError.InvalidImage);
            }

            if (source.Format != PixelFormats.Pbgra32)
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                converted.Freeze();
                source = converted;
            }
            else if (source.CanFreeze)
            {
                source.Freeze();
            }

            var dimensions = new PixelSize(source.PixelWidth, source.PixelHeight);
            var stride = checked(source.PixelWidth * 4);
            var pixels = new byte[checked(stride * source.PixelHeight)];
            source.CopyPixels(pixels, stride, 0);

            return ImageImportResult.Success(
                new OriginalAsset(Path.GetFileName(path), dimensions, stride, pixels));
        }
        catch (FileFormatException)
        {
            return ImageImportResult.Failure(ImageImportError.InvalidImage);
        }
        catch (NotSupportedException)
        {
            return ImageImportResult.Failure(ImageImportError.InvalidImage);
        }
        catch (ArgumentException)
        {
            return ImageImportResult.Failure(ImageImportError.InvalidImage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return ImageImportResult.Failure(ImageImportError.IoFailure);
        }
    }
}
