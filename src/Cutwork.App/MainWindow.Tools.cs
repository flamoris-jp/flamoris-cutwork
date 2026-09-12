using System.Globalization;
using System.Windows;
using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.App;

public partial class MainWindow
{
    private bool _refreshingToolProperties;

    private void LocalizePhase4Tools()
    {
        var text = LocalizationService.Current;
        MaskToolButton.Content = text["MaskTool_Name"];
        MaskToolButton.ToolTip = text["MaskTool_Tooltip"];
        PatchToolButton.Content = text["PatchTool_Name"];
        PatchToolButton.ToolTip = text["PatchTool_Tooltip"];
        CloneToolButton.Content = text["CloneTool_Name"];
        CloneToolButton.ToolTip = text["CloneTool_Tooltip"];
        BlurToolButton.Content = text["BlurTool_Name"];
        BlurToolButton.ToolTip = text["BlurTool_Tooltip"];
        SmudgeToolButton.Content = text["SmudgeTool_Name"];
        SmudgeToolButton.ToolTip = text["SmudgeTool_Tooltip"];
        PatchCommitButton.Content = text["PatchTool_Commit"];
        PatchCancelButton.Content = text["PatchTool_Cancel"];
        MaskRadiusLabel.Text = text["MaskTool_Radius"];
        MaskAddRadio.Content = text["MaskTool_Add"];
        MaskEraseRadio.Content = text["MaskTool_Erase"];
        MaskApplyButton.Content = text["Properties_Apply"];
        PatchPositionLabel.Text = text["PatchTool_Position"];
        PatchScaleLabel.Text = text["PatchTool_Scale"];
        PatchRotationLabel.Text = text["PatchTool_Rotation"];
        PatchApplyButton.Content = text["Properties_Apply"];
        CloneRadiusLabel.Text = text["CloneTool_Radius"];
        CloneApplyButton.Content = text["Properties_Apply"];
        FinishingRadiusLabel.Text = text["FinishingTool_Radius"];
        FinishingStrengthLabel.Text = text["FinishingTool_Strength"];
        FinishingApplyButton.Content = text["Properties_Apply"];
        UpdatePhase4ToolUi();
    }

