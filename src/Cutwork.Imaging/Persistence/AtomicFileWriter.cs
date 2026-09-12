using System.IO;

namespace Flamoris.Cutwork.Imaging.Persistence;

internal sealed class AtomicFileWriter(IAtomicProjectFileSystem files)
{
    internal void Write(string path, Action<Stream> write, Action<string>? validate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new FlimgException(FlimgError.IoFailure);
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = files.CreateNew(temporary))
            {
                write(output);
                output.Flush();
                if (output is FileStream file) file.Flush(flushToDisk: true);
            }
            validate?.Invoke(temporary);
            files.Replace(temporary, destination);
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
        catch
        {
            BestEffortDelete(temporary);
            throw;
        }
    }

    private void BestEffortDelete(string path)
    {
        try { files.DeleteIfExists(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
