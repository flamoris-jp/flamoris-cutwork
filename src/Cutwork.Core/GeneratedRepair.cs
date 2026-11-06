namespace Flamoris.Cutwork.Core;

/// <summary>Frozen, bounded request and single-use result. No network or format state.</summary>
public sealed class GeneratedRepair
{
    public const int MaximumPixels = 262144;
    private readonly string token;
    private readonly long revision;
    private readonly byte[] mask;
    private byte[]? candidate;
    private bool committed;
    public Guid PartId { get; }
    public DocumentRect Region { get; }
    public byte[] CopyMask() => (byte[])mask.Clone();
    public byte[] OriginalPixels { get; }

    public GeneratedRepair(EditorSession session, Guid partId, int context = 32)
    {
        var doc = session.Document ?? throw new InvalidOperationException("repair_no_document");
        if (session.HasActiveTransaction) throw new InvalidOperationException("repair_busy");
        if (doc.GetLayer(partId) is not PartLayer part) throw new InvalidOperationException("repair_select_part");
        if (context is < 0 or > 128) throw new ArgumentOutOfRangeException(nameof(context));
        int x = Math.Max(0, part.Bounds.X - context), y = Math.Max(0, part.Bounds.Y - context);
        Region = new(x, y, Math.Min(doc.Dimensions.Width, checked(part.Bounds.Right + context)) - x,
            Math.Min(doc.Dimensions.Height, checked(part.Bounds.Bottom + context)) - y);
        if (Region.IsEmpty || (long)Region.Width * Region.Height > MaximumPixels)
            throw new InvalidOperationException("repair_region_limit");
        token = session.DocumentToken; revision = doc.Revision; PartId = part.Id;
        mask = new byte[Region.Width * Region.Height]; OriginalPixels = new byte[mask.Length * 4];
        for (int row = 0; row < Region.Height; row++)
        for (int col = 0; col < Region.Width; col++)
        {
            int i = row * Region.Width + col;
            mask[i] = part.MaskAt(x + col, y + row);
            doc.Original.PixelAt(x + col, y + row).CopyTo(OriginalPixels.AsSpan(i * 4, 4));
        }
        if (!mask.Any(v => v > 0)) throw new InvalidOperationException("repair_empty_mask");
    }

    public byte[] ReferencePixels()
    {
        var pixels = (byte[])OriginalPixels.Clone();
        for (int i = 0; i < mask.Length; i++) if (mask[i] > 0)
        { pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = 128; pixels[i * 4 + 3] = 255; }
        return pixels;
    }

    public bool IsCurrent(EditorSession session) => !committed && !session.HasActiveTransaction
        && session.DocumentToken == token && session.Document?.Revision == revision;

    public void SetCandidate(ReadOnlySpan<byte> straightBgra)
    {
        if (committed) throw new InvalidOperationException("repair_stale");
        if (straightBgra.Length != mask.Length * 4) throw new ArgumentException("repair_dimensions");
        var pixels = straightBgra.ToArray();
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0) pixels.AsSpan(i * 4, 4).Clear();
            else if (pixels[i * 4 + 3] != 255) throw new InvalidOperationException("repair_incomplete_fill");
        }
        candidate = pixels;
    }

    public RepairLayer PreviewLayer(string name) => new(Region,
        candidate ?? throw new InvalidOperationException("repair_no_candidate"), name, ownerPartId: PartId);

    public Guid Commit(EditorSession session, string name)
    {
        if (!IsCurrent(session)) throw new InvalidOperationException("repair_stale");
        var layer = PreviewLayer(name);
        if (session.Document!.Layers.Count >= 4096 || (long)mask.Length * 8 + 1024 > session.HistoryBudgetBytes)
            throw new InvalidOperationException("repair_history_limit");
        using var tx = session.BeginTransaction();
        int index = session.Document.Layers.ToList().FindIndex(l => l is BaseLayer) + 1;
        tx.Apply(new AddLayer(layer, index)); tx.Commit(layer.Id); committed = true;
        return layer.Id;
    }
}
