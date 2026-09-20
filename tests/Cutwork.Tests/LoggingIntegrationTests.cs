using System.Text.Json;
using Flamoris.Cutwork.App;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging.Persistence;
using Flamoris.Cutwork.Mcp;
using Flamoris.Logging;
using Flamoris.Mcp.Core;

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
    public void ProjectFailuresRecordSafeStructuredDiagnosticsWithoutFilesystemPaths()
    {
        using var directory = new TemporaryDirectory();
        var logger = LoggingBootstrap.Create(WriteFileOnlySettings(directory.Path), directory.Path);
        const string sensitivePath = @"C:\private\user\secret-project.flimg";
        var session = new EditorSession();
        var store = new FlimgProjectStore(files: new PathLeakingFileSystem(sensitivePath));
        var workspace = new ProjectWorkspace(session, store, logger);

        Assert.ThrowsExactly<FlimgException>(() =>
            workspace.OpenProject(Path.Combine(directory.Path, "missing.flimg")));
        session.Open(FlimgRoundTripTests.FullDocument());
        Assert.ThrowsExactly<FlimgException>(() =>
            workspace.Save(Path.Combine(directory.Path, "project.flimg")));

        var content = File.ReadAllText(Path.Combine(directory.Path, "logs", "cutwork.log"));
        StringAssert.Contains(content, "[ERROR] [document.open]");
        StringAssert.Contains(content, "[ERROR] [document.save]");
        StringAssert.Contains(content, "operation=open");
        StringAssert.Contains(content, "operation=save");
        StringAssert.Contains(content, "exceptionType=FlimgException");
        StringAssert.Contains(content, "innerExceptionType=IOException");
        StringAssert.Contains(content, "error=IoFailure");
        Assert.IsFalse(content.Contains(sensitivePath, StringComparison.Ordinal));
    }

    [TestMethod]
    [DoNotParallelize]
    public void BridgeFileSinkFailureFallsBackToStderrWithoutWritingProtocolStdout()
    {
        var options = new LoggingOptions
        {
            Level = "debug",
            Outputs = [new() { Type = "file", Path = "\0" }],
        };
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var protocolOutput = new StringWriter();
        using var diagnosticOutput = new StringWriter();
        try
        {
            Console.SetOut(protocolOutput);
            Console.SetError(diagnosticOutput);
            var logger = BridgeLogging.Create(options, Path.GetTempPath());

            logger.Info("mcp.transport", "Bridge remains protocol safe");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.AreEqual(string.Empty, protocolOutput.ToString());
        StringAssert.Contains(diagnosticOutput.ToString(), "[mcp.transport]");
    }

    [TestMethod]
    public async Task McpPermissionAndRevisionFailuresUseSafeHierarchicalCategories()
    {
        using var directory = new TemporaryDirectory();
        var logger = LoggingBootstrap.Create(WriteFileOnlySettings(directory.Path), directory.Path);
        var session = LiveMcpTests.Open();
        using (var readOnly = await McpCoreHarness.CreateAsync(session, McpPermission.ReadOnly, logger: logger))
        {
            var result = await readOnly.CallAsync("edit", new
            {
                operations = new[] { new { type = "layer.rename", target = session.Document.Base.Id.ToString(), name = "blocked" } },
            });
            Assert.AreEqual(McpErrors.Forbidden, result.Error);
        }
        using (var edit = await McpCoreHarness.CreateAsync(session, logger: logger))
        {
            await edit.CallAsync("edit", new
            {
                operations = new[] { new { type = "layer.rename", target = session.Document!.Base.Id.ToString(), name = "stale" } },
            }, expectedRevision: 999);
            await edit.CallAsync("secret-token-value", new { });
        }

        var content = File.ReadAllText(Path.Combine(directory.Path, "logs", "cutwork.log"));
        StringAssert.Contains(content, "[INFO ] [mcp.auth]");
        StringAssert.Contains(content, "[INFO ] [mcp.command]");
        StringAssert.Contains(content, "outcome=enabled");
        StringAssert.Contains(content, "outcome=revoked");
        StringAssert.Contains(content, "outcome=stale_revision");
        Assert.IsFalse(content.Contains("operations", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(content.Contains("secret-token-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task McpCoreDiagnosticsDoNotLogConnectionCoordinatesOrCredentials()
    {
        using var directory = new TemporaryDirectory();
        var logger = LoggingBootstrap.Create(WriteFileOnlySettings(directory.Path), directory.Path);
        var session = LiveMcpTests.Open();
        string credential;
        using (var mcp = await McpCoreHarness.CreateAsync(session, logger: logger))
            credential = mcp.Grant.ExportCredential();

        var content = File.ReadAllText(Path.Combine(directory.Path, "logs", "cutwork.log"));
        StringAssert.Contains(content, "[mcp.auth]");
        Assert.IsFalse(content.Contains("flamoris-test-", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains(credential, StringComparison.Ordinal));
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

    private sealed class PathLeakingFileSystem(string sensitivePath) : IAtomicProjectFileSystem
    {
        public Stream CreateNew(string path) =>
            throw new IOException($"Cannot create {sensitivePath}");
        public Stream OpenRead(string path) =>
            throw new IOException($"Cannot read {sensitivePath}");
        public bool Exists(string path) => false;
        public void Replace(string temporaryPath, string destinationPath) =>
            throw new IOException($"Cannot replace {sensitivePath}");
        public void DeleteIfExists(string path) { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cutwork-logging-tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
