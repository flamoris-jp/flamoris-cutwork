namespace Flamoris.Cutwork.Core;

public readonly record struct CloneRepairPatch(DocumentRect Region, ReadOnlyMemory<byte> StraightBgra);

/// <summary>WPF-independent imaging boundary for one local batch of clone dabs.</summary>
public interface ICloneRepairKernel
{
    CloneRepairPatch CreatePatch(OriginalAsset original, RepairLayer target,
        IReadOnlyList<DocumentPoint> destinationSamples, DocumentPoint offset, double radius);
}
