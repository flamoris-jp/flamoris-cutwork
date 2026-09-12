namespace Flamoris.Cutwork.Core;

public enum EditError { NoDocument, MissingLayer, BaseFixed, BandCrossing, InvalidLayer, InvalidPatch, HistoryBudgetExceeded, TransactionActive }

public sealed class EditException(EditError error) : InvalidOperationException(error.ToString())
{
    public EditError Error { get; } = error;
}
