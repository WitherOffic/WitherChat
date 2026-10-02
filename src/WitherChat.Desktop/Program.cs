using Avalonia;
using System;
using WitherChat.Core.Services;
using WitherChat.Desktop.Platforms;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
#if DEBUG
        AppDiagnostics.Initialize(Environment.GetEnvironmentVariable("WITHERCHAT_UI_DATA_DIRECTORY"));
#else
        AppDiagnostics.Initialize();
#endif
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write("Program.Main", exception);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        App.WaitForRestartParentExit();
#if DEBUG
        var mutexName = Environment.GetEnvironmentVariable("WITHERCHAT_UI_MUTEX_NAME");
        using var singleInstance = SingleInstanceGuard.Acquire(mutexName);
#else
        using var singleInstance = SingleInstanceGuard.Acquire();
#endif
        if (!singleInstance.IsFirstInstance)
        {
            App.SingleInstanceNotification = LoadSingleInstanceNotification();
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static SingleInstanceNotification LoadSingleInstanceNotification()
    {
        try
        {
#if DEBUG
            var dataDirectory = Environment.GetEnvironmentVariable("WITHERCHAT_UI_DATA_DIRECTORY");
            var paths = new AppDataPaths(dataDirectory);
#else
            var paths = new AppDataPaths();
#endif
            using var settingsStore = new SettingsStore(paths);
            var settings = settingsStore.Load();
            return new SingleInstanceNotification(settings.Language, settings.Theme);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SingleInstanceNotification("ru", "Dark");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();
#if DEBUG
        builder.LogToTrace();
#endif
        return builder;
    }
}
