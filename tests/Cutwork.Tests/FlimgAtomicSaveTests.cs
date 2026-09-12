using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging.Persistence;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class FlimgAtomicSaveTests
{
    [TestMethod]
    public void SuccessfulReplacementIsReadableAndMarksWorkspaceSaved()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "project.flimg");
        var session = new EditorSession();
        session.Open(FlimgRoundTripTests.FullDocument());
        session.Execute(new RenameLayer(session.Document!.Base.Id, "saved base"));
        var workspace = new ProjectWorkspace(session);

        workspace.Save(path);

        Assert.AreEqual(Path.GetFullPath(path), workspace.ProjectPath);
        Assert.IsFalse(session.IsDirty);
        var loaded = new FlimgProjectStore().Load(path);
        Assert.AreEqual(session.Document.Id, loaded.Id);
        Assert.AreEqual("saved base", loaded.Base.Name);
    }

    [TestMethod]
    public void ReplacementFailurePreservesOldProjectAndDoesNotMarkSaved()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "project.flimg");
        var oldDocument = FlimgRoundTripTests.FullDocument();
        new FlimgProjectStore().Save(oldDocument, path);
        var oldBytes = File.ReadAllBytes(path);
        var session = new EditorSession();
        session.Open(FlimgRoundTripTests.FullDocument());
        session.Execute(new RenameLayer(session.Document!.Base.Id, "unsaved"));
        var workspace = new ProjectWorkspace(session,
            new FlimgProjectStore(files: new FailingReplaceFileSystem()));

        AssertFlimg(FlimgError.IoFailure, () => workspace.Save(path));

        CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(path));
        Assert.AreEqual(oldDocument.Id, new FlimgProjectStore().Load(path).Id);
        Assert.IsTrue(session.IsDirty);
        Assert.IsNull(workspace.ProjectPath);
        Assert.AreEqual(0, Directory.GetFiles(directory.Path, "*.tmp").Length);
    }

    [TestMethod]
    public void FailedOpenDoesNotReplaceLiveDocumentOrProjectPath()
    {
        using var directory = new TemporaryDirectory();
        var valid = Path.Combine(directory.Path, "valid.flimg");
        var invalid = Path.Combine(directory.Path, "invalid.flimg");
        File.WriteAllBytes(invalid, [1, 2, 3, 4]);
        var session = new EditorSession();
        var workspace = new ProjectWorkspace(session);
        workspace.OpenArtwork(FlimgRoundTripTests.FullDocument());
        var current = session.Document;
        workspace.Save(valid);

        AssertFlimg(FlimgError.MalformedArchive, () => workspace.OpenProject(invalid));

        Assert.AreSame(current, session.Document);
        Assert.AreEqual(Path.GetFullPath(valid), workspace.ProjectPath);
        Assert.IsFalse(session.IsDirty);
    }

    [TestMethod]
    public void OpenProjectResetsHistoryViewportAndSavedState()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "project.flimg");
        var expected = FlimgRoundTripTests.FullDocument();
        new FlimgProjectStore().Save(expected, path);
        var session = new EditorSession();
        session.Open(FlimgRoundTripTests.FullDocument());
        session.Execute(new RenameLayer(session.Document!.Base.Id, "dirty"));
        session.Viewport.ZoomAt(new(0, 0), 4);
        var workspace = new ProjectWorkspace(session);

        workspace.OpenProject(path);

        Assert.AreEqual(expected.Id, session.Document!.Id);
        Assert.AreEqual(0, session.UndoCount);
        Assert.AreEqual(0, session.RedoCount);
        Assert.IsFalse(session.IsDirty);
        Assert.AreEqual(1.0, session.Viewport.Zoom);
        Assert.AreEqual(Path.GetFullPath(path), workspace.ProjectPath);
    }

    internal static void AssertFlimg(FlimgError error, Action action)
    {
        try { action(); Assert.Fail("Expected a rejected FLIMG operation."); }
        catch (FlimgException exception) { Assert.AreEqual(error, exception.Error); }
    }

    private sealed class FailingReplaceFileSystem : IAtomicProjectFileSystem
    {
        public Stream CreateNew(string path) => File.Open(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        public Stream OpenRead(string path) => File.OpenRead(path);
        public bool Exists(string path) => File.Exists(path);
        public void Replace(string temporaryPath, string destinationPath) =>
            throw new IOException("Injected replacement failure.");
        public void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cutwork-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
