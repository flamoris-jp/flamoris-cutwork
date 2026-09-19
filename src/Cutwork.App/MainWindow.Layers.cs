using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging.Diagnostics;

namespace Flamoris.Cutwork.App;

public partial class MainWindow
{
    private bool _refreshingLayers;
    private static readonly RoutedCommand UndoEdit = new(nameof(UndoEdit), typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.Z, ModifierKeys.Control) });
    private static readonly RoutedCommand RedoEdit = new(nameof(RedoEdit), typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.Y, ModifierKeys.Control) });

    private void InitializeLayerEditing()
    {
        CommandBindings.Add(new CommandBinding(UndoEdit, (_, _) => _session.Undo(),
            (_, e) => e.CanExecute = !_remoteEditing && _session.CanUndo));
        CommandBindings.Add(new CommandBinding(RedoEdit, (_, _) => _session.Redo(),
            (_, e) => e.CanExecute = !_remoteEditing && _session.CanRedo));
        UndoMenuItem.Command = UndoEdit;
        UndoMenuItem.CommandTarget = this;
        RedoMenuItem.Command = RedoEdit;
        RedoMenuItem.CommandTarget = this;
        UndoToolbarButton.Command = UndoEdit;
        UndoToolbarButton.CommandTarget = this;
        RedoToolbarButton.Command = RedoEdit;
        RedoToolbarButton.CommandTarget = this;
        SemanticNameEditor.ItemsSource = PartLayerProjection.CanonicalSemanticNames;
        DeveloperFixtureMenuItem.Visibility = Environment.GetCommandLineArgs().Contains("--developer")
            ? Visibility.Visible : Visibility.Collapsed;
        _session.Changed += (_, _) =>
        {
            RefreshLayerPanel();
            UpdatePreviewChecks();
            UpdateStatus();
            UpdateFileCommandState();
            CommandManager.InvalidateRequerySuggested();
        };
    }

    private void LocalizeLayerEditing()
    {
        var text = LocalizationService.Current;
        UndoMenuItem.Header = text["Edit_Undo"];
        RedoMenuItem.Header = text["Edit_Redo"];
        UndoToolbarButton.ToolTip = text["Edit_Undo"];
        RedoToolbarButton.ToolTip = text["Edit_Redo"];
        DeveloperFixtureMenuItem.Header = text["Developer_AddLayers"];
        PartsGroup.Header = text["Panel_Parts"];
        LayersGroup.Header = text["Panel_Layers"];
        PartAddButton.ToolTip = text["Part_Add"];
        PartDeleteButton.ToolTip = text["Part_Delete"];
        LayerUpButton.ToolTip = text["Layer_MoveUp"];
        LayerDownButton.ToolTip = text["Layer_MoveDown"];
        LayerDeleteButton.ToolTip = text["Layer_Delete"];
        SemanticNameLabel.Text = text["Part_SemanticName"];
        SemanticNameApplyButton.Content = text["Properties_Apply"];
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
            var layerRows = document?.Layers.Select(layer => new LayerRow(layer.Id,
                DisplayName(layer, text), LayerKindLabel(document, layer, text), layer.Visible,
                text["Layer_Visible"])).ToArray() ?? [];
            var partRows = PartLayerProjection.Create(document).Select(part => new PartRow(part.Id,
                DisplayName(part, text), part.SemanticName ?? text["Part_SemanticName_None"],
                part.Visible, text["Layer_Visible"])).ToArray();

            // Pixel-only/session changes must not rebuild rows or erase in-progress direct edits.
            if (!LayerList.Items.OfType<LayerRow>().SequenceEqual(layerRows))
                LayerList.ItemsSource = layerRows;
            if (!PartList.Items.OfType<PartRow>().SequenceEqual(partRows))
                PartList.ItemsSource = partRows;

            LayerList.SelectedItem = LayerList.Items.OfType<LayerRow>()
                .FirstOrDefault(row => row.Id == _session.SelectedLayerId);
            PartList.SelectedItem = PartList.Items.OfType<PartRow>()
                .FirstOrDefault(row => row.Id == _session.SelectedLayerId);

            var selected = document?.Layers.FirstOrDefault(layer => layer.Id == _session.SelectedLayerId);
            var selectedPart = selected as PartLayer;
            var index = selected is null ? -1 : document!.Layers.ToList().IndexOf(selected);
            LayerUpButton.IsEnabled = selected is not null && document!.CanReorder(selected.Id, index - 1);
            LayerDownButton.IsEnabled = selected is not null && document!.CanReorder(selected.Id, index + 1);
            LayerDeleteButton.IsEnabled = selected is not null && document!.CanDelete(selected.Id);
            PartAddButton.IsEnabled = document is not null;
            PartDeleteButton.IsEnabled = selectedPart is not null && document!.CanDelete(selectedPart.Id);
            SemanticNameEditor.IsEnabled = SemanticNameApplyButton.IsEnabled = selectedPart is not null;
            SemanticNameEditor.Text = selectedPart?.SemanticName ?? "";
            DeveloperFixtureMenuItem.IsEnabled = document is not null;

            var targetName = selectedPart is null ? null : DisplayName(selectedPart, text);
            MaskTargetText.Text = targetName is null ? text["MaskTool_Target_None"]
                : string.Format(text.Culture, text["MaskTool_Target"], targetName);
            CanvasView.SetMaskTargetLabel(_maskTool.IsActive ? MaskTargetText.Text : null);
            RefreshToolProperties(selected);
        }
        finally
        {
            _refreshingLayers = false;
        }
    }

    private static string DisplayName(Layer layer, LocalizationService text) =>
        string.IsNullOrEmpty(layer.Name) ? text[$"LayerKind_{layer.Kind}"] : layer.Name;

    private static string LayerKindLabel(CutworkDocument document, Layer layer,
        LocalizationService text)
    {
        if (layer is RepairLayer { OwnerPartId: { } ownerPartId }
            && document.GetLayer(ownerPartId) is PartLayer owner)
            return string.Format(text.Culture, text["Layer_RepairOwner"], DisplayName(owner, text));
        return text[$"LayerKind_{layer.Kind}"];
    }

    private void PartSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingLayers && PartList.SelectedItem is PartRow row)
            _session.SelectLayer(row.Id);
    }

    private void LayerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingLayers && LayerList.SelectedItem is LayerRow row)
            _session.SelectLayer(row.Id);
    }

    private void LayerVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (!_refreshingLayers && sender is CheckBox { Tag: Guid id, IsChecked: bool visible })
            ExecuteEdit(new SetLayerVisibility(id, visible));
    }

    private void PartAdd_Click(object sender, RoutedEventArgs e) => PartTool_Click(sender, e);

    private void PartDelete_Click(object sender, RoutedEventArgs e)
    {
        if (PartList.SelectedItem is PartRow row)
            ExecuteEdit(new DeleteLayer(row.Id));
    }

    private void LayerDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_session.SelectedLayerId is { } id)
            ExecuteEdit(new DeleteLayer(id));
    }

    private void LayerUp_Click(object sender, RoutedEventArgs e) => MoveSelectedLayer(-1);
    private void LayerDown_Click(object sender, RoutedEventArgs e) => MoveSelectedLayer(1);

    private void MoveSelectedLayer(int delta)
    {
        if (_session.Document is not { } document || _session.SelectedLayerId is not { } id) return;
        var index = document.Layers.ToList().FindIndex(layer => layer.Id == id);
        ExecuteEdit(new ReorderLayer(id, index + delta));
    }

    private void SemanticNameApply_Click(object sender, RoutedEventArgs e)
    {
        if (PartList.SelectedItem is not PartRow row) return;
        ExecuteEdit(new SetLayerSemanticName(row.Id, SemanticNameEditor.Text));
        CanvasView.Focus();
    }

    private void PartList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            BeginSelectedNameEdit(PartList);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && PartDeleteButton.IsEnabled)
        {
            PartDelete_Click(sender, e);
            e.Handled = true;
        }
    }

    private void LayerList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            BeginSelectedNameEdit(LayerList);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && LayerDeleteButton.IsEnabled)
        {
            LayerDelete_Click(sender, e);
            e.Handled = true;
        }
    }

    private void BeginSelectedNameEdit(ListBox list)
    {
        if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is not DependencyObject item)
            return;
        if (FindVisualChild<TextBox>(item) is { } editor) BeginNameEdit(editor);
    }

    private void DirectName_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox editor)
        {
            BeginNameEdit(editor);
            e.Handled = true;
        }
    }

    private void DirectName_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor) return;
        if (e.Key == Key.Enter)
        {
            CommitNameEdit(editor);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RestoreNameEdit(editor);
            e.Handled = true;
        }
    }

    private void DirectName_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { IsReadOnly: false } editor) CommitNameEdit(editor);
    }

    private void BeginNameEdit(TextBox editor)
    {
        // Read-only rows may show a localized kind fallback. Editing always begins from the
        // authored name so merely pressing Enter cannot persist localized UI prose.
        if (editor.Tag is Guid id && _session.Document is { } document)
            editor.Text = document.GetLayer(id).Name;
        _inlineName = editor;
        editor.IsReadOnly = false;
        editor.Focus();
        editor.SelectAll();
    }

    private void CommitNameEdit(TextBox editor)
    {
        _inlineName = null;
        editor.IsReadOnly = true;
        if (editor.Tag is Guid id) ExecuteEdit(new RenameLayer(id, editor.Text));
    }

    private void RestoreNameEdit(TextBox editor)
    {
        _inlineName = null;
        editor.IsReadOnly = true;
        if (editor.Tag is Guid id && _session.Document is { } document)
            editor.Text = DisplayName(document.GetLayer(id), LocalizationService.Current);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) return result;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
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
        CutworkLog.Current.Error("command.failure", "Edit command failed", exception,
            new Dictionary<string, object?>
            {
                ["error"] = exception.Error.ToString(),
                ["documentToken"] = _session.DocumentToken,
                ["revision"] = _session.Document?.Revision,
            });
        var text = LocalizationService.Current;
        MessageBox.Show(this, text[$"EditError_{exception.Error}"], text["Edit_ErrorTitle"],
            MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshLayerPanel();
    }

    // Presentation rows only. Neither list is a second document or stack authority.
    private sealed record PartRow(Guid Id, string DisplayName, string SemanticName,
        bool Visible, string VisibilityLabel);
    private sealed record LayerRow(Guid Id, string DisplayName, string KindLabel,
        bool Visible, string VisibilityLabel);
}
