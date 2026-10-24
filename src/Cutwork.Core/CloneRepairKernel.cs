namespace Flamoris.Cutwork.Core;

public readonly record struct CloneRepairPatch(
    DocumentRect Region,
    ReadOnlyMemory<byte> StraightBgra,
    bool HasChanges = true);

/// <summary>WPF-independent imaging boundary for one local batch of clone dabs.</summary>
public sealed class CloneBudgetException : Exception { }

public interface ICloneStroke
{
    CloneRepairPatch Apply(RepairLayer target, IReadOnlyList<DocumentPoint> samples);
}

public interface ICloneRepairKernel
{
    ICloneStroke? BeginStroke(CutworkDocument document, CloneStrokeMapping mapping, double radius) => null;
    CloneRepairPatch CreatePatch(OriginalAsset original, RepairLayer target,
        IReadOnlyList<DocumentPoint> destinationSamples, CloneStrokeMapping mapping, double radius);
}
