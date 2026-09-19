using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace Flamoris.Cutwork.Mcp;

/// <summary>The same closed schema tree validates calls and produces SDK discovery.</summary>
public static class LiveSchema
{
    private static object Obj(params (string, object)[] fields) => new {
        type = "object", additionalProperties = false,
        properties = fields.ToDictionary(f => f.Item1, f => f.Item2), required = fields.Select(f => f.Item1).ToArray() };
    private static object Str(int max = 256) => new { type = "string", maxLength = max };
    private static object Num(double min, double max) => new { type = "number", minimum = min, maximum = max };
    private static object Int(int min, int max) => new { type = "integer", minimum = min, maximum = max };
    private static object Enum(params string[] values) => new { type = "string", @enum = values };
    private static object ArrayOf(object items, int min, int max) => new { type = "array", items, minItems = min, maxItems = max };
    private static readonly object Point = Obj(("x", Num(0, 100000)), ("y", Num(0, 100000)));
    private static readonly object Fence = ArrayOf(Point, 3, LiveLimits.FenceVertices);
    private static readonly object Path = ArrayOf(Point, 1, LiveLimits.Points);
    private static readonly object Target = new { type = "string", pattern = "^(?:[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}|@(?:[0-9]|[1-5][0-9]|6[0-3]))$" };
    private static readonly object NullableTarget = new { anyOf = new object[] { Target, new { type = "null" } } };
    private static readonly object Transform = Obj(("centerX", Num(0, 100000)), ("centerY", Num(0, 100000)),
        ("scale", Num(0.01, 10)), ("rotationDegrees", Num(-360, 360)));
    private static readonly object Revision = new { type = "string", pattern = "^(0|[1-9][0-9]{0,18})$" };
    private static readonly object Token = new { type = "string", pattern = "^[0-9a-f]{32}$" };
    private static object Op(string kind, params (string, object)[] fields) => Obj(new[] { ("type", (object)new { @const = kind }) }.Concat(fields).ToArray());
    private static readonly object Operations = new { oneOf = new object[] {
        Op("part.create", ("fence", Fence), ("step", Int(0, 100)), ("name", Str())),
        Op("layer.rename", ("target", Target), ("name", Str())),
        Op("layer.semantic", ("target", Target), ("semanticName", Str())),
        Op("layer.visible", ("target", Target), ("visible", new { type = "boolean" })),
        Op("layer.reorder", ("target", Target), ("index", Int(0, 4095))),
        Op("layer.delete", ("target", Target)),
        Op("mask.stroke", ("target", Target), ("points", Path), ("radius", Num(0.5, LiveLimits.Radius)), ("polarity", Enum("Add", "Erase"))),
        Op("clone.stroke", ("target", NullableTarget), ("ownerPart", NullableTarget), ("global", new { type = "boolean" }),
            ("source", Point), ("points", Path), ("radius", Num(0.5, LiveLimits.Radius)), ("mode", Enum("Fixed", "Offset"))),
        Op("patch.create", ("fence", Fence), ("transform", Transform), ("name", Str())),
        Op("patch.transform", ("target", Target), ("transform", Transform)) } };
    public static readonly IReadOnlyDictionary<string, JsonElement> Schemas = new Dictionary<string, object> {
        ["context"] = Obj(),
        ["layers"] = Obj(("offset", Int(0, 4096)), ("count", Int(1, LiveLimits.PageSize))),
        ["image"] = Obj(("source", Enum("Original", "Composite", "Part", "Mask")), ("target", NullableTarget),
            ("roi", new { anyOf = new object[] { new { type = "null" }, Obj(("x", Int(0, 100000)), ("y", Int(0, 100000)), ("width", Int(1, 100000)), ("height", Int(1, 100000))) } }),
            ("maxEdge", Int(1, LiveLimits.PreviewEdge))),
        ["part_preview"] = Obj(("fence", Fence), ("step", Int(0, 100)), ("maxEdge", Int(1, LiveLimits.PreviewEdge))),
        ["edit"] = Obj(("documentToken", Token), ("expectedRevision", Revision), ("operations", ArrayOf(Operations, 1, LiveLimits.Operations))),
        ["undo"] = Obj(("documentToken", Token), ("expectedRevision", Revision)),
        ["redo"] = Obj(("documentToken", Token), ("expectedRevision", Revision))
    }.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

