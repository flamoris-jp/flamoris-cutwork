using System.Text.Json;
using Flamoris.Cutwork.App;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging.Persistence;
using Flamoris.Cutwork.Mcp;
using Flamoris.Logging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LoggingIntegrationTests
{
    [TestMethod]
    public void ApplicationSettingsBindLoggingLevelCategoriesOutputsAndRotation()
    {
        using var directory = new TemporaryDirectory();
        var settings = Path.Combine(directory.Path, "appsettings.json");
        File.WriteAllText(settings, """
            {
              "logging": {
                "level": "warn",
                "categories": { "mcp.transport": "debug" },
                "outputs": [{
                  "type": "file", "path": "logs/custom.log", "format": "text",
                  "rotation": { "enabled": true, "maxFileSizeMb": 7, "maxFiles": 3 }
                }]
              }
            }
            """);

        var options = LoggingBootstrap.LoadOptions(settings);

        Assert.AreEqual("warn", options.Level);
        Assert.AreEqual("debug", options.Categories["mcp.transport"]);
        Assert.AreEqual("logs/custom.log", options.Outputs.Single().Path);
        Assert.AreEqual(7, options.Outputs.Single().Rotation.MaxFileSizeMb);
        Assert.AreEqual(3, options.Outputs.Single().Rotation.MaxFiles);
    }

    [TestMethod]
    public void ApplicationLoggerUsesExplicitWritableBasePathAndExceptionChannel()
    {
        using var directory = new TemporaryDirectory();
        var settings = WriteFileOnlySettings(directory.Path);
        var logger = LoggingBootstrap.Create(settings, directory.Path);

        logger.Info("app.startup", "Application started", new Dictionary<string, object?>
        {
            ["testRun"] = true,
        });
        logger.Error("preview", "Preview generation failed", new InvalidOperationException("preview broke"),
            new Dictionary<string, object?> { ["revision"] = 4L });

        var content = File.ReadAllText(Path.Combine(directory.Path, "logs", "cutwork.log"));
        StringAssert.Contains(content, "[INFO ] [app.startup]");
        StringAssert.Contains(content, "testRun=True");
        StringAssert.Contains(content, "[ERROR] [preview]");
        StringAssert.Contains(content, "System.InvalidOperationException");
        StringAssert.Contains(content, "revision=4");
    }

    [TestMethod]
    public void FileOutputFailureDoesNotEscapeIntoApplicationHost()
    {
        using var directory = new TemporaryDirectory();
        var settings = WriteFileOnlySettings(directory.Path);
        var notDirectory = Path.Combine(directory.Path, "occupied");
        File.WriteAllText(notDirectory, "file");

        var logger = LoggingBootstrap.Create(settings, notDirectory);

        logger.Info("app.startup", "Application remains available");
    }

    [TestMethod]
    public void ProjectFailureRecordsCategoryExceptionAndStructuredOperation()
    {
        using var directory = new TemporaryDirectory();
        var logger = LoggingBootstrap.Create(WriteFileOnlySettings(directory.Path), directory.Path);
        var workspace = new ProjectWorkspace(new EditorSession(), logger: logger);

        Assert.ThrowsExactly<FlimgException>(() =>
            workspace.OpenProject(Path.Combine(directory.Path, "missing.flimg")));

        var content = File.ReadAllText(Path.Combine(directory.Path, "logs", "cutwork.log"));
        StringAssert.Contains(content, "[ERROR] [document.open]");
        StringAssert.Contains(content, "operation=open");
        StringAssert.Contains(content, "FlimgException");
    }

    [TestMethod]
    public async Task McpPermissionAndRevisionFailuresUseSafeHierarchicalCategories()
    {
        using var directory = new TemporaryDirectory();
        var logger = LoggingBootstrap.Create(WriteFileOnlySettings(directory.Path), directory.Path);
        var session = LiveMcpTests.Open();
        using (var readOnly = new LiveAccess(session, LivePermission.ReadOnly, logger))
        {
            var editor = new LiveEditor(session, readOnly, () => false, () => Task.CompletedTask, _ => { }, logger);
            await editor.CallAsync("edit", JsonSerializer.SerializeToElement(new
            {
                documentToken = session.DocumentToken,
                expectedRevision = session.Document!.Revision.ToString(),
                operations = new[] { new { type = "layer.rename", target = session.Document.Base.Id.ToString(), name = "blocked" } },
            }));
        }
        using (var edit = new LiveAccess(session, LivePermission.Edit, logger))
        {
            var editor = new LiveEditor(session, edit, () => false, () => Task.CompletedTask, _ => { }, logger);
            await editor.CallAsync("edit", JsonSerializer.SerializeToElement(new
            {
                documentToken = session.DocumentToken,
                expectedRevision = "999",
                operations = new[] { new { type = "layer.rename", target = session.Document!.Base.Id.ToString(), name = "stale" } },
            }));
            await editor.CallAsync("secret-token-value", JsonSerializer.SerializeToElement(new { }));
        }

        var content = File.ReadAllText(Path.Combine(directory.Path, "logs", "cutwork.log"));
        StringAssert.Contains(content, "[WARN ] [mcp.auth]");
        StringAssert.Contains(content, "[WARN ] [mcp.command]");
        StringAssert.Contains(content, "[INFO ] [mcp.session] Session access granted");
        StringAssert.Contains(content, "[INFO ] [mcp.session] Session access revoked");
        StringAssert.Contains(content, "error=revision_conflict");
        Assert.IsFalse(content.Contains("operations", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(content.Contains("secret-token-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public void McpTransportFailureUsesTransportCategoryWithoutConnectionCoordinates()
    {
        using var directory = new TemporaryDirectory();
        var logger = LoggingBootstrap.Create(WriteFileOnlySettings(directory.Path), directory.Path);

        LiveDiagnostics.TransportFailure(logger, new IOException("connection lost"), recoverable: true);

        var content = File.ReadAllText(Path.Combine(directory.Path, "logs", "cutwork.log"));
        StringAssert.Contains(content, "[WARN ] [mcp.transport]");
        StringAssert.Contains(content, "exceptionType=IOException");
        Assert.IsFalse(content.Contains("cutwork-", StringComparison.Ordinal));
    }

    private static string WriteFileOnlySettings(string directory)
    {
        var path = Path.Combine(directory, "appsettings.json");
        File.WriteAllText(path, """
            { "logging": { "level": "debug", "outputs": [
              { "type": "file", "path": "logs/cutwork.log", "format": "text",
                "rotation": { "enabled": true, "maxFileSizeMb": 20, "maxFiles": 10 } }
            ] } }
            """);
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cutwork-logging-tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
