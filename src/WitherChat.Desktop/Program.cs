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
        if (args.Length > 0 && args[0] == "--inspect-obs-plugin")
        {
            if (args.Length != 2) return (int)ObsPluginInstallCode.InvalidTarget;
            var inspection = new WindowsObsPluginService().InspectAsync(args[1], CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                State = inspection.State.ToString(), inspection.Directory, inspection.CanInstall, inspection.ObsRunning, inspection.CanRemove
            }));
            return 0;
        }
        if (args.Length > 0 && args[0] == "--remove-obs-plugin")
            return args.Length == 2 ? (int)WindowsObsPluginService.RunRemover(args[1]).Code : (int)ObsPluginInstallCode.InvalidTarget;
        if (args.Length > 0 && args[0] == "--install-obs-plugin")
            return args.Length == 2 ? (int)WindowsObsPluginService.RunInstaller(args[1]).Code : (int)ObsPluginInstallCode.InvalidTarget;
        AppDiagnostics.Initialize(Environment.GetEnvironmentVariable("WITHERCHAT_UI_DATA_DIRECTORY"));
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
        var isDockLaunch = args.Length > 0 && args[0] == "--obs-dock";
        ObsDockRequest dockRequest = default;
        if (isDockLaunch && (!ObsDockRequest.TryParseArguments(args, out dockRequest) ||
            !WindowsObsDockHost.IsValidParent(dockRequest))) return 2;
#if DEBUG
        var mutexName = Environment.GetEnvironmentVariable("WITHERCHAT_UI_MUTEX_NAME");
        using var singleInstance = SingleInstanceGuard.Acquire(mutexName);
#else
        using var singleInstance = SingleInstanceGuard.Acquire();
#endif
        if (!singleInstance.IsFirstInstance)
        {
            if (isDockLaunch)
                return ObsDockIpcService.RequestAsync(dockRequest).GetAwaiter().GetResult() ? 0 : 3;
            App.SingleInstanceNotification = LoadSingleInstanceNotification(
                Environment.GetEnvironmentVariable("WITHERCHAT_UI_DATA_DIRECTORY"));
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        App.InitialObsDockRequest = isDockLaunch ? dockRequest : null;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(isDockLaunch ? [] : args);
    }

    internal static SingleInstanceNotification LoadSingleInstanceNotification(string? dataDirectory)
    {
        try
        {
            var paths = new AppDataPaths(dataDirectory);
            using var settingsStore = new SettingsStore(paths);
            var settings = settingsStore.Load();
            return new SingleInstanceNotification(settings.Language, settings.Theme);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SingleInstanceNotification("ru", "Dark");
        }
    }

    // The same process can be launched standalone and subsequently reparented
    // into OBS. ANGLE can repeatedly lose its surface during HWND/session
    // transitions, recreating native devices on every render tick. Software
    // rendering keeps both entry points on the same stable Skia drawing path.
    internal static Win32PlatformOptions CreateWindowsPlatformOptions() => new()
    {
        RenderingMode = [Win32RenderingMode.Software]
    };

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();
        if (OperatingSystem.IsWindows())
        {
            builder.With(CreateWindowsPlatformOptions());
        }
#if DEBUG
        builder.LogToTrace();
#endif
        return builder;
    }
}
