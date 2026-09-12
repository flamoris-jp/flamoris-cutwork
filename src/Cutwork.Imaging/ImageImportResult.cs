using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging;

public enum ImageImportError
{
    UnsupportedFormat,
    InvalidImage,
    IoFailure,
}

public sealed record ImageImportResult(OriginalAsset? Original, ImageImportError? Error)
{
    public bool IsSuccess => Original is not null;

    public static ImageImportResult Success(OriginalAsset original) => new(original, null);

    public static ImageImportResult Failure(ImageImportError error) => new(null, error);
}