    private void MaskTool_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Document is null) return;
        _inputRouter.SetActiveTool(_maskTool);
        UpdatePartToolUi();
        CanvasView.Focus();
    }

    private void PatchTool_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Document is null) return;
        _inputRouter.SetActiveTool(_patchTool);
        UpdatePartToolUi();
        CanvasView.Focus();
    }

    private void CloneTool_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Document is null) return;
        _inputRouter.SetActiveTool(_cloneTool);
        UpdatePartToolUi();
        CanvasView.Focus();
    }

    private void BlurTool_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Document is null) return;
        _inputRouter.SetActiveTool(_blurTool);
        UpdatePartToolUi();
        CanvasView.Focus();
    }

    private void SmudgeTool_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Document is null) return;
        _inputRouter.SetActiveTool(_smudgeTool);
        UpdatePartToolUi();
        CanvasView.Focus();
    }

    private void PatchCommit_Click(object sender, RoutedEventArgs e)
    {
        try { _patchTool.Commit(); }
        catch (EditException exception) { ShowEditError(exception); }
        CanvasView.Focus();
    }

    private void PatchCancel_Click(object sender, RoutedEventArgs e)
    {
        _inputRouter.CancelActiveTool();
        CanvasView.Focus();
    }

    private void MaskPolarity_Checked(object sender, RoutedEventArgs e)
    {
        if (_refreshingToolProperties || _maskTool is null) return;
        _maskTool.SetPrimaryPolarity(ReferenceEquals(sender, MaskEraseRadio)
            ? MaskPolarity.Erase : MaskPolarity.Add);
        CanvasView.Focus();
    }

    private void MaskApply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(MaskRadiusEditor.Text, out var radius))
        {
            ShowToolInputError();
            return;
        }
        try { _maskTool.SetRadius(radius); }
        catch (ArgumentOutOfRangeException) { ShowToolInputError(); }
        CanvasView.Focus();
    }

    private void CloneApply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(CloneRadiusEditor.Text, out var radius))
        {
            ShowToolInputError();
            return;
        }
        try { _cloneTool.SetRadius(radius); }
        catch (ArgumentOutOfRangeException) { ShowToolInputError(); }
        CanvasView.Focus();
    }

    private void FinishingApply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(FinishingRadiusEditor.Text, out var radius)
            || !TryRead(FinishingStrengthEditor.Text, out var strengthPercent))
        {
            ShowFinishingInputError();
            return;
        }
        var tool = _blurTool.IsActive ? _blurTool : _smudgeTool.IsActive ? _smudgeTool : null;
        if (tool is null) return;
        try
        {
            tool.SetRadius(radius);
            tool.SetStrength(strengthPercent / 100.0);
        }
        catch (ArgumentOutOfRangeException) { ShowFinishingInputError(); }
        CanvasView.Focus();
    }

    private void PatchApply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(PatchXEditor.Text, out var x)
            || !TryRead(PatchYEditor.Text, out var y)
            || !TryRead(PatchScaleEditor.Text, out var scalePercent)
            || !TryRead(PatchRotationEditor.Text, out var rotation))
        {
            ShowToolInputError();
            return;
        }

        PatchTransform transform;
        try { transform = new PatchTransform(x, y, scalePercent / 100.0, rotation); }
        catch (ArgumentOutOfRangeException)
        {
            ShowToolInputError();
            return;
        }

        try
        {
            var snapshot = _patchTool.Snapshot();
            if (snapshot.Source is not null)
            {
                if (!_patchTool.SetPendingTransform(transform)) ShowToolInputError();
            }
            else if (_session.SelectedLayerId is { } id
                && _session.Document?.GetLayer(id) is PatchLayer)
            {
                _session.Execute(new SetPatchTransform(id, transform));
            }
        }
        catch (EditException exception) { ShowEditError(exception); }
        CanvasView.Focus();
    }

    private void UpdatePhase4ToolUi()
    {
        if (_inputRouter is null) return;
        MaskToolButton.IsChecked = ReferenceEquals(_inputRouter.ActiveTool, _maskTool);
        PatchToolButton.IsChecked = ReferenceEquals(_inputRouter.ActiveTool, _patchTool);
        CloneToolButton.IsChecked = ReferenceEquals(_inputRouter.ActiveTool, _cloneTool);
        BlurToolButton.IsChecked = ReferenceEquals(_inputRouter.ActiveTool, _blurTool);
        SmudgeToolButton.IsChecked = ReferenceEquals(_inputRouter.ActiveTool, _smudgeTool);
        var patchPending = _patchTool.Snapshot().Source is not null;
        PatchCommitButton.Visibility = patchPending ? Visibility.Visible : Visibility.Collapsed;
        PatchCancelButton.Visibility = _patchTool.Snapshot().SourceFence.Count > 0 || patchPending
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshToolProperties(_session.Document?.Layers.FirstOrDefault(
            layer => layer.Id == _session.SelectedLayerId));
    }

    private void RefreshToolProperties(Layer? selected)
    {
        if (_maskTool is null || _patchTool is null || _cloneTool is null
            || _blurTool is null || _smudgeTool is null) return;
        _refreshingToolProperties = true;
        try
        {
            MaskPropertiesPanel.Visibility = selected is PartLayer ? Visibility.Visible : Visibility.Collapsed;
            MaskRadiusEditor.Text = _maskTool.Radius.ToString("0.##", LocalizationService.Current.Culture);
            MaskAddRadio.IsChecked = _maskTool.PrimaryPolarity == MaskPolarity.Add;
            MaskEraseRadio.IsChecked = _maskTool.PrimaryPolarity == MaskPolarity.Erase;

            var pending = _patchTool.Snapshot().Transform;
            var transform = pending ?? (selected as PatchLayer)?.Transform;
            PatchPropertiesPanel.Visibility = transform is null ? Visibility.Collapsed : Visibility.Visible;
            PatchApplyButton.IsEnabled = transform is not null;
            if (transform is { } value)
            {
                PatchXEditor.Text = value.CenterX.ToString("0.##", LocalizationService.Current.Culture);
                PatchYEditor.Text = value.CenterY.ToString("0.##", LocalizationService.Current.Culture);
                PatchScaleEditor.Text = (value.Scale * 100).ToString("0.##", LocalizationService.Current.Culture);
                PatchRotationEditor.Text = value.RotationDegrees.ToString("0.##", LocalizationService.Current.Culture);
            }

            ClonePropertiesPanel.Visibility = _cloneTool.IsActive
                ? Visibility.Visible : Visibility.Collapsed;
            CloneRadiusEditor.Text = _cloneTool.Radius.ToString("0.##", LocalizationService.Current.Culture);

            var finishing = _blurTool.IsActive ? _blurTool : _smudgeTool.IsActive ? _smudgeTool : null;
            FinishingPropertiesPanel.Visibility = finishing is null
                ? Visibility.Collapsed : Visibility.Visible;
            if (finishing is not null)
            {
                FinishingRadiusEditor.Text = finishing.Radius.ToString("0.##", LocalizationService.Current.Culture);
                FinishingStrengthEditor.Text = (finishing.Strength * 100)
                    .ToString("0.##", LocalizationService.Current.Culture);
            }
        }
        finally { _refreshingToolProperties = false; }
    }

    private bool TryGetPhase4Status(out string status)
    {
        var text = LocalizationService.Current;
        if (_maskTool.IsActive)
        {
            status = _maskTool.Status.Message switch
            {
                MaskBrushMessage.SelectPart => text["MaskTool_Status_SelectPart"],
                MaskBrushMessage.Painting => text["MaskTool_Status_Painting"],
                MaskBrushMessage.Cancelled => text["MaskTool_Status_Cancelled"],
                MaskBrushMessage.Committed => text["MaskTool_Status_Committed"],
                _ => text["MaskTool_Status_Ready"],
            };
            return true;
        }
        if (_patchTool.IsActive)
        {
            var snapshot = _patchTool.Snapshot();
            status = snapshot.Status.Message switch
            {
                PatchToolMessage.DrawingSource => string.Format(text.Culture,
                    text["PatchTool_Status_DrawingSource"], snapshot.SourceFence.Count),
                PatchToolMessage.NeedsThreePoints => string.Format(text.Culture,
                    text["PatchTool_Status_NeedsThreePoints"], snapshot.SourceFence.Count),
                PatchToolMessage.InvalidSource => text["PatchTool_Status_InvalidSource"],
                PatchToolMessage.Placing => text["PatchTool_Status_Placing"],
                PatchToolMessage.Cancelled => text["PatchTool_Status_Cancelled"],
                PatchToolMessage.Committed => text["PatchTool_Status_Committed"],
                _ => text["PatchTool_Status_Ready"],
            };
            return true;
        }
        if (_cloneTool.IsActive)
        {
            var snapshot = _cloneTool.Snapshot();
            status = snapshot.Status.Message switch
            {
                CloneRepairMessage.SourceRequired => text["CloneTool_Status_SourceRequired"],
                CloneRepairMessage.SourceSet => string.Format(text.Culture,
                    text["CloneTool_Status_SourceSet"], snapshot.SourceAnchor!.Value.X,
                    snapshot.SourceAnchor.Value.Y),
                CloneRepairMessage.Painting => text["CloneTool_Status_Painting"],
                CloneRepairMessage.Cancelled => text["CloneTool_Status_Cancelled"],
                CloneRepairMessage.Committed => text["CloneTool_Status_Committed"],
                _ => text["CloneTool_Status_Ready"],
            };
            return true;
        }
        var finishing = _blurTool.IsActive ? _blurTool : _smudgeTool.IsActive ? _smudgeTool : null;
        if (finishing is not null)
        {
            status = finishing.Message switch
            {
                RepairFinishingMessage.SelectRepair => text["FinishingTool_Status_SelectRepair"],
                RepairFinishingMessage.Painting => text["FinishingTool_Status_Painting"],
                RepairFinishingMessage.Cancelled => text["FinishingTool_Status_Cancelled"],
                RepairFinishingMessage.Committed => text["FinishingTool_Status_Committed"],
                _ => text[finishing.Kind == RepairFinishingKind.Blur
                    ? "BlurTool_Status_Ready" : "SmudgeTool_Status_Ready"],
            };
            return true;
        }
        status = "";
        return false;
    }

    private static bool TryRead(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, LocalizationService.Current.Culture, out number);

    private void ShowToolInputError() => MessageBox.Show(this,
        LocalizationService.Current["Tool_InvalidNumber"],
        LocalizationService.Current["Edit_ErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);

    private void ShowFinishingInputError() => MessageBox.Show(this,
        LocalizationService.Current["FinishingTool_InvalidNumber"],
        LocalizationService.Current["Edit_ErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
}
