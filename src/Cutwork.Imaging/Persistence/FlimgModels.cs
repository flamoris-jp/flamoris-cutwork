using System.Text.Json.Serialization;

namespace Flamoris.Cutwork.Imaging.Persistence;

public enum FlimgError
{
    IoFailure,
    MalformedArchive,
    MissingManifest,
    MalformedManifest,
    UnsupportedVersion,
    InvalidArchivePath,
    DuplicateEntry,
    MissingAsset,
    InvalidIdentity,
    InvalidLayer,
    InvalidDimensions,
    AssetDimensionMismatch,
    InvalidPatchTransform,
    MalformedPng,
    SizeLimitExceeded,
    ChecksumMismatch,
}

public sealed class FlimgException(FlimgError error, Exception? innerException = null)
    : Exception(error.ToString(), innerException)
{
    public FlimgError Error { get; } = error;
}

internal sealed class FlimgManifest
{
    public string Format { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string DocumentId { get; set; } = "";
    public FlimgCanvas Canvas { get; set; } = new();
    public FlimgOriginal Original { get; set; } = new();
    public List<FlimgLayer> Layers { get; set; } = [];
}

internal sealed class FlimgCanvas
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string ColorSpace { get; set; } = "";
    public string PixelFormat { get; set; } = "";
}

internal sealed class FlimgOriginal
{
    public string Asset { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string SourceName { get; set; } = "";
}

internal sealed class FlimgLayer
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? SemanticName { get; set; }
    public bool Visible { get; set; }
    public FlimgRect Bounds { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Asset { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlimgTransform? Transform { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FlimgPoint>? SourcePolygon { get; set; }
}

internal sealed class FlimgRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

internal sealed class FlimgTransform
{
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Scale { get; set; }
    public double RotationDegrees { get; set; }
}

internal sealed class FlimgPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}
