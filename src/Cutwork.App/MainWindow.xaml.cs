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
    private DocumentPoint? _pointerPosition;

    public MainWindow()
    {
        InitializeComponent();
        InitializeLayerEditing();
        CanvasView.AttachSession(_session);
        CanvasView.PointerDocumentPositionChanged += CanvasView_PointerDocumentPositionChanged;
        CanvasView.ViewportChanged += (_, _) => UpdateStatus();
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
        LayersGroup.Header = text["Panel_Layers"];
        PropertiesGroup.Header = text["Panel_Properties"];
        CanvasView.EmptyText = text["Canvas_NoDocument"];
        UpdateStatus();
        JapaneseMenuItem.IsChecked = text.Culture.Name == "ja-JP";
        EnglishMenuItem.IsChecked = text.Culture.Name == "en-US";
        LocalizeLayerEditing();
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

        var document = new CutworkDocument(result.Original!);
        _session.Open(document);
        _pointerPosition = null;
        CanvasView.Present(document);
        FitMenuItem.IsEnabled = true;
        ActualSizeMenuItem.IsEnabled = true;
        OriginalMenuItem.IsEnabled = true;
        CompositeMenuItem.IsEnabled = true;
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

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();
}
