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
            "edit" or "repair_commit_candidate" or "project_save" or "project_checkpoint" or "project_save_cancel" => OperationKind.Transaction,
            _ => OperationKind.Query,
        };
        bool foreground = name is not ("context" or "layers" or "project_context" or "project_save_status" or "project_checkpoints_list" or "project_save_cancel");
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
