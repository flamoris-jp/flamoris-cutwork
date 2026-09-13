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
    [JsonRequired]
    public string Format { get; set; } = "";
    [JsonRequired]
    public int SchemaVersion { get; set; }
    [JsonRequired]
    public string DocumentId { get; set; } = "";
    [JsonRequired]
    public FlimgCanvas Canvas { get; set; } = new();
    [JsonRequired]
    public FlimgOriginal Original { get; set; } = new();
    [JsonRequired]
    public List<FlimgLayer> Layers { get; set; } = [];
}

internal sealed class FlimgCanvas
{
    [JsonRequired]
    public int Width { get; set; }
    [JsonRequired]
    public int Height { get; set; }
    [JsonRequired]
    public string ColorSpace { get; set; } = "";
    [JsonRequired]
    public string PixelFormat { get; set; } = "";
}

internal sealed class FlimgOriginal
{
    [JsonRequired]
    public string Asset { get; set; } = "";
    [JsonRequired]
    public string Sha256 { get; set; } = "";
    [JsonRequired]
    public string SourceName { get; set; } = "";
}

internal sealed class FlimgLayer
{
    [JsonRequired]
    public string Id { get; set; } = "";
    [JsonRequired]
    public string Kind { get; set; } = "";
    [JsonRequired]
    public string Name { get; set; } = "";
    public string? SemanticName { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PartOrder { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerPartId { get; set; }
    [JsonRequired]
    public bool Visible { get; set; }
    [JsonRequired]
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
    [JsonRequired]
    public int X { get; set; }
    [JsonRequired]
    public int Y { get; set; }
    [JsonRequired]
    public int Width { get; set; }
    [JsonRequired]
    public int Height { get; set; }
}

internal sealed class FlimgTransform
{
    [JsonRequired]
    public double CenterX { get; set; }
    [JsonRequired]
    public double CenterY { get; set; }
    [JsonRequired]
    public double Scale { get; set; }
    [JsonRequired]
    public double RotationDegrees { get; set; }
}

internal sealed class FlimgPoint
{
    [JsonRequired]
    public double X { get; set; }
    [JsonRequired]
    public double Y { get; set; }
}
