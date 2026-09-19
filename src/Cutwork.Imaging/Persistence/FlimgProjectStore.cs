using System.IO;
using Flamoris.Cutwork.Core;
using Flamoris.Logging;

namespace Flamoris.Cutwork.Imaging.Persistence;

public interface IAtomicProjectFileSystem
{
    Stream CreateNew(string path);
    Stream OpenRead(string path);
    bool Exists(string path);
    void Replace(string temporaryPath, string destinationPath);
    void DeleteIfExists(string path);
}

public sealed class FlimgProjectStore
{
    private readonly FlimgArchiveCodec _codec;
    private readonly IAtomicProjectFileSystem _files;
    private readonly AtomicFileWriter _writer;

    public FlimgProjectStore(FlimgArchiveCodec? codec = null, IAtomicProjectFileSystem? files = null)
    {
        _codec = codec ?? new FlimgArchiveCodec();
        _files = files ?? new LocalAtomicProjectFileSystem();
        _writer = new AtomicFileWriter(_files);
    }

    public CutworkDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var input = _files.OpenRead(Path.GetFullPath(path));
            return _codec.Read(input);
        }
        catch (FlimgException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FlimgException(FlimgError.IoFailure, exception);
        }
    }

    public void Save(CutworkDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _writer.Write(path, output => _codec.Write(output, document), temporary =>
        {
            // Validate the completely closed artifact before it can replace a valid project.
            using var input = _files.OpenRead(temporary);
            _ = _codec.Read(input);
        });
    }
}

public sealed class ProjectWorkspace
{
    private readonly FlimgProjectStore _store;
    private readonly FlamorisLogger? _logger;

    public ProjectWorkspace(EditorSession session, FlimgProjectStore? store = null,
        FlamorisLogger? logger = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _store = store ?? new FlimgProjectStore();
        _logger = logger;
    }

    public EditorSession Session { get; }
    public string? ProjectPath { get; private set; }

    public void OpenArtwork(CutworkDocument document)
    {
        Session.Open(document ?? throw new ArgumentNullException(nameof(document)));
        ProjectPath = null;
        _logger?.Info("document.open", "Artwork opened", Properties("import"));
    }

    public void OpenProject(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var document = _store.Load(fullPath);
            Session.Open(document);
            ProjectPath = fullPath;
            _logger?.Info("document.open", "Project opened", Properties("open"));
        }
        catch (Exception exception)
        {
            _logger?.Error("document.open", "Project open failed",
                properties: FailureProperties("open", exception));
            throw;
        }
    }

    public void Save(string? path = null)
    {
        try
        {
            var destination = path is null ? ProjectPath : Path.GetFullPath(path);
            if (destination is null || Session.Document is null)
                throw new InvalidOperationException("A document and project path are required.");
            _store.Save(Session.Document, destination);
            ProjectPath = destination;
            Session.MarkSaved();
            _logger?.Info("document.save", "Project saved", Properties("save"));
        }
        catch (Exception exception)
        {
            _logger?.Error("document.save", "Project save failed",
                properties: FailureProperties("save", exception));
            throw;
        }
    }

    private Dictionary<string, object?> FailureProperties(string operation, Exception exception)
    {
        var properties = Properties(operation);
        properties["format"] = ".flimg";
        properties["exceptionType"] = exception.GetType().Name;
        if (exception is FlimgException flimgException)
        {
            properties["error"] = flimgException.Error.ToString();
            properties["innerExceptionType"] = flimgException.InnerException?.GetType().Name;
        }
        return properties;
    }

    private Dictionary<string, object?> Properties(string operation) => new()
    {
        ["operation"] = operation,
        ["documentToken"] = Session.DocumentToken,
        ["revision"] = Session.Document?.Revision,
        ["width"] = Session.Document?.Dimensions.Width,
        ["height"] = Session.Document?.Dimensions.Height,
    };
}

internal sealed class LocalAtomicProjectFileSystem : IAtomicProjectFileSystem
{
    public Stream CreateNew(string path) => new FileStream(path, FileMode.CreateNew,
        FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);

    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open,
        FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    public bool Exists(string path) => File.Exists(path);

    public void Replace(string temporaryPath, string destinationPath)
    {
        if (Exists(destinationPath))
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null,
                ignoreMetadataErrors: true);
        else
            File.Move(temporaryPath, destinationPath);
    }

    public void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
