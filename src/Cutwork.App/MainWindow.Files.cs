using System.ComponentModel;
using System.IO;
using System.Windows;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Imaging.Persistence;
using Microsoft.Win32;

namespace Flamoris.Cutwork.App;

public partial class MainWindow
{
    private bool _closeApproved;

    private void OpenProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = LocalizationService.Current["Dialog_ProjectFilter"],
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true || !ConfirmUnsavedChanges()) return;

        try
        {
            _inputRouter.CancelActiveTool();
            _workspace.OpenProject(dialog.FileName);
            CompleteDocumentOpen(_session.Document!);
        }
        catch (FlimgException exception) { ShowProjectError(exception); }
    }

    private void SaveMenuItem_Click(object sender, RoutedEventArgs e) => SaveProject(forceSaveAs: false);

    private void SaveAsMenuItem_Click(object sender, RoutedEventArgs e) => SaveProject(forceSaveAs: true);

    private bool SaveProject(bool forceSaveAs)
    {
        if (_session.Document is null) return false;
        var path = forceSaveAs ? null : _workspace.ProjectPath;
        if (path is null)
        {
            var dialog = new SaveFileDialog
            {
                Filter = LocalizationService.Current["Dialog_ProjectFilter"],
                DefaultExt = ".flimg",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = SuggestedFileName(_session.Document.Original.SourceName),
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }

        try
        {
            _inputRouter.CancelActiveTool();
            _workspace.Save(path);
            UpdateStatus();
            return true;
        }
        catch (FlimgException exception)
        {
            ShowProjectError(exception);
            return false;
        }
    }

    private void ExportCompositeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Document is not { } document) return;
        var dialog = new SaveFileDialog
        {
            Filter = LocalizationService.Current["Dialog_PngFilter"],
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = SuggestedFileName(document.Original.SourceName) + "-composite",
        };
        if (dialog.ShowDialog(this) != true) return;
        TryExport(() => _exporter.ExportComposite(document, dialog.FileName));
    }

    private void ExportHandoffMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Document is not { } document) return;
        var dialog = new SaveFileDialog
        {
            Filter = LocalizationService.Current["Dialog_HandoffFilter"],
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = SuggestedFileName(document.Original.SourceName) + "-layers",
        };
        if (dialog.ShowDialog(this) != true) return;
        TryExport(() => _exporter.ExportLayerHandoff(document, dialog.FileName));
    }

    private void TryExport(Action export)
    {
        try { export(); }
        catch (FlimgException exception) { ShowProjectError(exception); }
    }

    private bool ConfirmUnsavedChanges()
    {
        if (!_session.IsDirty) return true;
        var text = LocalizationService.Current;
        var answer = MessageBox.Show(this, text["UnsavedChanges_Message"],
            text["UnsavedChanges_Title"], MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return answer switch
        {
            MessageBoxResult.Yes => SaveProject(forceSaveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void CompleteDocumentOpen(CutworkDocument document)
    {
        _inputRouter.SetActiveTool(null);
        _cloneTool.ResetSource();
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
        BlurToolButton.IsEnabled = true;
        SmudgeToolButton.IsEnabled = true;
        UpdateFileCommandState();
        UpdatePreviewChecks();
        ApplyLocalization();
    }

    private void UpdateFileCommandState()
    {
        var enabled = _session.Document is not null;
        SaveMenuItem.IsEnabled = enabled;
        SaveAsMenuItem.IsEnabled = enabled;
        ExportCompositeMenuItem.IsEnabled = enabled;
        ExportHandoffMenuItem.IsEnabled = enabled;
    }

    private void ShowProjectError(FlimgException exception)
    {
        var text = LocalizationService.Current;
        var key = exception.Error switch
        {
            FlimgError.IoFailure => "ProjectError_IoFailure",
            FlimgError.UnsupportedVersion => "ProjectError_UnsupportedVersion",
            FlimgError.SizeLimitExceeded => "ProjectError_SizeLimit",
            _ => "ProjectError_InvalidFile",
        };
        MessageBox.Show(this, text[key], text["ProjectError_Title"],
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string SuggestedFileName(string sourceName)
    {
        var name = Path.GetFileNameWithoutExtension(sourceName);
        foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        name = name.Trim().TrimEnd('.');
        return string.IsNullOrEmpty(name) ? "cutwork" : name;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        if (!ConfirmUnsavedChanges())
        {
            e.Cancel = true;
            return;
        }
        _closeApproved = true;
    }
}
