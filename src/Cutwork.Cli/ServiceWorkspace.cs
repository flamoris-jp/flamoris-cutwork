using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Imaging.Export;
using Flamoris.Cutwork.Imaging.Persistence;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Cli;

/// <summary>Bounded file transfer and document lifecycle for the headless service only.</summary>
internal sealed class ServiceWorkspace : IDisposable
{
    private const long MaximumUpload = 64 * 1024 * 1024;
    private const long MaximumStorage = 8L * 1024 * 1024 * 1024;
    private const int ChunkBytes = 128 * 1024;
    private readonly string root;
    private readonly ProjectWorkspace workspace;
    private readonly McpPermission permission;
    private readonly FileStream processLock;
    private Upload? upload;
    private sealed record Upload(string Id, string Name, string Path, long Size, string Hash, DateTimeOffset Created);
    private sealed record ExportReceipt(string RequestId, string Kind, string DocumentToken, long Revision,
        string FileName, string SizeBytes, string Sha256);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ServiceWorkspace(string directory, ProjectWorkspace workspace, McpPermission permission)
    {
        root = Path.GetFullPath(directory); this.workspace = workspace; this.permission = permission;
        CheckLinks(root);
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CheckLinks(Path.Combine(root, "service.lock"));
        processLock = new FileStream(Path.Combine(root, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var child in new[] { "uploads", "projects", "exports" })
        { CheckLinks(Path.Combine(root, child)); Directory.CreateDirectory(Path.Combine(root, child)); }
        foreach (var pending in Directory.EnumerateFiles(Path.Combine(root, "uploads"), "*.partial")) File.Delete(pending);
        var active = Path.Combine(root, "active.json");
        CheckLinks(active);
        if (File.Exists(active))
        {
            var id = JsonSerializer.Deserialize<string>(File.ReadAllText(active)) ?? throw new IOException("Invalid active document.");
            if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("Invalid active document.");
            workspace.OpenProject(Path.Combine(root, "projects", id + ".flimg"));
            if (permission == McpPermission.Edit) workspace.AuthorizeAiSave();
        }
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Json);
    private static object StringSchema(int max = 256) => new { type = "string", minLength = 1, maxLength = max };
    private static JsonElement Schema(Dictionary<string, object> properties, params string[] required) =>
        Element(new { type = "object", additionalProperties = false, properties, required });
    public IEnumerable<HostTool> Tools() => DefineTools(this);
    public static IEnumerable<HostTool> CatalogTools() => DefineTools(null);
    private static IEnumerable<HostTool> DefineTools(ServiceWorkspace? owner)
    {
        var schemas = new Dictionary<string, JsonElement>
        {
            ["workspace_status"] = Schema(new()),
            ["upload_begin"] = Schema(new() { ["uploadId"] = StringSchema(36), ["fileName"] = StringSchema(128), ["sizeBytes"] = StringSchema(12), ["sha256"] = StringSchema(64) }, "uploadId", "fileName", "sizeBytes", "sha256"),
            ["upload_write"] = Schema(new() { ["uploadId"] = StringSchema(36), ["offset"] = StringSchema(12), ["dataBase64"] = StringSchema(174764), ["sha256"] = StringSchema(64) }, "uploadId", "offset", "dataBase64", "sha256"),
            ["upload_cancel"] = Schema(new() { ["uploadId"] = StringSchema(36) }, "uploadId"),
            ["document_import"] = Schema(new() { ["uploadId"] = StringSchema(36), ["discardUnsaved"] = new { type = "boolean" } }, "uploadId", "discardUnsaved"),
            ["artifact_export"] = Schema(new() { ["requestId"] = StringSchema(36), ["kind"] = new { type = "string", @enum = new[] { "project", "composite", "handoff" } } }, "requestId", "kind"),
            ["artifact_read"] = Schema(new() { ["requestId"] = StringSchema(36), ["offset"] = StringSchema(12) }, "requestId", "offset"),
        };
        foreach (var (name, schema) in schemas)
        {
            bool query = name is "workspace_status" or "artifact_read";
            yield return new HostTool<JsonElement>(name, name switch
            {
                "workspace_status" => "Headless workspace status and pending upload; no filesystem paths. Works without an image.",
                "upload_begin" => "Reserve one PNG/JPEG/flimg upload up to 64 MiB; uploadId UUID; decimal sizeBytes; SHA-256. Edit required.",
                "upload_write" => "Write next chunk, max 128 KiB decoded. Decimal offset; chunk SHA-256; identical retry accepted. Edit required.",
                "upload_cancel" => "Discard the unfinished owned upload. Does not affect the current document.",
                "document_import" => "Verify upload and open as a new persistent working project. Rejects unsaved work unless discardUnsaved=true. Rotates document token; read mcp.context again.",
                "artifact_export" => "Export current revision to immutable project/composite/handoff artifact; requestId UUID is idempotent for this revision. Download using artifact_read.",
                _ => "Read at most 128 KiB from an owned export at decimal offset. Returns chunk digest; verify complete size and SHA-256 from export receipt.",
            }, schema, query ? OperationKind.Query : OperationKind.Transaction,
                input => Decode(input, schema),
                (context, input, token) => owner is null ? throw new InvalidOperationException("Catalog only.") : owner.Execute(context, name, input, token),
                foreground: false) { RequiresDocument = name == "artifact_export" };
        }
    }

    private static JsonElement Decode(JsonElement input, JsonElement schema)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException();
        var fields = schema.GetProperty("properties");
        foreach (var property in input.EnumerateObject())
        {
            if (!fields.TryGetProperty(property.Name, out var spec)) throw new ArgumentException();
            var type = spec.GetProperty("type").GetString();
            if (type == "boolean") { if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException(); }
            else if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { Length: > 0 } text
                || text.Length > spec.GetProperty("maxLength").GetInt32()) throw new ArgumentException();
        }
        foreach (var required in schema.GetProperty("required").EnumerateArray())
            if (!input.TryGetProperty(required.GetString()!, out _)) throw new ArgumentException();
        if (input.EnumerateObject().Select(p => p.Name).Distinct().Count() != input.EnumerateObject().Count()) throw new ArgumentException();
        return input.Clone();
    }

    private Task<JsonElement> Execute(RequestContext context, string name, JsonElement input, CancellationToken token)
    {
        JsonElement Run()
        {
            token.ThrowIfCancellationRequested(); CheckLinks(root);
            foreach (var child in new[] { "uploads", "projects", "exports", "active.json" }) CheckLinks(Path.Combine(root, child));
            if (workspace.IsSaving && name is not ("workspace_status" or "artifact_read")) throw new McpFault(McpErrors.Busy);
            if (name == "workspace_status")
                return Element(new { hasDocument = workspace.Session.Document is not null, dirty = workspace.Session.IsDirty,
                    saving = workspace.IsSaving, upload = upload is null ? null : new { uploadId = upload.Id, fileName = upload.Name, sizeBytes = upload.Size.ToString(CultureInfo.InvariantCulture), receivedBytes = new FileInfo(upload.Path).Length.ToString(CultureInfo.InvariantCulture) } });
            if (name == "artifact_read") return ReadArtifact(input);
            if (permission != McpPermission.Edit) throw new McpFault(McpErrors.Forbidden);
            return name switch
            {
                "upload_begin" => Begin(input), "upload_write" => Write(input),
                "upload_cancel" => Cancel(input), "document_import" => Import(input, token),
                "artifact_export" => Export(input, token), _ => throw new McpFault(McpErrors.UnsupportedCapability),
            };
        }
        return name is "workspace_status" or "artifact_read" ? context.ReadAsync(Run) : context.CommitAsync(Run);
    }

    private JsonElement Begin(JsonElement input)
    {
        var id = Id(input, "uploadId"); var name = Text(input, "fileName");
        var size = Number(input, "sizeBytes"); var hash = HashText(input);
        if (size is < 1 or > MaximumUpload || Path.GetFileName(name) != name || name.Contains('\\')
            || Path.GetExtension(name).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".flimg")) throw new McpFault(McpErrors.InvalidRequest);
        if (upload is { } previous)
        {
            if (previous.Id == id && previous.Name == name && previous.Size == size && previous.Hash == hash)
                return Element(new { uploadId = id, receivedBytes = new FileInfo(previous.Path).Length.ToString(CultureInfo.InvariantCulture) });
            if (DateTimeOffset.UtcNow - previous.Created < TimeSpan.FromMinutes(30)) throw new McpFault("upload_busy");
            File.Delete(previous.Path); upload = null;
        }
        DemandSpace(size);
        var path = Path.Combine(root, "uploads", id + ".partial");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        upload = new(id, name, path, size, hash, DateTimeOffset.UtcNow);
        return Element(new { uploadId = id, receivedBytes = "0" });
    }

    private Upload Owned(JsonElement input) => upload is { } current && current.Id == Id(input, "uploadId")
        && DateTimeOffset.UtcNow - current.Created < TimeSpan.FromMinutes(30) ? current : throw new McpFault("upload_unknown");
    private JsonElement Write(JsonElement input)
    {
        var current = Owned(input); var offset = Number(input, "offset");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(Text(input, "dataBase64")); }
        catch (FormatException) { throw new McpFault(McpErrors.InvalidRequest); }
        if (bytes.Length is < 1 or > ChunkBytes || offset > current.Size - bytes.Length || Digest(bytes) != HashText(input)) throw new McpFault(McpErrors.InvalidRequest);
        using var file = new FileStream(current.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (offset < file.Length)
        {
            if (offset + bytes.Length > file.Length) throw new McpFault("upload_offset");
            file.Position = offset; var old = new byte[bytes.Length]; file.ReadExactly(old);
            if (!old.AsSpan().SequenceEqual(bytes)) throw new McpFault("upload_chunk_conflict");
        }
        else
        {
            if (offset != file.Length) throw new McpFault("upload_offset");
            file.Position = offset; file.Write(bytes); file.Flush(true);
        }
        return Element(new { uploadId = current.Id, receivedBytes = file.Length.ToString(CultureInfo.InvariantCulture) });
    }
    private JsonElement Cancel(JsonElement input)
    { var current = Owned(input); File.Delete(current.Path); upload = null; return Element(new { cancelled = true }); }

    private JsonElement Import(JsonElement input, CancellationToken token)
    {
        var current = Owned(input);
        if (workspace.Session.IsDirty && !input.GetProperty("discardUnsaved").GetBoolean()) throw new McpFault("unsaved_changes");
        if (new FileInfo(current.Path).Length != current.Size || FileHash(current.Path) != current.Hash) throw new McpFault("upload_digest_mismatch");
        var source = Path.Combine(root, "uploads", current.Id + Path.GetExtension(current.Name).ToLowerInvariant());
        File.Move(current.Path, source);
        try
        {
            CutworkDocument document;
            if (Path.GetExtension(source) == ".flimg") document = new FlimgProjectStore().Load(source);
            else
            {
                var imported = new ImageImportService().Import(source);
                if (!imported.IsSuccess) throw new McpFault("invalid_image");
                var original = imported.Original!;
                document = new CutworkDocument(new OriginalAsset(current.Name, original.Dimensions,
                    original.Stride, original.CopyPixelBytes()));
            }
            if (DocumentSnapshot.EstimatedBytes(document) + document.Original.ByteLength > ProjectCheckpoints.MaximumSnapshotBytes) throw new McpFault("document_limit");
            DemandSpace(document.Original.ByteLength * 2);
            var target = Path.Combine(root, "projects", current.Id + ".flimg");
            if (File.Exists(target)) throw new McpFault("document_id_conflict");
            token.ThrowIfCancellationRequested();
            new FlimgProjectStore().Save(document, target);
            token.ThrowIfCancellationRequested();
            // Prepare and verify the file before replacing the one live session.
            workspace.OpenProject(target); workspace.AuthorizeAiSave();
            var pointer = Path.Combine(root, ".active-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(pointer, JsonSerializer.Serialize(current.Id));
            File.Move(pointer, Path.Combine(root, "active.json"), overwrite: true);
            return Element(new { documentId = document.Id, documentToken = workspace.Session.DocumentToken,
                revision = workspace.Session.Document!.Revision.ToString(CultureInfo.InvariantCulture), width = document.Dimensions.Width, height = document.Dimensions.Height });
        }
        finally { File.Delete(source); upload = null; }
    }

    private JsonElement Export(JsonElement input, CancellationToken token)
    {
        var id = Id(input, "requestId"); var kind = Text(input, "kind");
        if (kind is not ("project" or "composite" or "handoff")) throw new McpFault(McpErrors.InvalidRequest);
        var document = workspace.Session.Document ?? throw new McpFault(McpErrors.NoDocument);
        var receiptPath = Path.Combine(root, "exports", id + ".json");
        if (File.Exists(receiptPath))
        {
            var existing = Receipt(id);
            if (existing.DocumentToken != workspace.Session.DocumentToken || existing.Revision != document.Revision || existing.Kind != kind)
                throw new McpFault("request_id_conflict");
            return Element(existing);
        }
        DemandSpace(document.Original.ByteLength * 4 + DocumentSnapshot.EstimatedBytes(document) * 4);
        var extension = kind switch { "project" => ".flimg", "composite" => ".png", _ => ".zip" };
        var fileName = id + extension; var path = Path.Combine(root, "exports", fileName);
        if (File.Exists(path)) throw new McpFault("artifact_incomplete");
        token.ThrowIfCancellationRequested();
        if (kind == "project") new FlimgProjectStore().Save(document, path);
        else if (kind == "composite") new DocumentExportService().ExportComposite(document, path);
        else new DocumentExportService().ExportLayerHandoff(document, path);
        var receipt = new ExportReceipt(id, kind, workspace.Session.DocumentToken, document.Revision,
            fileName, new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture), FileHash(path));
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt, Json));
        return Element(receipt);
    }
    private ExportReceipt Receipt(string id)
    {
        var path = Path.Combine(root, "exports", id + ".json");
        if (!File.Exists(path) || new FileInfo(path).Length > 4096) throw new McpFault("artifact_unknown");
        var receipt = JsonSerializer.Deserialize<ExportReceipt>(File.ReadAllText(path), Json) ?? throw new McpFault("artifact_unknown");
        if (receipt.RequestId != id || Path.GetFileName(receipt.FileName) != receipt.FileName) throw new McpFault("artifact_unknown");
        return receipt;
    }
    private JsonElement ReadArtifact(JsonElement input)
    {
        var id = Id(input, "requestId"); var offset = Number(input, "offset"); var receipt = Receipt(id);
        var path = Path.Combine(root, "exports", receipt.FileName);
        CheckLinks(path);
        using var file = File.OpenRead(path);
        if (offset > file.Length || file.Length.ToString(CultureInfo.InvariantCulture) != receipt.SizeBytes) throw new McpFault(McpErrors.InvalidRequest);
        var bytes = new byte[(int)Math.Min(ChunkBytes, file.Length - offset)]; file.Position = offset; file.ReadExactly(bytes);
        return Element(new { requestId = id, offset = offset.ToString(CultureInfo.InvariantCulture), dataBase64 = Convert.ToBase64String(bytes), chunkSha256 = Digest(bytes), eof = file.Position == file.Length });
    }

    private void DemandSpace(long incoming)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(10001).ToArray();
        if (files.Length > 10000 || files.Sum(p => new FileInfo(p).Length) + incoming > MaximumStorage) throw new McpFault("workspace_quota");
    }
    private static string Text(JsonElement input, string name) => input.GetProperty(name).GetString()!;
    private static string Id(JsonElement input, string name) => Guid.TryParse(Text(input, name), out var id) ? id.ToString("N") : throw new McpFault(McpErrors.InvalidRequest);
    private static string HashText(JsonElement input)
    { var text = Text(input, "sha256"); return text.Length == 64 && text.All(char.IsAsciiHexDigit) ? text.ToLowerInvariant() : throw new McpFault(McpErrors.InvalidRequest); }
    private static long Number(JsonElement input, string name)
    { var text = Text(input, name); return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long n) && n >= 0 && n.ToString(CultureInfo.InvariantCulture) == text ? n : throw new McpFault(McpErrors.InvalidRequest); }
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string FileHash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    private static void CheckLinks(string path)
    {
        for (var current = new FileInfo(Path.GetFullPath(path)); current is not null; current = current.Directory is { } directory ? new FileInfo(directory.FullName) : null)
            if (current.LinkTarget is not null || ((File.Exists(current.FullName) || Directory.Exists(current.FullName)) && (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Service workspace must be on a direct local path.");
    }
    public void Dispose() { if (upload is not null && File.Exists(upload.Path)) File.Delete(upload.Path); processLock.Dispose(); }
}
