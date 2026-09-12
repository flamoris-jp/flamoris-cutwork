using System.Globalization;
using System.IO;
using System.Windows;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.App.Controls;
using Flamoris.Cutwork.Imaging;
using Microsoft.Win32;

namespace Flamoris.Cutwork.App;

public partial class MainWindow : Window
{
    private readonly EditorSession _session = new();
    private readonly ImageImportService _imageImporter = new();
    private readonly PartToolController _partTool;
    private readonly MaskBrushController _maskTool;
    private readonly PatchToolController _patchTool;
    private readonly CloneRepairController _cloneTool;
    private readonly CanvasInputRouter _inputRouter;
    private DocumentPoint? _pointerPosition;

    public MainWindow()
    {
        InitializeComponent();
        _partTool = new PartToolController(_session, new GuriguriPartFitter());
        _maskTool = new MaskBrushController(_session);
        _patchTool = new PatchToolController(_session, new PatchSourceSampler());
        _cloneTool = new CloneRepairController(_session, new CloneRepairKernel());
        _inputRouter = new CanvasInputRouter(_session);
        InitializeLayerEditing();
        CanvasView.AttachSession(_session);
        CanvasView.AttachInputRouter(_inputRouter, _partTool, _maskTool, _patchTool, _cloneTool);
        CanvasView.PointerDocumentPositionChanged += CanvasView_PointerDocumentPositionChanged;
        CanvasView.ViewportChanged += (_, _) => UpdateStatus();
        CanvasView.EditRejected += (_, e) => ShowEditError(e.Exception);
        _partTool.Changed += (_, _) =>
        {
            UpdatePartToolUi();
            UpdateStatus();
        };
        _maskTool.Changed += (_, _) =>
        {
            UpdatePhase4ToolUi();
            UpdateStatus();
        };
        _patchTool.Changed += (_, _) =>
        {
            UpdatePhase4ToolUi();
            UpdateStatus();
        };
        _cloneTool.Changed += (_, _) =>
        {
            UpdatePhase4ToolUi();
            UpdateStatus();
        };
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        var text = LocalizationService.Current;
        Title = text["AppTitle"];
        FileMenu.Header = text["Menu_File"];
        OpenMenuItem.Header = text["Menu_File_Open"];
        ExitMenuItem.Header = text["Menu_File_Exit"];
        EditMenu.Header = text["Menu_Edit"];
        ViewMenu.Header = text["Menu_View"];
        FitMenuItem.Header = text["Menu_View_Fit"];
        ActualSizeMenuItem.Header = text["Menu_View_ActualSize"];
        OriginalMenuItem.Header = text["Menu_View_Original"];
        CompositeMenuItem.Header = text["Menu_View_Composite"];
        LanguageMenu.Header = text["Menu_View_Language"];
        JapaneseMenuItem.Header = text["Language_Japanese"];
        EnglishMenuItem.Header = text["Language_English"];
        LayerMenu.Header = text["Menu_Layer"];
        ExportMenu.Header = text["Menu_Export"];
        HelpMenu.Header = text["Menu_Help"];
        ToolRailLabel.Text = text["Panel_Tools"];
        PartToolButton.Content = text["PartTool_Name"];
        PartToolButton.ToolTip = text["PartTool_Tooltip"];
        PartCommitButton.Content = text["PartTool_Commit"];
        PartCancelButton.Content = text["PartTool_Cancel"];
        LocalizePhase4Tools();
        LayersGroup.Header = text["Panel_Layers"];
        PropertiesGroup.Header = text["Panel_Properties"];
        CanvasView.EmptyText = text["Canvas_NoDocument"];
        UpdateStatus();
        JapaneseMenuItem.IsChecked = text.Culture.Name == "ja-JP";
        EnglishMenuItem.IsChecked = text.Culture.Name == "en-US";
        LocalizeLayerEditing();
        UpdatePartToolUi();
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = LocalizationService.Current["Dialog_ImageFilter"],
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var result = _imageImporter.Import(dialog.FileName);
        if (!result.IsSuccess)
        {
            ShowImportError(result.Error ?? ImageImportError.InvalidImage);
            return;
        }

        _inputRouter.CancelActiveTool();
        var document = new CutworkDocument(result.Original!);
        _session.Open(document);
        _pointerPosition = null;
        CanvasView.Present(document);
        FitMenuItem.IsEnabled = true;
        ActualSizeMenuItem.IsEnabled = true;
        OriginalMenuItem.IsEnabled = true;
        CompositeMenuItem.IsEnabled = true;
        PartToolButton.IsEnabled = true;
        MaskToolButton.IsEnabled = true;
        PatchToolButton.IsEnabled = true;
        CloneToolButton.IsEnabled = true;
        UpdatePreviewChecks();
        ApplyLocalization();
    }

