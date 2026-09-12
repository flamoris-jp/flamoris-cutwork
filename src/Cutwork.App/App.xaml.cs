using System.Globalization;
using System.Windows;

namespace Flamoris.Cutwork.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        LocalizationService.Current.SetCulture(new CultureInfo("ja-JP"));
        base.OnStartup(e);
    }
}
