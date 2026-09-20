using System.Text.Json;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Mcp;

public static class CutworkMcpTools
{
    public static IReadOnlyList<HostTool> Create(LiveEditor editor) => LiveSchema.Schemas
        .Select(pair => Create(editor, pair.Key, pair.Value))
        .ToArray();

    private static HostTool Create(LiveEditor editor, string name, JsonElement schema)
    {
        var kind = name switch
        {
            "undo" => OperationKind.Undo,
            "redo" => OperationKind.Redo,
            "edit" => OperationKind.Transaction,
            _ => OperationKind.Query,
        };
        bool foreground = name is not ("context" or "layers");
        return new HostTool<JsonElement>(name, LiveSchema.Description(name), schema, kind,
            input => Decode(name, input),
            (context, input, token) => editor.ExecuteAsync(context, name, input, token),
            foreground);
    }

    private static JsonElement Decode(string name, JsonElement input)
    {
        try { LiveSchema.Validate(name, input); }
        catch (LiveException exception) { throw new ArgumentException(exception.Code, exception); }
        return input.Clone();
    }
}
