using System.IO;
using System.Globalization;
using System.Text.Json;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging;
using Flamoris.Cutwork.Imaging.Export;
using Flamoris.Cutwork.Imaging.Persistence;

namespace Flamoris.Cutwork.Cli;

/// <summary>
/// Headless Linux/LIME entry point. Reuses the same production document, tool and export
/// owners as the editor; no second layer format or image-masking implementation.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // The production CLI is portable and does not create a desktop window.
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
            {
                Help();
                return 0;
            }

            if (args.Length == 1 && args[0] == "mcp-catalog")
            {
                CliMcp.WriteCatalog();
                return 0;
            }
            if (args[0] == "serve")
            {
                var serviceOptions = ParseOptions(args, 1);
                Allow(serviceOptions, "--workspace", "--permission", "--hub");
                _ = Required(serviceOptions, "--workspace");
                CliMcp.Run(null, serviceOptions);
                return 0;
            }

            if (args.Length < 2)
                throw new ArgumentException("Expected COMMAND and INPUT. Run cutwork-cli --help.");

            var command = args[0].ToLowerInvariant();
            var input = Path.GetFullPath(args[1]);
            var options = ParseOptions(args, 2);
            switch (command)
            {
                case "mcp":
                    Allow(options, "--permission", "--save-to", "--hub");
                    CliMcp.Run(input, options);
                    break;
                case "import":
                    Allow(options, "--out");
                    Import(input, Required(options, "--out"));
                    break;
                case "inspect":
                    Allow(options);
                    Inspect(input);
                    break;
                case "part-add":
                    Allow(options, "--out", "--polygon", "--name", "--semantic", "--step");
                    PartAdd(input, Required(options, "--out"), Required(options, "--polygon"),
                        Required(options, "--name"), options.GetValueOrDefault("--semantic"),
                        options.GetValueOrDefault("--step"));
                    break;
                case "composite":
                    Allow(options, "--out");
                    Composite(input, Required(options, "--out"));
                    break;
                case "handoff":
                    Allow(options, "--out");
                    Handoff(input, Required(options, "--out"));
                    break;
                default:
                    throw new ArgumentException("Unknown command: " + command);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("cutwork-cli: " + exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }

    private static void Import(string source, string output)
    {
        var destination = Destination(source, output, ".flimg");
        var imported = new ImageImportService().Import(source);
        if (!imported.IsSuccess)
            throw new InvalidOperationException("Image import failed: " + imported.Error);
        var document = new CutworkDocument(imported.Original!);
        new FlimgProjectStore().Save(document, destination);
        Success("import", destination, document);
    }

    private static void Inspect(string source)
    {
        var document = Load(source);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            command = "inspect",
            documentId = document.Id,
            source = document.Original.SourceName,
            width = document.Dimensions.Width,
            height = document.Dimensions.Height,
            layers = document.Layers.Select((layer, index) => new
            {
                id = layer.Id,
                order = index,
                kind = layer.Kind.ToString().ToLowerInvariant(),
                name = layer.Name,
                semanticName = layer.SemanticName,
                visible = layer.Visible,
                bounds = layer.Bounds,
                partOrder = (layer as PartLayer)?.PartOrder,
            }).ToArray(),
        }, Json));
    }

    private static void PartAdd(string source, string output, string polygon,
        string name, string? semantic, string? adjustment)
    {
        var destination = Destination(source, output, ".flimg");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Part name may not be empty.");
        var points = ParsePolygon(polygon);
        var step = 0;
        if (adjustment is not null
            && (!int.TryParse(adjustment, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out step) || step is < 0 or > 20))
            throw new ArgumentException("--step must be an integer from 0 to 20.");

        var document = Load(source);
        var session = new EditorSession();
        session.Open(document);
        var tool = new PartToolController(session, new GuriguriPartFitter());
        tool.Activate();
        foreach (var point in points)
            tool.PointerDown(point, 1, CanvasModifiers.None);
        if (!tool.FinalizeFence())
            throw new ArgumentException("Polygon cannot make a Part: " + tool.Status.Message);
        if (step != 0)
            tool.Wheel(step, CanvasModifiers.None);
        var part = tool.Commit()
            ?? throw new InvalidOperationException("Part could not be committed.");
        session.Execute(new RenameLayer(part.Id, name),
            new SetLayerSemanticName(part.Id, semantic ?? name));
        new FlimgProjectStore().Save(document, destination);
        Success("part-add", destination, document, part.Id);
    }

    private static void Composite(string source, string output)
    {
        var destination = Destination(source, output, ".png");
        var document = Load(source);
        new DocumentExportService().ExportComposite(document, destination);
        Success("composite", destination, document);
    }

    private static void Handoff(string source, string output)
    {
        var destination = Destination(source, output, ".zip");
        var document = Load(source);
        new DocumentExportService().ExportLayerHandoff(document, destination);
        Success("handoff", destination, document);
    }

    private static CutworkDocument Load(string path)
    {
        if (!Path.GetExtension(path).Equals(".flimg", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected a .flimg input project.");
        return new FlimgProjectStore().Load(path);
    }

    private static string Destination(string input, string output, string extension)
    {
        var path = Path.GetFullPath(output);
        if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must end with " + extension);
        if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(input), path))
            throw new ArgumentException("Input and output must be different files.");
        if (File.Exists(path))
            throw new IOException("Refusing to overwrite an existing file: " + path);
        return path;
    }

    private static IReadOnlyList<DocumentPoint> ParsePolygon(string value)
    {
        var tokens = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 3 or > 2048)
            throw new ArgumentException("--polygon needs 3 to 2048 x,y points.");
        var points = new List<DocumentPoint>(tokens.Length);
        foreach (var token in tokens)
        {
            var xy = token.Split(',', StringSplitOptions.TrimEntries);
            if (xy.Length != 2
                || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !double.IsFinite(x) || !double.IsFinite(y))
                throw new ArgumentException("Invalid --polygon point (expected x,y): " + token);
            points.Add(new DocumentPoint(x, y));
        }
        return points;
    }

    private static Dictionary<string, string> ParseOptions(string[] args, int start)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = start; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)
                || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)
                || !parsed.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("Expected unique --key VALUE pairs.");
        }
        return parsed;
    }

    private static string Required(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("Missing required option: " + key);

    private static void Allow(Dictionary<string, string> options, params string[] accepted)
    {
        foreach (var key in options.Keys)
            if (!accepted.Contains(key, StringComparer.Ordinal))
                throw new ArgumentException("Unknown option: " + key);
    }

    private static void Success(string command, string path, CutworkDocument document, Guid? partId = null) =>
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            command,
            output = path,
            documentId = document.Id,
            width = document.Dimensions.Width,
            height = document.Dimensions.Height,
            partId,
        }, Json));

    private static void Help()
    {
        Console.WriteLine("""
        FLAMORIS Cutwork CLI (Linux/LIME, .NET 10)
          cutwork-cli import IMAGE.png --out draft.flimg
          cutwork-cli inspect draft.flimg
          cutwork-cli part-add draft.flimg --polygon "20,20;300,20;300,400;20,400" --name character --out parts.flimg [--semantic body] [--step 2]
          cutwork-cli composite parts.flimg --out composite.png
          cutwork-cli handoff parts.flimg --out handoff.zip
          cutwork-cli mcp IMAGE.png-or-PROJECT.flimg [--permission readOnly|edit] [--save-to NEW.flimg] [--hub wss://HOST/PATH]
          cutwork-cli mcp-catalog

        cutwork-cli serve --workspace /data/workspace [--permission edit] [--hub wss://HOST/PATH]

        Coordinates are image pixels, not viewport positions.
        Outputs must not already exist. No automatic segmentation or hole repair.
        """);
    }
}

