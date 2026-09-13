namespace Flamoris.Cutwork.Core;

public abstract record LayerRestoreState(Guid Id, string Name, string? SemanticName, bool Visible);

public sealed record BaseLayerRestoreState(Guid Id, string Name, string? SemanticName, bool Visible)
    : LayerRestoreState(Id, Name, SemanticName, Visible);

public sealed record PartLayerRestoreState(Guid Id, string Name, string? SemanticName, bool Visible,
    DocumentRect Bounds, ReadOnlyMemory<byte> Mask, int? PartOrder = null)
    : LayerRestoreState(Id, Name, SemanticName, Visible);

public sealed record PatchLayerRestoreState(Guid Id, string Name, string? SemanticName, bool Visible,
    DocumentRect SourceBounds, ReadOnlyMemory<byte> Pixels, PatchTransform Transform,
    IReadOnlyList<DocumentPoint> SourcePolygon)
    : LayerRestoreState(Id, Name, SemanticName, Visible);

public sealed record RepairLayerRestoreState(Guid Id, string Name, string? SemanticName, bool Visible,
    DocumentRect Bounds, ReadOnlyMemory<byte> Pixels, Guid? OwnerPartId = null)
    : LayerRestoreState(Id, Name, SemanticName, Visible);
