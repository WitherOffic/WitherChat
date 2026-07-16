using System.Windows;
using System.Windows.Threading;
using System.Threading;
using WitherChat.Services;
using WitherChat.Views;

namespace WitherChat;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\WitherChat-9E69A68D-87D9-47F1-99AE-F35AA2DCC3EA";
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        var settings = new SettingsService().Load();
        if (!isFirstInstance)
        {
            var language = settings.Language;
            LocalizationService.ApplyToResources(language);
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            SilentDialog.ShowMessage(
                AppInfo.Name,
                LocalizationService.Get(language, "AlreadyRunning"));
            Shutdown();
            return;
        }

        LocalizationService.ApplyToResources(settings.Language);
        AnimationService.SetReduceMotion(settings.ReduceMotion);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        FileLogger.ShutdownAsync().GetAwaiter().GetResult();

        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.RequestApplicationExit();
        }

        base.OnSessionEnding(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        new FileLogger().Error("Unhandled UI exception", e.Exception);
        SilentDialog.ShowMessage(
            AppInfo.Name,
            LocalizationService.Get(LocalizationService.CurrentLanguage, "UnexpectedError"));
        e.Handled = true;
        if (Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.RequestApplicationExit();
        }
        else
        {
            Current.Shutdown(-1);
        }
    }
}