    public static bool IsEdit(string name) => name is "edit" or "undo" or "redo";
    public static IList<Tool> Tools(LivePermission permission) => Schemas
        .Where(p => permission == LivePermission.Edit || !IsEdit(p.Key))
        .Select(p => new Tool { Name = p.Key, Description = Descriptions[p.Key], InputSchema = p.Value }).ToList();
    private static readonly Dictionary<string, string> Descriptions = new() {
        ["context"] = "Live document token, monotonic revision, busy and shared history. Coordinates are document pixels; pixel centers x+0.5,y+0.5.",
        ["layers"] = "Paged layer metadata including semantic partOrder, compositor index and ownerPartId. No pixels or filesystem paths.",
        ["image"] = "Bounded PNG of Original, Composite or explicit Part/Mask. Null ROI means source bounds; null target only for Original/Composite. Nearest-pixel mapping is returned.",
        ["part_preview"] = "Pure existing Guriguri fence/step preview; repeat steps to compare. Same parameters create the same mask.",
        ["edit"] = "Atomic ordinary edits; one history entry. Targets are UUID or @N for an earlier created operation result. Clone: existing target OR ownerPart OR explicit global=true. Read current revision first; never retry automatically.",
        ["undo"] = "Undo one shared WPF/MCP history entry with document/revision precondition.",
        ["redo"] = "Redo one shared WPF/MCP history entry with document/revision precondition." };
    public static void Validate(string name, JsonElement input)
    {
        if (!Schemas.TryGetValue(name, out var schema)) throw new LiveException("unknown_tool");
        if (!Matches(schema, input)) throw new LiveException("invalid_arguments");
    }
    private static bool Matches(JsonElement schema, JsonElement value)
    {
        if (schema.TryGetProperty("const", out var constant)) return value.ValueKind == constant.ValueKind && value.ToString() == constant.ToString();
        if (schema.TryGetProperty("oneOf", out var one)) return one.EnumerateArray().Count(s => Matches(s, value)) == 1;
        if (schema.TryGetProperty("anyOf", out var any)) return any.EnumerateArray().Any(s => Matches(s, value));
        var type = schema.GetProperty("type").GetString();
        if (type == "null") return value.ValueKind == JsonValueKind.Null;
        if (type == "object")
        {
            if (value.ValueKind != JsonValueKind.Object) return false;
            var properties = schema.GetProperty("properties");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
                if (!seen.Add(p.Name) || !properties.TryGetProperty(p.Name, out var child) || !Matches(child, p.Value)) return false;
            return schema.GetProperty("required").EnumerateArray().All(p => seen.Contains(p.GetString()!));
        }
        if (type == "array") return value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() >= schema.GetProperty("minItems").GetInt32()
            && value.GetArrayLength() <= schema.GetProperty("maxItems").GetInt32()
            && value.EnumerateArray().All(v => Matches(schema.GetProperty("items"), v));
        if (type == "string")
        {
            if (value.ValueKind != JsonValueKind.String) return false;
            string s = value.GetString()!;
            if (schema.TryGetProperty("maxLength", out var max) && s.Length > max.GetInt32()) return false;
            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(s, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return false;
            return !schema.TryGetProperty("enum", out var values) || values.EnumerateArray().Any(v => v.GetString() == s);
        }
        if (type == "boolean") return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            && (type != "integer" || value.TryGetInt32(out _))
            && number >= schema.GetProperty("minimum").GetDouble() && number <= schema.GetProperty("maximum").GetDouble();
    }
}
