using System.Windows;
using System.Windows.Threading;
using TwitchChatMvp.Services;
using TwitchChatMvp.Views;

namespace TwitchChatMvp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var settings = new SettingsService().Load();
        LocalizationService.ApplyToResources(settings.Language);
        AnimationService.SetReduceMotion(settings.ReduceMotion);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        SilentDialog.ShowMessage(
            AppInfo.Name,
            LocalizationService.Get(new SettingsService().Load().Language, "UnexpectedError") + "\n\n" + e.Exception.Message);
        e.Handled = true;
    }
}
