using System.Buffers.Binary;
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
    public const int SchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
    public const int MaximumDimension = 16_384;
    public const long MaximumPixels = 100_000_000;
    public const int MaximumEntries = 4_096;
    public const long MaximumEntryBytes = 512L * 1024 * 1024;
    public const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    public const long MaximumPhysicalArchiveBytes = MaximumArchiveBytes + 64L * 1024 * 1024;
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
                    item.PartOrder = part.PartOrder;
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
                    item.OwnerPartId = repair.OwnerPartId is { } ownerPartId
                        ? FormatId(ownerPartId) : null;
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
        MemoryStream? bufferedInput = null;
        try
        {
            var archiveInput = PrepareArchiveInput(input, out bufferedInput);
            PreflightArchive(archiveInput);
            using var archive = new ZipArchive(archiveInput, ZipArchiveMode.Read, leaveOpen: true,
                entryNameEncoding: Encoding.UTF8);
            var entries = ValidateEntries(archive);
            var readBudget = new ArchiveReadBudget(MaximumArchiveBytes);
            if (!entries.TryGetValue(ManifestPath, out var manifestEntry))
                throw new FlimgException(FlimgError.MissingManifest);
            if (manifestEntry.Length > MaximumManifestBytes)
                throw new FlimgException(FlimgError.SizeLimitExceeded);
            var manifestBytes = ReadEntry(manifestEntry, MaximumManifestBytes, readBudget);
            ValidateNoDuplicateJsonProperties(manifestBytes);
            var schemaVersion = ReadEnvelope(manifestBytes);
            if (schemaVersion is not LegacySchemaVersion and not SchemaVersion)
                throw new FlimgException(FlimgError.UnsupportedVersion);
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
            return ReadManifest(manifest, schemaVersion, entries, readBudget);
        }
        catch (FlimgException) { throw; }
        catch (InvalidDataException exception)
        {
            throw new FlimgException(FlimgError.MalformedArchive, exception);
        }
        finally { bufferedInput?.Dispose(); }
    }

    private static CutworkDocument ReadManifest(FlimgManifest manifest, int schemaVersion,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries, ArchiveReadBudget readBudget)
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
        var originalPng = ReadRequiredAsset(entries, OriginalPath, manifest.Original.Sha256, readBudget);
        var original = new OriginalAsset(manifest.Original.SourceName, dimensions,
            checked(dimensions.Width * 4), PngAssetCodec.DecodeBgra32(originalPng, dimensions));

        var restored = new List<LayerRestoreState>(manifest.Layers.Count);
        var identities = new HashSet<Guid> { documentId };
        var referenced = new HashSet<string>(StringComparer.Ordinal) { ManifestPath, OriginalPath };
        foreach (var layer in manifest.Layers)
        {
            if (layer is null || layer.Bounds is null || layer.Name is null)
                throw new FlimgException(FlimgError.MalformedManifest);
            if (schemaVersion == LegacySchemaVersion
                && (layer.PartOrder.HasValue || layer.OwnerPartId is not null))
                throw new FlimgException(FlimgError.InvalidLayer);
            var id = ParseId(layer.Id);
            if (!identities.Add(id)) throw new FlimgException(FlimgError.InvalidIdentity);
            var bounds = ParseRect(layer.Bounds, dimensions);
            switch (layer.Kind)
            {
                case "base":
                    RequireBaseOnlyFields(layer);
                    if (bounds != DocumentRect.FromSize(dimensions))
                        throw new FlimgException(FlimgError.InvalidLayer);
                    restored.Add(new BaseLayerRestoreState(id, layer.Name,
                        layer.SemanticName, layer.Visible));
                    break;
                case "part":
                    if (layer.Transform is not null || layer.SourcePolygon is not null
                        || layer.OwnerPartId is not null
                        || schemaVersion == SchemaVersion && !layer.PartOrder.HasValue)
                        throw new FlimgException(FlimgError.InvalidLayer);
                    var maskPath = RequireAssetPath(layer, id, "mask.png");
                    referenced.Add(maskPath);
                    var maskPng = ReadRequiredAsset(entries, maskPath, layer.Sha256, readBudget);
                    restored.Add(new PartLayerRestoreState(id, layer.Name, layer.SemanticName,
                        layer.Visible, bounds, PngAssetCodec.DecodeGray8(maskPng,
                            new(bounds.Width, bounds.Height)), layer.PartOrder));
                    break;
                case "patch":
                    if (layer.PartOrder.HasValue || layer.OwnerPartId is not null)
                        throw new FlimgException(FlimgError.InvalidLayer);
                    var patchPath = RequireAssetPath(layer, id, "pixels.png");
                    referenced.Add(patchPath);
                    if (layer.Transform is null || layer.SourcePolygon is null)
                        throw new FlimgException(FlimgError.InvalidPatchTransform);
                    var transform = ParseTransform(layer.Transform);
                    var polygon = layer.SourcePolygon.Select(point => ParsePoint(point, dimensions)).ToArray();
                    if (polygon.Length is > 0 and < 3)
                        throw new FlimgException(FlimgError.InvalidLayer);
                    var patchPng = ReadRequiredAsset(entries, patchPath, layer.Sha256, readBudget);
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
                    if (layer.Transform is not null || layer.SourcePolygon is not null
                        || layer.PartOrder.HasValue)
                        throw new FlimgException(FlimgError.InvalidLayer);
                    var repairPath = RequireAssetPath(layer, id, "pixels.png");
                    referenced.Add(repairPath);
                    var repairPng = ReadRequiredAsset(entries, repairPath, layer.Sha256, readBudget);
                    restored.Add(new RepairLayerRestoreState(id, layer.Name, layer.SemanticName,
                        layer.Visible, bounds, PngAssetCodec.DecodeBgra32(repairPng,
                            new(bounds.Width, bounds.Height)), layer.OwnerPartId is { } ownerPartId
                                ? ParseId(ownerPartId) : null));
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
        string path, string? expectedHash, ArchiveReadBudget readBudget)
    {
        if (!entries.TryGetValue(path, out var entry)) throw new FlimgException(FlimgError.MissingAsset);
        var bytes = ReadEntry(entry, MaximumEntryBytes, readBudget);
        if (!IsHash(expectedHash) || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash!), Convert.FromHexString(Hash(bytes))))
            throw new FlimgException(FlimgError.ChecksumMismatch);
        return bytes;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, long limit, ArchiveReadBudget readBudget)
    {
        if (entry.Length < 0 || entry.Length > limit || entry.Length > int.MaxValue)
            throw new FlimgException(FlimgError.SizeLimitExceeded);
        using var source = entry.Open();
        return ReadBounded(source, entry.Length, limit, readBudget);
    }

    internal static byte[] ReadBounded(Stream source, long declaredLength, long limit,
        ArchiveReadBudget readBudget)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(readBudget);
        if (declaredLength < 0 || declaredLength > limit || declaredLength > int.MaxValue)
            throw new FlimgException(FlimgError.SizeLimitExceeded);

        using var target = new MemoryStream((int)Math.Min(declaredLength, 64 * 1024));
        var buffer = new byte[64 * 1024];
        var actualLength = 0L;
        var probeLimit = checked(declaredLength + 1);
        while (actualLength < probeLimit)
        {
            var requested = (int)Math.Min(buffer.Length,
                Math.Min(probeLimit - actualLength, readBudget.Remaining + 1));
            var read = source.Read(buffer, 0, requested);
            if (read == 0) break;
            actualLength += read;
            if (actualLength > declaredLength)
                throw new FlimgException(FlimgError.MalformedArchive);
            readBudget.Consume(read);
            target.Write(buffer, 0, read);
        }
        if (actualLength != declaredLength) throw new FlimgException(FlimgError.MalformedArchive);
        return target.ToArray();
    }

    private static Stream PrepareArchiveInput(Stream input, out MemoryStream? bufferedInput)
    {
        bufferedInput = null;
        if (input.CanSeek)
        {
            if (input.Length > MaximumPhysicalArchiveBytes)
                throw new FlimgException(FlimgError.SizeLimitExceeded);
            return input;
        }

        bufferedInput = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var requested = (int)Math.Min(buffer.Length, MaximumPhysicalArchiveBytes + 1 - total);
            var read = input.Read(buffer, 0, requested);
            if (read == 0) break;
            total += read;
            if (total > MaximumPhysicalArchiveBytes)
                throw new FlimgException(FlimgError.SizeLimitExceeded);
            bufferedInput.Write(buffer, 0, read);
        }
        bufferedInput.Position = 0;
        return bufferedInput;
    }

    internal static int PreflightArchive(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanSeek) throw new ArgumentException("Archive preflight requires a seekable stream.",
            nameof(input));
        var originalPosition = input.Position;
        try
        {
            var length = input.Length;
            const int endRecordLength = 22;
            if (length < endRecordLength) throw new FlimgException(FlimgError.MalformedArchive);
            var tailLength = (int)Math.Min(length, endRecordLength + ushort.MaxValue);
            var tail = new byte[tailLength];
            input.Position = length - tailLength;
            ReadExactly(input, tail);

            var endIndex = -1;
            for (var index = tailLength - endRecordLength; index >= 0; index--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) != 0x06054b50)
                    continue;
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(
                    tail.AsSpan(index + 20, 2));
                if (index + endRecordLength + commentLength != tailLength) continue;
                endIndex = index;
                break;
            }
            if (endIndex < 0) throw new FlimgException(FlimgError.MalformedArchive);

            var end = tail.AsSpan(endIndex, endRecordLength);
            var endOffset = length - tailLength + endIndex;
            var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(end.Slice(4, 2));
            var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(end.Slice(6, 2));
            ulong entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(end.Slice(8, 2));
            ulong entryCount = BinaryPrimitives.ReadUInt16LittleEndian(end.Slice(10, 2));
            ulong directorySize = BinaryPrimitives.ReadUInt32LittleEndian(end.Slice(12, 4));
            ulong directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(end.Slice(16, 4));
            var needsZip64 = diskNumber == ushort.MaxValue || directoryDisk == ushort.MaxValue
                || entriesOnDisk == ushort.MaxValue || entryCount == ushort.MaxValue
                || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue;
            var directoryBoundary = endOffset;

            if (needsZip64)
            {
                const int locatorLength = 20;
                var locatorOffset = endOffset - locatorLength;
                if (locatorOffset < 0) throw new FlimgException(FlimgError.MalformedArchive);
                var locator = new byte[locatorLength];
                input.Position = locatorOffset;
                ReadExactly(input, locator);
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50
                    || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4, 4)) != 0
                    || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16, 4)) != 1)
                    throw new FlimgException(FlimgError.MalformedArchive);
                var zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8, 8));
                if (locatorOffset < 56 || zip64Offset > (ulong)(locatorOffset - 56))
                    throw new FlimgException(FlimgError.MalformedArchive);
                var zip64 = new byte[56];
                input.Position = (long)zip64Offset;
                ReadExactly(input, zip64);
                var zip64Size = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(4, 8));
                if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50
                    || zip64Size < 44 || zip64Size > long.MaxValue
                    || (ulong)locatorOffset != zip64Offset + 12 + zip64Size
                    || BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(16, 4)) != 0
                    || BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(20, 4)) != 0)
                    throw new FlimgException(FlimgError.MalformedArchive);
                entriesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(24, 8));
                entryCount = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32, 8));
                directorySize = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40, 8));
                directoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48, 8));
                directoryBoundary = (long)zip64Offset;
            }
            else if (diskNumber != 0 || directoryDisk != 0)
                throw new FlimgException(FlimgError.MalformedArchive);

            if (entriesOnDisk != entryCount) throw new FlimgException(FlimgError.MalformedArchive);
            if (entryCount > MaximumEntries) throw new FlimgException(FlimgError.SizeLimitExceeded);
            if (directoryOffset > long.MaxValue || directorySize > long.MaxValue)
                throw new FlimgException(FlimgError.MalformedArchive);
            var directoryStart = (long)directoryOffset;
            long directoryEnd;
            try { directoryEnd = checked(directoryStart + (long)directorySize); }
            catch (OverflowException exception)
            {
                throw new FlimgException(FlimgError.MalformedArchive, exception);
            }
            if (directoryStart < 0 || directoryEnd > directoryBoundary)
                throw new FlimgException(FlimgError.MalformedArchive);

            var header = new byte[46];
            var cursor = directoryStart;
            input.Position = cursor;
            for (ulong index = 0; index < entryCount; index++)
            {
                if (directoryEnd - cursor < header.Length)
                    throw new FlimgException(FlimgError.MalformedArchive);
                ReadExactly(input, header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
                    throw new FlimgException(FlimgError.MalformedArchive);
                var variableLength = (long)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2))
                    + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2))
                    + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2));
                var recordLength = header.Length + variableLength;
                if (recordLength > directoryEnd - cursor)
                    throw new FlimgException(FlimgError.MalformedArchive);
                input.Seek(variableLength, SeekOrigin.Current);
                cursor += recordLength;
            }
            if (cursor != directoryEnd) throw new FlimgException(FlimgError.MalformedArchive);
            return (int)entryCount;
        }
        finally { input.Position = originalPosition; }
    }

    private static void ReadExactly(Stream input, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = input.Read(destination[total..]);
            if (read == 0) throw new FlimgException(FlimgError.MalformedArchive);
            total += read;
        }
    }

    private static string RequireAssetPath(FlimgLayer layer, Guid id, string file)
    {
        var expected = LayerPath(id, file);
        if (layer.Asset != expected || !IsHash(layer.Sha256))
            throw new FlimgException(FlimgError.InvalidLayer);
        return expected;
    }

    private static void RequireBaseOnlyFields(FlimgLayer layer)
    {
        // Base accepts common metadata only. Every kind-specific v1/v2 field is invalid.
        if (layer.Asset is not null || layer.Sha256 is not null || layer.Transform is not null
            || layer.SourcePolygon is not null || layer.PartOrder.HasValue
            || layer.OwnerPartId is not null) throw new FlimgException(FlimgError.InvalidLayer);
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

    private static int ReadEnvelope(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format)
                || format.ValueKind != JsonValueKind.String
                || format.GetString() != "flamoris-cutwork"
                || !root.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out var version))
                throw new FlimgException(FlimgError.MalformedManifest);
            return version;
        }
        catch (FlimgException) { throw; }
        catch (JsonException exception)
        {
            throw new FlimgException(FlimgError.MalformedManifest, exception);
        }
    }
}

internal sealed class ArchiveReadBudget(long limit)
{
    private long _remaining = limit > 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));

    internal long Remaining => _remaining;

    internal void Consume(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _remaining) throw new FlimgException(FlimgError.SizeLimitExceeded);
        _remaining -= count;
    }
}
