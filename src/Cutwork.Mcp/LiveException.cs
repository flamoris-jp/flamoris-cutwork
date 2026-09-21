namespace Flamoris.Cutwork.Mcp;

public sealed class LiveException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
