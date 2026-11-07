using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flamoris.Cutwork.Imaging.Persistence;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Run(Path.GetFullPath(args.Single())).GetAwaiter().GetResult(); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static async Task Run(string cli)
    {
        var root = Path.Combine(Path.GetTempPath(), "cutwork-cli-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "art.png");
            var output = Path.Combine(root, "edited.flimg");
            var pixels = Enumerable.Repeat(new byte[] { 20, 80, 200, 255 }, 32 * 32).SelectMany(x => x).ToArray();
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128)));
            using (var stream = File.Create(input)) png.Save(stream);
            var sourceHash = SHA256.HashData(File.ReadAllBytes(input));

            await using (var read = await Connect(cli, input))
            {
                var tools = await read.ListToolsAsync(cancellationToken: Deadline());
                Check(!tools.Any(t => t.Name is "edit" or "undo" or "project_save"), "Read-only discovery exposed mutations.");
                var common = await Common(read);
                var denied = await Call(read, "undo", new { }, common);
                Check(denied.IsError == true, "Read-only direct mutation accepted.");
                var image = Text(await Call(read, "image", new { source = "Original", target = (string?)null, roi = (object?)null, maxEdge = 32 }));
                Check(Convert.FromBase64String(image.GetProperty("pngBase64").GetString()!).Length > 0, "Original preview missing.");
            }
            Console.WriteLine("CLI read-only discovery, direct denial and image: PASS");

            await using (var edit = await Connect(cli, input, "--permission", "edit", "--save-to", output))
            {
                var before = await Common(edit);
                var part = await Call(edit, "part_polygon_create", new
                {
                    name = "Akino", fence = new[] { new { x = 4, y = 4 }, new { x = 28, y = 4 }, new { x = 28, y = 28 }, new { x = 4, y = 28 } },
                }, before);
                Check(part.IsError != true, "Polygon Part creation failed: " + Text(part));
                Check(await LayerCount(edit) == 2, "Part not present.");
                Check((await Call(edit, "undo", new { }, before)).IsError == true, "Stale guard accepted.");
                Check((await Call(edit, "undo", new { }, await Common(edit))).IsError != true && await LayerCount(edit) == 1, "Undo failed.");
                Check((await Call(edit, "redo", new { }, await Common(edit))).IsError != true && await LayerCount(edit) == 2, "Redo failed.");

                var project = Text(await Call(edit, "project_context", new { })).GetProperty("target");
                var requestId = Guid.NewGuid().ToString();
                var saved = await Call(edit, "project_save", new
                {
                    saveTargetId = project.GetProperty("saveTargetId").GetString(),
                    expectedCheckpointId = (string?)null,
                    expectedWorkingFileHash = project.GetProperty("expectedWorkingFileHash").GetString(),
                    requestId, label = "CLI MCP smoke",
                }, await Common(edit));
                Check(saved.IsError != true, "Save start failed: " + Text(saved));
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    var status = Text(await Call(edit, "project_save_status", new { requestId }));
                    var state = status.GetProperty("state").GetString();
                    if (state == "completed")
                    {
                        Check(status.GetProperty("receipt").GetProperty("workingCopySaved").GetBoolean(), "Checkpoint did not save working copy.");
                        break;
                    }
                    Check(state == "running" && watch.Elapsed < TimeSpan.FromSeconds(30), "Save failed/timed out: " + status);
                    await Task.Delay(50);
                }
                Check(new FlimgProjectStore().Load(output).Layers.Count == 2, "Saved flimg lost Part.");
            }
            Console.WriteLine("CLI guarded Part, stale denial, Undo/Redo and durable checkpoint: PASS");
            Check(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(input))), "Source artwork changed.");
            var savedHash = SHA256.HashData(File.ReadAllBytes(output));
            await RejectStartup(cli, "mcp", input, "--permission", "edit", "--save-to", output);
            Check(savedHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(output))), "Existing target overwritten.");
            await RejectStartup(cli, "mcp", input, "--save-to", Path.Combine(root, "forbidden.flimg"));
            Check(!File.Exists(Path.Combine(root, "forbidden.flimg")), "Read-only startup wrote a project.");
            await Eof(cli, input);
            await Catalog(cli);
            Console.WriteLine("CLI source preservation, overwrite denial, EOF teardown and catalog: PASS");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static CancellationToken Deadline() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
    private static async Task<McpClient> Connect(string cli, string input, params string[] extra) =>
        await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet", Arguments = new[] { cli, "mcp", input }.Concat(extra).ToArray(), Name = "Cutwork CLI smoke",
        }), cancellationToken: Deadline());
    private static JsonElement Text(CallToolResult result) => JsonSerializer.Deserialize<JsonElement>(result.Content.OfType<TextContentBlock>().First().Text);
    private static async Task<JsonElement> Common(McpClient client) => Text(await client.CallToolAsync("mcp.context", new Dictionary<string, object?>(), cancellationToken: Deadline()));
    private static async Task<CallToolResult> Call(McpClient client, string name, object input, JsonElement? guard = null) =>
        await client.CallToolAsync(name, guard.HasValue
            ? new Dictionary<string, object?> { ["input"] = input, ["guard"] = new { runtimeId = guard.Value.GetProperty("runtimeId").GetString(), documentToken = guard.Value.GetProperty("documentToken").GetString(), expectedRevision = guard.Value.GetProperty("revision").GetString() } }
            : new Dictionary<string, object?> { ["input"] = input }, cancellationToken: Deadline());
    private static async Task<int> LayerCount(McpClient client) => Text(await Call(client, "layers", new { offset = 0, count = 64 })).GetProperty("total").GetInt32();
    private static Process Start(string cli, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(cli);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
    private static async Task RejectStartup(string cli, params string[] args)
    {
        using var process = Start(cli, args);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check(process.ExitCode != 0, "Invalid startup accepted."); }
        finally { if (!process.HasExited) process.Kill(true); }
    }
    private static async Task Eof(string cli, string input)
    {
        using var process = Start(cli, "mcp", input);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check(process.ExitCode == 0, "EOF did not stop cleanly."); }
        finally { if (!process.HasExited) process.Kill(true); }
    }
    private static async Task Catalog(string cli)
    {
        using var process = Start(cli, "mcp-catalog");
        var json = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var catalog = JsonSerializer.Deserialize<JsonElement>(json);
        Check(process.ExitCode == 0 && catalog.GetProperty("id").GetString() == "cutwork-cli", "Catalog generation failed.");
        var names = catalog.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Check(names.Contains("part_polygon_create") && names.Contains("project_save") && !names.Any(n => n!.StartsWith("capture.")), "CLI catalog does not match host.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
