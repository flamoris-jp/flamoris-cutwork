using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging.Diagnostics;

namespace Flamoris.Cutwork.App;

public partial class MainWindow
{
    private bool _refreshingLayers;
    private Guid? _displayedLayerId;
    private string? _displayedLayerName;
    private static readonly RoutedCommand UndoEdit = new(nameof(UndoEdit), typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.Z, ModifierKeys.Control) });
    private static readonly RoutedCommand RedoEdit = new(nameof(RedoEdit), typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.Y, ModifierKeys.Control) });

    private void InitializeLayerEditing()
    {
        CommandBindings.Add(new CommandBinding(UndoEdit, (_, _) => _session.Undo(),
            (_, e) => e.CanExecute = _session.CanUndo));
        CommandBindings.Add(new CommandBinding(RedoEdit, (_, _) => _session.Redo(),
            (_, e) => e.CanExecute = _session.CanRedo));
        UndoMenuItem.Command = UndoEdit; UndoMenuItem.CommandTarget = this;
        RedoMenuItem.Command = RedoEdit; RedoMenuItem.CommandTarget = this;
        DeveloperFixtureMenuItem.Visibility = Environment.GetCommandLineArgs().Contains("--developer")
            ? Visibility.Visible : Visibility.Collapsed;
        _session.Changed += (_, _) =>
        {
            RefreshLayerPanel(); UpdatePreviewChecks(); UpdateStatus();
            CommandManager.InvalidateRequerySuggested();
        };
    }

    private void LocalizeLayerEditing()
    {
        var text = LocalizationService.Current;
        UndoMenuItem.Header = text["Edit_Undo"]; RedoMenuItem.Header = text["Edit_Redo"];
        DeveloperFixtureMenuItem.Header = text["Developer_AddLayers"];
        LayerUpButton.Content = text["Layer_MoveUp"]; LayerDownButton.Content = text["Layer_MoveDown"];
        LayerDeleteButton.Content = text["Layer_Delete"];
        LayerNameLabel.Text = text["Layer_Name"]; LayerRenameButton.Content = text["Layer_Rename"];
        RefreshLayerPanel();
    }

    private void RefreshLayerPanel()
    {
        if (_refreshingLayers) return;
        _refreshingLayers = true;
        try
        {
            var text = LocalizationService.Current;
            var document = _session.Document;
            var rows = document?.Layers.Select(layer => new LayerRow(layer.Id,
                string.IsNullOrEmpty(layer.Name) ? text[$"LayerKind_{layer.Kind}"] : layer.Name,
                text[$"LayerKind_{layer.Kind}"], layer.Visible, text["Layer_Visible"])).ToArray() ?? [];
            // Pixel-only/preview/session changes must not rebuild rows or erase in-progress typing.
            if (!LayerList.Items.OfType<LayerRow>().SequenceEqual(rows)) LayerList.ItemsSource = rows;
            LayerList.SelectedItem = LayerList.Items.OfType<LayerRow>().FirstOrDefault(row => row.Id == _session.SelectedLayerId);
            var selected = document?.Layers.FirstOrDefault(layer => layer.Id == _session.SelectedLayerId);
            var index = selected is null ? -1 : document!.Layers.ToList().IndexOf(selected);
            LayerUpButton.IsEnabled = selected is not null && document!.CanReorder(selected.Id, index - 1);
            LayerDownButton.IsEnabled = selected is not null && document!.CanReorder(selected.Id, index + 1);
            LayerDeleteButton.IsEnabled = selected is not null && document!.CanDelete(selected.Id);
            LayerNameEditor.IsEnabled = LayerRenameButton.IsEnabled = selected is not null;
            DeveloperFixtureMenuItem.IsEnabled = document is not null;
            if (_displayedLayerId != selected?.Id || _displayedLayerName != selected?.Name)
            {
                LayerNameEditor.Text = selected?.Name ?? "";
                _displayedLayerId = selected?.Id;
                _displayedLayerName = selected?.Name;
            }
            SelectedKindText.Text = selected is null ? text["Properties_NoSelection"] : text[$"LayerKind_{selected.Kind}"];
            LayerKindDescription.Text = selected is null ? "" : text[$"LayerDescription_{selected.Kind}"];
            LayerBoundsText.Text = selected is null ? "" : string.Format(text.Culture, text["Layer_Bounds"],
                selected.Bounds.X, selected.Bounds.Y, selected.Bounds.Width, selected.Bounds.Height);
            RefreshToolProperties(selected);
        }
        finally { _refreshingLayers = false; }
    }

    private void LayerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingLayers && LayerList.SelectedItem is LayerRow row) _session.SelectLayer(row.Id);
    }
    private void LayerVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (!_refreshingLayers && sender is CheckBox { Tag: Guid id, IsChecked: bool visible })
            ExecuteEdit(new SetLayerVisibility(id, visible));
    }
    private void LayerRename_Click(object sender, RoutedEventArgs e)
    {
        if (_session.SelectedLayerId is { } id) ExecuteEdit(new RenameLayer(id, LayerNameEditor.Text));
    }
    private void LayerDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_session.SelectedLayerId is { } id) ExecuteEdit(new DeleteLayer(id));
    }
    private void LayerUp_Click(object sender, RoutedEventArgs e) => MoveSelectedLayer(-1);
    private void LayerDown_Click(object sender, RoutedEventArgs e) => MoveSelectedLayer(1);
    private void MoveSelectedLayer(int delta)
    {
        if (_session.Document is not { } document || _session.SelectedLayerId is not { } id) return;
        var index = document.Layers.ToList().FindIndex(layer => layer.Id == id);
        ExecuteEdit(new ReorderLayer(id, index + delta));
    }
    private void DeveloperFixture_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Document is { } document) ExecuteEdit(DeveloperLayerFixture.Create(document));
    }
    private void ExecuteEdit(params EditCommand[] commands)
    {
        try { _session.Execute(commands); }
        catch (EditException exception) { ShowEditError(exception); }
    }
    private void ShowEditError(EditException exception)
    {
        var text = LocalizationService.Current;
        MessageBox.Show(this, text[$"EditError_{exception.Error}"], text["Edit_ErrorTitle"],
            MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshLayerPanel();
    }

    // Display projection only. No setters/bindings can mutate an authored Layer.
    private sealed record LayerRow(Guid Id, string DisplayName, string KindLabel, bool Visible, string VisibilityLabel);
}
