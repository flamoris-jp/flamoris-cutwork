using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.App;

/// <summary>
/// Semantic editing projection over existing PartLayer identities. Its ordering is deliberately
/// independent from the compositor stack; Layers remains the only draw-order authority.
/// </summary>
public static class PartLayerProjection
{
    public static IReadOnlyList<PartLayer> Create(CutworkDocument? document) =>
        document?.Layers.OfType<PartLayer>()
            .OrderBy(layer => layer.PartOrder)
            .ThenBy(layer => layer.Id)
            .ToArray() ?? [];

    public static IReadOnlyList<string> CanonicalSemanticNames { get; } =
    [
        "face", "eye_left", "eye_right", "eyebrow_left", "eyebrow_right",
        "ear_left", "ear_right", "earring_left", "earring_right",
        "hair_front", "hair_back", "mouth", "nose", "neck",
        "arm_left", "arm_right", "hand_left", "hand_right",
    ];
}
