using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Imaging.Persistence;
using Flamoris.Cutwork.Mcp;
using Flamoris.Logging;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Cli;

internal static class CliMcp
{
    internal const string HubTokenEnvironment = "CUTWORK_LIME_CONNECTOR_TOKEN";

    public static void Run(string? input, IReadOnlyDictionary<string, string> options)
    {
        var permission = options.GetValueOrDefault("--permission", "readOnly") switch
        {
            "readOnly" => McpPermission.ReadOnly,
            "edit" => McpPermission.Edit,
            _ => throw new ArgumentException("--permission must be readOnly or edit."),
        };
        HubConnectionSettings? hub = null;
        if (options.TryGetValue("--hub", out var endpoint))
        {
            hub = new(new Uri(endpoint, UriKind.Absolute));
            hub.Validate();
            var secret = Environment.GetEnvironmentVariable(HubTokenEnvironment);
            if (string.IsNullOrWhiteSpace(secret) || secret.Length > 4096 || secret.Any(char.IsControl))
                throw new ArgumentException("Set " + HubTokenEnvironment + " before starting Hub mode.");
        }
        if (permission != McpPermission.Edit && options.ContainsKey("--save-to"))
            throw new ArgumentException("--save-to requires --permission edit.");

        var originalOutput = Console.Out;
        Console.SetOut(Console.Error); // Core logging uses Console.Out; MCP writes the raw stdout stream.
        using var lane = new HostLane();
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var terminate = OperatingSystem.IsLinux()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); })
            : null;
        try
        {
            lane.Run(() => ServeAsync(input, options.GetValueOrDefault("--save-to"),
                options.GetValueOrDefault("--workspace"), permission, hub, lane, stop.Token));
        }
        finally { Console.CancelKeyPress -= cancel; Console.SetOut(originalOutput); }
    }

    public static void WriteCatalog()
    {
        var output = Console.Out;
        Console.SetOut(Console.Error);
        try { WriteCatalogCore(output); }
        finally { Console.SetOut(output); }
    }
    private static FlamorisLogger Logger() => FlamorisLogger.Create(new LoggingOptions
    {
        Level = "info", Outputs = [new() { Type = "console" }],
    });
    private static void WriteCatalogCore(TextWriter output)
    {
        var session = new EditorSession();
        using var host = new CutworkMcpHost(session, () => false,
            (action, token) => { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; });
        var editor = new LiveEditor(session, McpPermission.Edit, () => false, _ => { });
        using var boundary = new McpBoundary(host, CutworkMcpTools.Create(editor).Concat(ServiceWorkspace.CatalogTools()), new(),
            new McpDiagnostics(Logger()));
        output.WriteLine(JsonSerializer.Serialize(new
        {
            id = "cutwork-lime", @namespace = "cutwork-lime", transport = "reverse-websocket",
            product_id = "flamoris.cutwork", connector_token_env = HubTokenEnvironment,
            tools = boundary.DescribeCatalog(),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task ServeAsync(string? input, string? saveTo, string? root,
        McpPermission permission, HubConnectionSettings? hub, HostLane lane, CancellationToken token)
    {
        Task Dispatch(Action action, CancellationToken ct) => lane.Dispatch(action, ct);

        var session = new EditorSession();
        var workspace = new ProjectWorkspace(session);
        using var service = root is null ? null : new ServiceWorkspace(root, workspace, permission);
        if (input is not null)
        {
            CutworkDocument document;
            if (Path.GetExtension(input).Equals(".flimg", StringComparison.OrdinalIgnoreCase))
                document = new FlimgProjectStore().Load(input);
            else
            {
                var imported = new ImageImportService().Import(input);
                if (!imported.IsSuccess) throw new InvalidOperationException("Image import failed: " + imported.Error);
                document = new CutworkDocument(imported.Original!);
            }
            token.ThrowIfCancellationRequested();
            if (saveTo is null) workspace.OpenArtwork(document);
            else
            {
                var target = CreateWorkingCopy(input, saveTo, document);
                workspace.OpenProject(target);
                workspace.AuthorizeAiSave();
            }
        }

        using var host = new CutworkMcpHost(session, () => false, Dispatch,
            applicationLifetime: true, runtimeTokenWhenEmpty: true);
        CapabilityGrant? currentGrant = null;
        var logger = Logger();
        var editor = new LiveEditor(session, permission, () => false, _ => { }, logger,
            currentGrant: () => currentGrant, workspace: workspace,
            dispatchCompletion: action => Dispatch(action, CancellationToken.None));
        var configuration = new McpOptions
        {
            ApplicationLifetime = true, Permission = permission, PipeName = "flamoris-cutwork-cli-" + Guid.NewGuid().ToString("N"),
            MaxConcurrentRequests = 1,
        };
        using var boundary = new McpBoundary(host, CutworkMcpTools.Create(editor).Concat(service?.Tools() ?? []), configuration,
            new McpDiagnostics(logger));
        using var grant = await boundary.EnableAsync(permission, token);
        currentGrant = grant;
        try
        {
            if (hub is not null)
            {
                var provider = new HubConnectionProvider(hub, boundary, grant,
                    _ => ValueTask.FromResult(Environment.GetEnvironmentVariable(HubTokenEnvironment) ?? ""));
                try
                {
                    await provider.StartAsync(token);
                    Console.Error.WriteLine("cutwork-cli: Hub provider started; waiting for registration. Ctrl+C stops.");
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(3), token);
                        // Core bounds each retry cycle; a service must also recover after a long Hub outage.
                        if (!boundary.Status.Current.Connected) await provider.RefreshAsync(token);
                    }
                }
                finally { boundary.Disable(); await provider.StopAsync(CancellationToken.None); }
            }
            else
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                var endpoint = new LocalMcpEndpoint(boundary).RunAsync(grant, lifetime.Token);
                try
                {
                    // Stdout belongs exclusively to MCP. The capability stays inside this process.
                    await StdioBridge.RunAsync(configuration.PipeName, grant.ExportCredential(),
                        Console.OpenStandardInput(), Console.OpenStandardOutput(), configuration, lifetime.Token);
                }
                finally { boundary.Disable(); lifetime.Cancel(); await endpoint; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            boundary.Disable();
            // Keep the host lane alive until the pending checkpoint posts completion after revocation.
            while (workspace.IsSaving) await Task.Delay(25);
            workspace.RevokeAiSave();
        }
    }

    private static string CreateWorkingCopy(string input, string output, CutworkDocument document)
    {
        var target = Path.GetFullPath(output);
        if (!Path.GetExtension(target).Equals(".flimg", StringComparison.OrdinalIgnoreCase)
            || StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(input), target))
            throw new ArgumentException("--save-to must be a new .flimg path distinct from INPUT.");
        if (File.Exists(target)) throw new IOException("--save-to already exists.");
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".cutwork-cli-" + Guid.NewGuid().ToString("N") + ".flimg");
        try
        {
            new FlimgProjectStore().Save(document, temporary);
            File.Move(temporary, target); // No overwrite, including a competing creator after the check above.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return target;
    }
}
