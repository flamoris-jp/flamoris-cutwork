using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging.Persistence;

namespace Flamoris.Cutwork.Imaging.Export;

public sealed class DocumentExportService
{
    private static readonly DateTimeOffset CanonicalTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly IAtomicProjectFileSystem _files;
    private readonly AtomicFileWriter _writer;

    public DocumentExportService(IAtomicProjectFileSystem? files = null)
    {
        _files = files ?? new LocalAtomicProjectFileSystem();
        _writer = new AtomicFileWriter(_files);
    }

    public void ExportComposite(CutworkDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var cache = new CompositeCache(document);
        var pixels = cache.RenderPending()!.PremultipliedBgra;
        var png = PngAssetCodec.EncodeBgra32(document.Dimensions, pixels, premultiplied: true);
        _writer.Write(path, output => output.Write(png), temporary =>
        {
            using var input = _files.OpenRead(temporary);
            var bytes = ReadBounded(input, FlimgArchiveCodec.MaximumEntryBytes);
            _ = PngAssetCodec.DecodeBgra32(bytes, document.Dimensions);
        });
    }

    public void ExportLayerHandoff(CutworkDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        var manifest = new HandoffManifest
        {
            Format = "flamoris-cutwork-handoff",
            Version = 2,
            DocumentId = document.Id.ToString("D").ToLowerInvariant(),
            Canvas = new() { Width = document.Dimensions.Width, Height = document.Dimensions.Height },
        };
        var assets = new List<(string Path, byte[] Bytes)>();
        for (var order = 0; order < document.Layers.Count; order++)
        {
            var layer = document.Layers[order];
            var item = new HandoffLayer
            {
                Id = layer.Id.ToString("D").ToLowerInvariant(),
                Order = order,
                Kind = layer.Kind.ToString().ToLowerInvariant(),
                Name = layer.Name,
                SemanticName = layer.SemanticName,
                PartOrder = (layer as PartLayer)?.PartOrder,
                OwnerPartId = (layer as RepairLayer)?.OwnerPartId?.ToString("D").ToLowerInvariant(),
                Visible = layer.Visible,
                Bounds = Rect(layer.Bounds),
            };
            if (layer is not BaseLayer)
            {
                item.Asset = $"layers/{order:D4}-{item.Kind}-{layer.Id:N}.png";
                var bytes = layer switch
                {
                    PartLayer part => PngAssetCodec.EncodeBgra32(
                        new(part.Bounds.Width, part.Bounds.Height), PartPixels(document.Original, part)),
                    PatchLayer patch => PngAssetCodec.EncodeBgra32(patch.SourceSize,
                        patch.CopySourcePixels()),
                    RepairLayer repair => PngAssetCodec.EncodeBgra32(
                        new(repair.Bounds.Width, repair.Bounds.Height), repair.CopyPixels(repair.Bounds)),
                    _ => throw new InvalidOperationException(),
                };
                assets.Add((item.Asset, bytes));
            }
            if (layer is PatchLayer patchLayer)
            {
                item.SourceBounds = Rect(patchLayer.SourceBounds);
                item.Transform = new()
                {
                    CenterX = patchLayer.Transform.CenterX,
                    CenterY = patchLayer.Transform.CenterY,
                    Scale = patchLayer.Transform.Scale,
                    RotationDegrees = patchLayer.Transform.RotationDegrees,
                };
                item.SourcePolygon = patchLayer.SourcePolygon.Select(point => new HandoffPoint
                    { X = point.X, Y = point.Y }).ToList();
            }
            manifest.Layers.Add(item);
        }
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        _writer.Write(path, output =>
        {
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true,
                entryNameEncoding: Encoding.UTF8);
            WriteEntry(archive, "handoff.json", json);
            foreach (var asset in assets) WriteEntry(archive, asset.Path, asset.Bytes);
        }, ValidateHandoff);
    }

    private void ValidateHandoff(string path)
    {
        using var input = _files.OpenRead(path);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false,
            entryNameEncoding: Encoding.UTF8);
        if (archive.GetEntry("handoff.json") is null) throw new FlimgException(FlimgError.MalformedArchive);
        foreach (var entry in archive.Entries)
        {
            using var content = entry.Open();
            _ = ReadBounded(content, FlimgArchiveCodec.MaximumEntryBytes);
        }
    }

    private static byte[] PartPixels(OriginalAsset original, PartLayer part)
    {
        var pixels = new byte[checked(part.Bounds.Width * part.Bounds.Height * 4)];
        for (var y = part.Bounds.Y; y < part.Bounds.Bottom; y++)
        for (var x = part.Bounds.X; x < part.Bounds.Right; x++)
        {
            var destination = ((y - part.Bounds.Y) * part.Bounds.Width + x - part.Bounds.X) * 4;
            var source = original.PixelAt(x, y);
            source[..3].CopyTo(pixels.AsSpan(destination, 3));
            pixels[destination + 3] = CompositeCache.Multiply(source[3], part.MaskAt(x, y));
        }
        return pixels;
    }

    private static void WriteEntry(ZipArchive archive, string path, ReadOnlySpan<byte> bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = CanonicalTimestamp;
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static byte[] ReadBounded(Stream input, long limit)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (output.Length + read > limit) throw new FlimgException(FlimgError.SizeLimitExceeded);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static HandoffRect Rect(DocumentRect value) => new()
        { X = value.X, Y = value.Y, Width = value.Width, Height = value.Height };

    private sealed class HandoffManifest
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public string DocumentId { get; set; } = "";
        public HandoffCanvas Canvas { get; set; } = new();
        public List<HandoffLayer> Layers { get; set; } = [];
    }
    private sealed class HandoffCanvas { public int Width { get; set; } public int Height { get; set; } }
    private sealed class HandoffLayer
    {
        public string Id { get; set; } = "";
        public int Order { get; set; }
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string? SemanticName { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? PartOrder { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OwnerPartId { get; set; }
        public bool Visible { get; set; }
        public HandoffRect Bounds { get; set; } = new();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Asset { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HandoffRect? SourceBounds { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HandoffTransform? Transform { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<HandoffPoint>? SourcePolygon { get; set; }
    }
    private sealed class HandoffRect { public int X { get; set; } public int Y { get; set; }
        public int Width { get; set; } public int Height { get; set; } }
    private sealed class HandoffTransform { public double CenterX { get; set; }
        public double CenterY { get; set; } public double Scale { get; set; }
        public double RotationDegrees { get; set; } }
    private sealed class HandoffPoint { public double X { get; set; } public double Y { get; set; } }
}
