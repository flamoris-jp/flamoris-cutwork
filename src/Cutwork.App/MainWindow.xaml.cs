using System.Globalization;
using System.Windows;

namespace Flamoris.Cutwork.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
        CanvasPlaceholderText.Text = text["Canvas_NoDocument"];
        StatusText.Text = text["Status_Ready"];
        JapaneseMenuItem.IsChecked = text.Culture.Name == "ja-JP";
        EnglishMenuItem.IsChecked = text.Culture.Name == "en-US";
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
