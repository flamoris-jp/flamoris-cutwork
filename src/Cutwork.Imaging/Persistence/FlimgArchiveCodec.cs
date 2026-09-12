using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging.Persistence;

public sealed class FlimgArchiveCodec
{
    public const int SchemaVersion = 1;
    public const int MaximumDimension = 16_384;
    public const long MaximumPixels = 100_000_000;
    public const int MaximumEntries = 4_096;
    public const long MaximumEntryBytes = 512L * 1024 * 1024;
    public const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    public const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const string ManifestPath = "manifest.json";
    private const string OriginalPath = "assets/original.png";
    private static readonly DateTimeOffset CanonicalTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public void Write(Stream output, CutworkDocument document)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(document);
        ValidateCanvas(document.Dimensions.Width, document.Dimensions.Height);

        var assets = new List<(string Path, byte[] Bytes)>();
        var originalBytes = PngAssetCodec.EncodeBgra32(document.Dimensions,
            document.Original.CopyPixelBytes());
        assets.Add((OriginalPath, originalBytes));
        var manifest = new FlimgManifest
        {
            Format = "flamoris-cutwork",
            SchemaVersion = SchemaVersion,
            DocumentId = FormatId(document.Id),
            Canvas = new()
            {
                Width = document.Dimensions.Width,
                Height = document.Dimensions.Height,
                ColorSpace = "srgb8",
                PixelFormat = "straight-bgra32",
            },
            Original = new()
            {
                Asset = OriginalPath,
                Sha256 = Hash(originalBytes),
                SourceName = document.Original.SourceName,
            },
        };

