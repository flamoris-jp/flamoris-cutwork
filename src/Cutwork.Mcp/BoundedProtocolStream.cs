using System.IO;
using System.Text;
using System.Text.Json;

namespace Flamoris.Cutwork.Mcp;

/// <summary>Resource and JSON hygiene around the official SDK, not a protocol implementation.</summary>
public sealed class BoundedProtocolStream(Stream inner, CancellationToken leaseToken) : Stream
{
    private readonly McpBoundedLineReader reader = new(inner, LiveLimits.FrameBytes - 1);
    private readonly CancellationTokenSource closed = CancellationTokenSource.CreateLinkedTokenSource(leaseToken);
    private readonly MemoryStream output = new();
    private readonly HashSet<string> pending = [];
    private readonly object gate = new();
    private byte[] input = [];
    private int inputOffset, notifications;
    public CancellationToken Closed => closed.Token;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            if (buffer.Length == 0) return 0;
            if (inputOffset == input.Length)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Closed, cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(120));
                var frame = await reader.ReadAsync(deadline.Token);
                if (frame.Status == McpFrameStatus.EndOfStream) { closed.Cancel(); return 0; }
                if (frame.Status != McpFrameStatus.Success) throw new IOException("invalid_frame");
                using var json = JsonDocument.Parse(frame.Line!, new() { MaxDepth = LiveLimits.Depth });
                ValidateEnvelope(json.RootElement);
                lock (gate)
                {
                    if (json.RootElement.TryGetProperty("id", out var id))
                    {
                        string key = id.GetRawText();
                        if (key.Length > 128 || pending.Count >= 8 || !pending.Add(key)) throw new IOException("request_limit");
                    }
                    else if (++notifications > 32) throw new IOException("notification_limit");
                }
                input = Encoding.UTF8.GetBytes(frame.Line! + "\n"); inputOffset = 0;
            }
            int count = Math.Min(buffer.Length, input.Length - inputOffset);
            input.AsMemory(inputOffset, count).CopyTo(buffer); inputOffset += count;
            return count;
        }
        catch { closed.Cancel(); throw; }
    }
    public static void ValidateEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new IOException("invalid_envelope");
        Duplicates(root);
        foreach (var p in root.EnumerateObject())
            if (p.Name is not ("jsonrpc" or "id" or "method" or "params")) throw new IOException("invalid_envelope");
        if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0"
            || !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String || method.GetString()!.Length > 128)
            throw new IOException("invalid_envelope");
        if (root.TryGetProperty("id", out var id) && !(id.ValueKind == JsonValueKind.String || id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _)))
            throw new IOException("invalid_id");
        if (root.TryGetProperty("params", out var args) && args.ValueKind != JsonValueKind.Object) throw new IOException("invalid_params");
        if (method.GetString() == "tools/call")
        {
            if (args.ValueKind != JsonValueKind.Object) throw new IOException("invalid_params");
            foreach (var p in args.EnumerateObject())
                if (p.Name is not ("name" or "arguments" or "_meta")) throw new IOException("invalid_params");
            if (!args.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString()!.Length > 128)
                throw new IOException("invalid_params");
        }
    }
    private static void Duplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new IOException("duplicate_field");
                Duplicates(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Duplicates(item);
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            Closed.ThrowIfCancellationRequested();
            // SDK serializes writes. Buffer a complete response before publishing any bytes.
            for (int i = 0; i < buffer.Length; i++)
            {
                byte b = buffer.Span[i];
                if (output.Length + 1 > LiveLimits.FrameBytes) throw new IOException("response_limit");
                output.WriteByte(b);
                if (b != (byte)'\n') continue;
                using var json = JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length));
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Closed, cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                Closed.ThrowIfCancellationRequested();
                await inner.WriteAsync(output.GetBuffer().AsMemory(0, (int)output.Length), deadline.Token);
                await inner.FlushAsync(deadline.Token);
                if (json.RootElement.TryGetProperty("id", out var id)) lock (gate) { pending.Remove(id.GetRawText()); notifications = 0; }
                output.SetLength(0);
            }
        }
        catch { closed.Cancel(); throw; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { closed.Cancel(); inner.Dispose(); output.Dispose(); }
        base.Dispose(disposing);
    }
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken t) => ReadAsync(b.AsMemory(o, c), t).AsTask();
    public override void Write(byte[] b, int o, int c) => WriteAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] b, int o, int c, CancellationToken t) => WriteAsync(b.AsMemory(o, c), t).AsTask();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
