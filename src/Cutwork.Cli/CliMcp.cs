using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Imaging.Persistence;
using Flamoris.Cutwork.Mcp;
using Flamoris.Logging;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Cli;

internal static class CliMcp
{
    internal const string HubTokenEnvironment = "CUTWORK_CLI_CONNECTOR_TOKEN";

    public static void Run(string input, IReadOnlyDictionary<string, string> options)
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

        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var work = ServeAsync(input, options.GetValueOrDefault("--save-to"), permission,
                hub, dispatcher, stop.Token);
            _ = work.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            Dispatcher.Run();
            work.GetAwaiter().GetResult();
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    public static void WriteCatalog()
    {
        var session = new EditorSession();
        using var host = new CutworkMcpHost(session, () => false,
            (action, token) => { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; });
        var editor = new LiveEditor(session, McpPermission.Edit, () => false, _ => { });
        using var boundary = new McpBoundary(host, CutworkMcpTools.Create(editor), new(),
            new McpDiagnostics(FlamorisLogger.Create()));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            id = "cutwork-cli", @namespace = "cutwork-cli", transport = "reverse-websocket",
            product_id = "flamoris.cutwork", connector_token_env = HubTokenEnvironment,
            tools = boundary.DescribeCatalog(),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task ServeAsync(string input, string? saveTo, McpPermission permission,
        HubConnectionSettings? hub, Dispatcher dispatcher, CancellationToken token)
    {
        Task Dispatch(Action action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
            return dispatcher.InvokeAsync(() => { ct.ThrowIfCancellationRequested(); action(); },
                DispatcherPriority.Background, ct).Task;
        }

        var session = new EditorSession();
        var workspace = new ProjectWorkspace(session);
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

        using var host = new CutworkMcpHost(session, () => false, Dispatch);
        CapabilityGrant? currentGrant = null;
        var editor = new LiveEditor(session, permission, () => false, _ => { },
            currentGrant: () => currentGrant, workspace: workspace,
            dispatchCompletion: action => Dispatch(action, CancellationToken.None));
        var configuration = new McpOptions
        {
            Permission = permission, PipeName = "flamoris-cutwork-cli-" + Guid.NewGuid().ToString("N"),
            MaxConcurrentRequests = 1,
        };
        using var boundary = new McpBoundary(host, CutworkMcpTools.Create(editor), configuration,
            new McpDiagnostics(FlamorisLogger.Create()));
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
                    await Task.Delay(Timeout.Infinite, token);
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
            // Core's save job posts completion back to the dispatcher; keep pumping it after revocation.
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
