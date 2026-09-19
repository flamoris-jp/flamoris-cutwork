using System.Globalization;
using System.Windows;

namespace Flamoris.Cutwork.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, args) => CutworkLog.Current.Error(
            "app", "Unhandled dispatcher exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => CutworkLog.Current.Error(
            "app", "Fatal application exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => CutworkLog.Current.Error(
            "app", "Unobserved task exception", args.Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            LocalizationService.Current.SetCulture(new CultureInfo("ja-JP"));
            CutworkLog.Current.Info("app.startup", "Application started", new Dictionary<string, object?>
            {
                ["version"] = typeof(App).Assembly.GetName().Version?.ToString(),
                ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            });
            base.OnStartup(e);
        }
        catch (Exception exception)
        {
            CutworkLog.Current.Error("app.startup", "Application startup failed", exception);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CutworkLog.Current.Info("app.shutdown", "Application stopped", new Dictionary<string, object?>
        {
            ["exitCode"] = e.ApplicationExitCode,
        });
        base.OnExit(e);
    }
}
