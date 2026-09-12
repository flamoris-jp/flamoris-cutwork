using System.IO;
using Flamoris.Cutwork.Core;

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

    public FlimgProjectStore(FlimgArchiveCodec? codec = null, IAtomicProjectFileSystem? files = null)
    {
        _codec = codec ?? new FlimgArchiveCodec();
        _files = files ?? new LocalAtomicProjectFileSystem();
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
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new FlimgException(FlimgError.IoFailure);
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = _files.CreateNew(temporary))
            {
                _codec.Write(output, document);
                output.Flush();
                if (output is FileStream file) file.Flush(flushToDisk: true);
            }
            // Validate the completely closed artifact before it can replace a valid project.
            using (var input = _files.OpenRead(temporary)) _ = _codec.Read(input);
            _files.Replace(temporary, destination);
        }
        catch (FlimgException)
        {
            BestEffortDelete(temporary);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BestEffortDelete(temporary);
            throw new FlimgException(FlimgError.IoFailure, exception);
        }
    }

    private void BestEffortDelete(string path)
    {
        try { _files.DeleteIfExists(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class ProjectWorkspace
{
    private readonly FlimgProjectStore _store;

    public ProjectWorkspace(EditorSession session, FlimgProjectStore? store = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _store = store ?? new FlimgProjectStore();
    }

    public EditorSession Session { get; }
    public string? ProjectPath { get; private set; }

    public void OpenArtwork(CutworkDocument document)
    {
        Session.Open(document ?? throw new ArgumentNullException(nameof(document)));
        ProjectPath = null;
    }

    public void OpenProject(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var document = _store.Load(fullPath);
        Session.Open(document);
        ProjectPath = fullPath;
    }

    public void Save(string? path = null)
    {
        var destination = path is null ? ProjectPath : Path.GetFullPath(path);
        if (destination is null || Session.Document is null)
            throw new InvalidOperationException("A document and project path are required.");
        _store.Save(Session.Document, destination);
        ProjectPath = destination;
        Session.MarkSaved();
    }
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