        foreach (var layer in document.Layers)
        {
            var item = Common(layer);
            switch (layer)
            {
                case PartLayer part:
                    item.Asset = LayerPath(layer.Id, "mask.png");
                    var mask = PngAssetCodec.EncodeGray8(
                        new(part.Bounds.Width, part.Bounds.Height), part.CopyMask(part.Bounds));
                    item.Sha256 = Hash(mask);
                    assets.Add((item.Asset, mask));
                    break;
                case BaseLayer:
                    break;
                case PatchLayer patch:
                    item.Bounds = Rect(patch.SourceBounds);
                    item.Asset = LayerPath(layer.Id, "pixels.png");
                    var patchBytes = PngAssetCodec.EncodeBgra32(patch.SourceSize,
                        patch.CopySourcePixels());
                    item.Sha256 = Hash(patchBytes);
                    item.Transform = new()
                    {
                        CenterX = patch.Transform.CenterX,
                        CenterY = patch.Transform.CenterY,
                        Scale = patch.Transform.Scale,
                        RotationDegrees = patch.Transform.RotationDegrees,
                    };
                    item.SourcePolygon = patch.SourcePolygon.Select(point => new FlimgPoint
                        { X = point.X, Y = point.Y }).ToList();
                    assets.Add((item.Asset, patchBytes));
                    break;
                case RepairLayer repair:
                    item.Asset = LayerPath(layer.Id, "pixels.png");
                    var repairBytes = PngAssetCodec.EncodeBgra32(
                        new(repair.Bounds.Width, repair.Bounds.Height), repair.CopyPixels(repair.Bounds));
                    item.Sha256 = Hash(repairBytes);
                    assets.Add((item.Asset, repairBytes));
                    break;
                default:
                    throw new FlimgException(FlimgError.InvalidLayer);
            }
            manifest.Layers.Add(item);
        }

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true,
            entryNameEncoding: Encoding.UTF8);
        WriteEntry(archive, ManifestPath, manifestBytes);
        foreach (var asset in assets) WriteEntry(archive, asset.Path, asset.Bytes);
    }

    public CutworkDocument Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true,
                entryNameEncoding: Encoding.UTF8);
            var entries = ValidateEntries(archive);
            if (!entries.TryGetValue(ManifestPath, out var manifestEntry))
                throw new FlimgException(FlimgError.MissingManifest);
            if (manifestEntry.Length > MaximumManifestBytes)
                throw new FlimgException(FlimgError.SizeLimitExceeded);
            var manifestBytes = ReadEntry(manifestEntry, MaximumManifestBytes);
            ValidateNoDuplicateJsonProperties(manifestBytes);
            FlimgManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<FlimgManifest>(manifestBytes, JsonOptions)
                    ?? throw new FlimgException(FlimgError.MalformedManifest);
            }
            catch (FlimgException) { throw; }
            catch (JsonException exception)
            {
                throw new FlimgException(FlimgError.MalformedManifest, exception);
            }
            return ReadVersion(manifest, entries);
        }
        catch (FlimgException) { throw; }
        catch (InvalidDataException exception)
        {
            throw new FlimgException(FlimgError.MalformedArchive, exception);
        }
    }

    private static CutworkDocument ReadVersion(FlimgManifest manifest,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries) => manifest.SchemaVersion switch
    {
        SchemaVersion => ReadV1(manifest, entries),
        _ => throw new FlimgException(FlimgError.UnsupportedVersion),
    };

    private static CutworkDocument ReadV1(FlimgManifest manifest,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (manifest.Format != "flamoris-cutwork" || manifest.Canvas is null
            || manifest.Original is null || manifest.Layers is null)
            throw new FlimgException(FlimgError.MalformedManifest);
        ValidateCanvas(manifest.Canvas.Width, manifest.Canvas.Height);
        if (manifest.Canvas.ColorSpace != "srgb8"
            || manifest.Canvas.PixelFormat != "straight-bgra32")
            throw new FlimgException(FlimgError.InvalidDimensions);
        var documentId = ParseId(manifest.DocumentId);
        var dimensions = new PixelSize(manifest.Canvas.Width, manifest.Canvas.Height);
        if (manifest.Original.Asset != OriginalPath
            || string.IsNullOrWhiteSpace(manifest.Original.SourceName))
            throw new FlimgException(FlimgError.MalformedManifest);
        var originalPng = ReadRequiredAsset(entries, OriginalPath, manifest.Original.Sha256);
        var original = new OriginalAsset(manifest.Original.SourceName, dimensions,
            checked(dimensions.Width * 4), PngAssetCodec.DecodeBgra32(originalPng, dimensions));

        var restored = new List<LayerRestoreState>(manifest.Layers.Count);
        var identities = new HashSet<Guid> { documentId };
        var referenced = new HashSet<string>(StringComparer.Ordinal) { ManifestPath, OriginalPath };
        foreach (var layer in manifest.Layers)
        {
            if (layer is null || layer.Bounds is null || layer.Name is null)
                throw new FlimgException(FlimgError.MalformedManifest);
            var id = ParseId(layer.Id);
            if (!identities.Add(id)) throw new FlimgException(FlimgError.InvalidIdentity);
            var bounds = ParseRect(layer.Bounds, dimensions);
            switch (layer.Kind)
            {
                case "base":
                    RequireNoAsset(layer);
                    if (bounds != DocumentRect.FromSize(dimensions))
                        throw new FlimgException(FlimgError.InvalidLayer);
                    restored.Add(new BaseLayerRestoreState(id, layer.Name,
                        layer.SemanticName, layer.Visible));
                    break;
                case "part":
                    if (layer.Transform is not null || layer.SourcePolygon is not null)
                        throw new FlimgException(FlimgError.InvalidLayer);
                    var maskPath = RequireAssetPath(layer, id, "mask.png");
                    referenced.Add(maskPath);
                    var maskPng = ReadRequiredAsset(entries, maskPath, layer.Sha256);
                    restored.Add(new PartLayerRestoreState(id, layer.Name, layer.SemanticName,
                        layer.Visible, bounds, PngAssetCodec.DecodeGray8(maskPng,
                            new(bounds.Width, bounds.Height))));
                    break;
                case "patch":
                    var patchPath = RequireAssetPath(layer, id, "pixels.png");
                    referenced.Add(patchPath);
                    if (layer.Transform is null || layer.SourcePolygon is null)
                        throw new FlimgException(FlimgError.InvalidPatchTransform);
                    var transform = ParseTransform(layer.Transform);
                    var polygon = layer.SourcePolygon.Select(point => ParsePoint(point, dimensions)).ToArray();
                    if (polygon.Length is > 0 and < 3)
                        throw new FlimgException(FlimgError.InvalidLayer);
                    var patchPng = ReadRequiredAsset(entries, patchPath, layer.Sha256);
                    var pixels = PngAssetCodec.DecodeBgra32(patchPng,
                        new(bounds.Width, bounds.Height));
                    try
                    {
                        var calculated = PatchLayer.CalculateBounds(new(bounds.Width, bounds.Height), transform);
                        if (!DocumentRect.FromSize(dimensions).Contains(calculated))
                            throw new FlimgException(FlimgError.InvalidPatchTransform);
                    }
                    catch (FlimgException) { throw; }
                    catch (Exception exception) when (exception is EditException or ArgumentOutOfRangeException)
                    {
                        throw new FlimgException(FlimgError.InvalidPatchTransform, exception);
                    }
                    restored.Add(new PatchLayerRestoreState(id, layer.Name, layer.SemanticName,
                        layer.Visible, bounds, pixels, transform, polygon));
                    break;
                case "repair":
                    if (layer.Transform is not null || layer.SourcePolygon is not null)
                        throw new FlimgException(FlimgError.InvalidLayer);
                    var repairPath = RequireAssetPath(layer, id, "pixels.png");
                    referenced.Add(repairPath);
                    var repairPng = ReadRequiredAsset(entries, repairPath, layer.Sha256);
                    restored.Add(new RepairLayerRestoreState(id, layer.Name, layer.SemanticName,
                        layer.Visible, bounds, PngAssetCodec.DecodeBgra32(repairPng,
                            new(bounds.Width, bounds.Height))));
                    break;
                default:
                    throw new FlimgException(FlimgError.InvalidLayer);
            }
        }
        if (entries.Keys.Any(path => !referenced.Contains(path)))
            throw new FlimgException(FlimgError.InvalidArchivePath);
        try { return CutworkDocument.Restore(documentId, original, restored); }
        catch (EditException exception) { throw new FlimgException(FlimgError.InvalidLayer, exception); }
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > MaximumEntries) throw new FlimgException(FlimgError.SizeLimitExceeded);
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var canonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (!IsCanonicalPath(name)) throw new FlimgException(FlimgError.InvalidArchivePath);
            var collisionKey = name.Normalize(NormalizationForm.FormC);
            if (!result.TryAdd(name, entry) || !canonical.Add(collisionKey))
                throw new FlimgException(FlimgError.DuplicateEntry);
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
                throw new FlimgException(FlimgError.SizeLimitExceeded);
            total = checked(total + entry.Length);
            if (total > MaximumArchiveBytes) throw new FlimgException(FlimgError.SizeLimitExceeded);
        }
        return result;
    }

    private static bool IsCanonicalPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path != path.Normalize(NormalizationForm.FormC)
            || path.StartsWith('/') || path.Contains('\\') || path.EndsWith('/')
            || path.Contains(':')) return false;
        var parts = path.Split('/');
        return parts.All(part => part.Length > 0 && part is not "." and not "..");
    }

    private static byte[] ReadRequiredAsset(IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string path, string? expectedHash)
    {
        if (!entries.TryGetValue(path, out var entry)) throw new FlimgException(FlimgError.MissingAsset);
        var bytes = ReadEntry(entry, MaximumEntryBytes);
        if (!IsHash(expectedHash) || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash!), Convert.FromHexString(Hash(bytes))))
            throw new FlimgException(FlimgError.ChecksumMismatch);
        return bytes;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, long limit)
    {
        if (entry.Length > limit || entry.Length > int.MaxValue)
            throw new FlimgException(FlimgError.SizeLimitExceeded);
        using var source = entry.Open();
        using var target = new MemoryStream((int)entry.Length);
        source.CopyTo(target);
        if (target.Length != entry.Length) throw new FlimgException(FlimgError.MalformedArchive);
        return target.ToArray();
    }

    private static string RequireAssetPath(FlimgLayer layer, Guid id, string file)
    {
        var expected = LayerPath(id, file);
        if (layer.Asset != expected || !IsHash(layer.Sha256))
            throw new FlimgException(FlimgError.InvalidLayer);
        return expected;
    }

    private static void RequireNoAsset(FlimgLayer layer)
    {
        if (layer.Asset is not null || layer.Sha256 is not null || layer.Transform is not null
            || layer.SourcePolygon is not null) throw new FlimgException(FlimgError.InvalidLayer);
    }

    private static PatchTransform ParseTransform(FlimgTransform value)
    {
        try { return new(value.CenterX, value.CenterY, value.Scale, value.RotationDegrees); }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new FlimgException(FlimgError.InvalidPatchTransform, exception);
        }
    }

    private static DocumentPoint ParsePoint(FlimgPoint? point, PixelSize dimensions)
    {
        if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || point.X < 0 || point.Y < 0 || point.X > dimensions.Width || point.Y > dimensions.Height)
            throw new FlimgException(FlimgError.InvalidLayer);
        return new(point.X, point.Y);
    }

    private static DocumentRect ParseRect(FlimgRect value, PixelSize dimensions)
    {
        try
        {
            var rect = new DocumentRect(value.X, value.Y, value.Width, value.Height);
            if (!DocumentRect.FromSize(dimensions).Contains(rect))
                throw new FlimgException(FlimgError.InvalidLayer);
            return rect;
        }
        catch (FlimgException) { throw; }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new FlimgException(FlimgError.InvalidLayer, exception);
        }
    }

    private static void ValidateCanvas(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaximumDimension || height > MaximumDimension
            || (long)width * height > MaximumPixels)
            throw new FlimgException(FlimgError.InvalidDimensions);
    }

    private static Guid ParseId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty
            || value != id.ToString("D").ToLowerInvariant())
            throw new FlimgException(FlimgError.InvalidIdentity);
        return id;
    }

    private static string FormatId(Guid id) => id.ToString("D").ToLowerInvariant();
    private static string LayerPath(Guid id, string file) => $"layers/{id:N}/{file}";
    private static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static FlimgLayer Common(Layer layer) => new()
    {
        Id = FormatId(layer.Id),
        Kind = layer.Kind.ToString().ToLowerInvariant(),
        Name = layer.Name,
        SemanticName = layer.SemanticName,
        Visible = layer.Visible,
        Bounds = Rect(layer.Bounds),
    };

    private static FlimgRect Rect(DocumentRect value) => new()
        { X = value.X, Y = value.Y, Width = value.Width, Height = value.Height };

    private static void WriteEntry(ZipArchive archive, string path, ReadOnlySpan<byte> bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = CanonicalTimestamp;
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static void ValidateNoDuplicateJsonProperties(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            Visit(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new FlimgException(FlimgError.MalformedManifest, exception);
        }

        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new FlimgException(FlimgError.MalformedManifest);
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Visit(child);
        }
    }
}