    private void FitMenuItem_Click(object sender, RoutedEventArgs e) => CanvasView.Fit();

    private void ActualSizeMenuItem_Click(object sender, RoutedEventArgs e) => CanvasView.ActualSize();

    private void CanvasView_PointerDocumentPositionChanged(object? sender, DocumentPointerEventArgs e)
    {
        _pointerPosition = e.Position;
        UpdateStatus();
    }

    private void OriginalMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _session.SetPreviewSource(PreviewSource.Original);
        UpdatePreviewChecks();
    }

    private void CompositeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _session.SetPreviewSource(PreviewSource.Composite);
        UpdatePreviewChecks();
    }

    private void UpdatePreviewChecks()
    {
        OriginalMenuItem.IsChecked = _session.PreviewSource == PreviewSource.Original;
        CompositeMenuItem.IsChecked = _session.PreviewSource == PreviewSource.Composite;
    }

    private void UpdateStatus()
    {
        var text = LocalizationService.Current;
        if (_session.Document is null)
        {
            Title = text["AppTitle"];
            StatusText.Text = text["Status_Ready"];
            return;
        }

        var document = _session.Document;
        Title = $"{text["AppTitle"]} — {document.Original.SourceName}";
        if (_session.IsDirty) Title += text["Status_UnsavedMarker"];
        if (TryGetPhase4Status(out var phase4Status))
        {
            StatusText.Text = phase4Status;
            return;
        }
        if (_partTool.IsActive)
        {
            var snapshot = _partTool.Snapshot();
            StatusText.Text = snapshot.Status.Message switch
            {
                PartToolMessage.DrawingFence => string.Format(text.Culture,
                    text["PartTool_Status_DrawingFence"], snapshot.Fence.Count),
                PartToolMessage.NeedsThreePoints => string.Format(text.Culture,
                    text["PartTool_Status_NeedsThreePoints"], snapshot.Fence.Count),
                PartToolMessage.FenceTooSmall => string.Format(text.Culture,
                    text["PartTool_Status_FenceTooSmall"], snapshot.Status.Value),
                PartToolMessage.FittingPreview => string.Format(text.Culture,
                    text["PartTool_Status_FittingPreview"], snapshot.Step,
                    snapshot.RemainingPixels, snapshot.FencePixels),
                PartToolMessage.Cancelled => text["PartTool_Status_Cancelled"],
                PartToolMessage.Committed => text["PartTool_Status_Committed"],
                _ => text["PartTool_Status_Ready"],
            };
            return;
        }
        StatusText.Text = _pointerPosition is { } point
            ? string.Format(
                text.Culture,
                text["Status_CanvasPosition"],
                point.X,
                point.Y,
                CanvasView.Zoom * 100.0)
            : string.Format(
                text.Culture,
                text["Status_ImageOpened"],
                document.Original.SourceName,
                document.Dimensions.Width,
                document.Dimensions.Height,
                CanvasView.Zoom * 100.0);
    }

    private void ShowImportError(ImageImportError error)
    {
        var key = error switch
        {
            ImageImportError.UnsupportedFormat => "Error_UnsupportedFormat",
            ImageImportError.IoFailure => "Error_IoFailure",
            _ => "Error_InvalidImage",
        };
        MessageBox.Show(
            this,
            LocalizationService.Current[key],
            LocalizationService.Current["Error_Title"],
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void JapaneseMenuItem_Click(object sender, RoutedEventArgs e)
    {
        LocalizationService.Current.SetCulture(new CultureInfo("ja-JP"));
        ApplyLocalization();
    }

    private void EnglishMenuItem_Click(object sender, RoutedEventArgs e)
    {
        LocalizationService.Current.SetCulture(new CultureInfo("en-US"));
        ApplyLocalization();
    }

    private void PartTool_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Document is null) return;
        _inputRouter.SetActiveTool(_partTool);
        UpdatePartToolUi();
        CanvasView.Focus();
    }

    private void PartCommit_Click(object sender, RoutedEventArgs e)
    {
        try { _partTool.Commit(); }
        catch (EditException exception) { ShowEditError(exception); }
        CanvasView.Focus();
    }

    private void PartCancel_Click(object sender, RoutedEventArgs e)
    {
        _inputRouter.CancelActiveTool();
        CanvasView.Focus();
    }

    private void UpdatePartToolUi()
    {
        PartToolButton.IsChecked = ReferenceEquals(_inputRouter.ActiveTool, _partTool);
        PartCommitButton.Visibility = _partTool.State == PartToolState.FittingPreview
            ? Visibility.Visible : Visibility.Collapsed;
        PartCancelButton.Visibility = _partTool.State != PartToolState.Idle
            ? Visibility.Visible : Visibility.Collapsed;
        UpdatePhase4ToolUi();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();
}
