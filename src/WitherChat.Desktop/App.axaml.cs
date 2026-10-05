using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.Platforms;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;
using WitherChat.Desktop.Views;

namespace WitherChat.Desktop;

#pragma warning disable CA2007 // Application shutdown and URI callbacks must resume on the Avalonia UI context.

[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "Avalonia creates the application type from compiled XAML.")]
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Avalonia owns the Application lifetime; RequestExitAsync disposes the view model and tray icon.")]
public partial class App : Application
{
    private const string RestartParentPidEnvironmentVariable = "WITHERCHAT_RESTART_PARENT_PID";
    private MainWindow? _mainWindow;
    private DonationAlertsWindow? _donationAlertsWindow;
    private MainWindowViewModel? _mainViewModel;
    private TrayIcon? _trayIcon;
    private SingleInstanceActivationService? _singleInstanceActivationService;
    private bool _trayMenuRefreshPending;
    private WindowsObsDockHost? _obsDockHost;
    private WindowsObsDockHost? _obsDonationDockHost;
    private ObsDockIpcService? _obsDockIpcService;
    internal static ObsDockRequest? InitialObsDockRequest { get; set; }

    public bool IsExitRequested { get; private set; }
    public bool IsSystemShutdownRequested { get; private set; }
    public bool IsTrayAvailable => _trayIcon?.IsVisible == true;
    internal static SingleInstanceNotification? SingleInstanceNotification { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (SingleInstanceNotification is { } notification)
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                ApplyTheme(notification.Theme);
                var notificationWindow = new SingleInstanceWindow(notification.Language);
                desktop.MainWindow = notificationWindow;
                notificationWindow.Show();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var testDataDirectory = Environment.GetEnvironmentVariable("WITHERCHAT_UI_DATA_DIRECTORY");
            var paths = new AppDataPaths(testDataDirectory);
            var settingsStore = new SettingsStore(paths);
            var settings = settingsStore.Load();
            var platform = PlatformDescriptorFactory.CreateCurrent();
            var chatClient = new TwitchIrcClient();
            var authService = new TwitchAuthService();
            var authSessionStore = new TwitchAuthSessionStore(paths);
            var chatApiClient = new TwitchChatApiClient();
            var eventSubClient = new TwitchEventSubClient(chatApiClient);
            var chatLogWriter = new ChatLogWriter(paths.LogDirectory);
            var obsOverlayServer = new ObsOverlayServer();
            var moderationCacheStore = new ModerationCacheStore(paths);
            var youTubeAuthService = new YouTubeAuthService();
            var youTubeAuthSessionStore = new YouTubeAuthSessionStore(paths);
            var youTubeLiveChatClient = new YouTubeLiveChatClient(youTubeAuthService);
            var donationAlertsAuthService = new DonationAlertsAuthService();
            var donationAlertsAuthSessionStore = new DonationAlertsAuthSessionStore(paths);
            var donationAlertsClient = new DonationAlertsClient();
            var donationAlertsObsController = new DonationAlertsObsController(paths);
            var streamMomentStore = new StreamMomentStore(paths);

            _mainViewModel = new MainWindowViewModel(
                chatClient,
                authService,
                authSessionStore,
                chatApiClient,
                eventSubClient,
                settingsStore,
                chatLogWriter,
                obsOverlayServer,
                moderationCacheStore,
                settings,
                platform,
                youTubeAuthService,
                youTubeAuthSessionStore,
                youTubeLiveChatClient,
                donationAlertsAuthService,
                donationAlertsAuthSessionStore,
                donationAlertsClient,
                donationAlertsObsController,
                streamMomentStore);
            _mainViewModel.ExitRequested += OnExitRequested;
            _mainViewModel.ThemeChanged += OnThemeChanged;
            _mainViewModel.LanguageChanged += OnLanguageChanged;
            _mainViewModel.FontFamilyChanged += OnFontFamilyChanged;
            _mainViewModel.SavedChannels.CollectionChanged += OnSavedChannelsChanged;
            _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
            _mainViewModel.DonationWindowRequested += OnDonationWindowRequested;
#if DEBUG
            var testTheme = Environment.GetEnvironmentVariable("WITHERCHAT_UI_THEME");
            ApplyTheme(testTheme is "Dark" or "Light" or "System" ? testTheme : _mainViewModel.Theme);
            var testLanguage = Environment.GetEnvironmentVariable("WITHERCHAT_UI_LANGUAGE");
            if (testLanguage is "ru" or "en")
            {
                _mainViewModel.ApplyLanguageForTesting(testLanguage);
            }
#else
            ApplyTheme(_mainViewModel.Theme);
#endif
            ApplyFontFamily(_mainViewModel.UiFontFamily);

            _mainWindow = new MainWindow
            {
                DataContext = _mainViewModel
            };
            desktop.MainWindow = _mainWindow;
            if (platform.SupportsSystemTray)
            {
                TryCreateTrayIcon();
            }

            // Explicit shutdown mode keeps the application alive in the tray, but it also
            // means the lifetime does not guarantee that the assigned main window is shown.
            // A normal launch must always present the UI once; subsequent close actions may
            // intentionally hide it according to the user's tray setting.
            if (OperatingSystem.IsWindows())
            {
                _obsDockHost = new WindowsObsDockHost(_mainWindow, ShowMainWindow);
                _obsDockIpcService = new ObsDockIpcService(async (request, cancellationToken) =>
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        IsExitRequested ? "ERROR shutting-down" : HandleObsDockRequest(request),
                        DispatcherPriority.Normal, cancellationToken));
            }
            if (InitialObsDockRequest is { } initialDock && _obsDockHost is not null)
            {
                var result = _obsDockHost.Handle(initialDock);
                InitialObsDockRequest = null;
                if (!result.StartsWith("OK ", StringComparison.Ordinal))
                {
                    AppDiagnostics.Write("OBS dock startup", new InvalidOperationException(result));
                    _ = RequestExitAsync();
                    // Do not allocate activation services or initialize the chat while exiting.
                    base.OnFrameworkInitializationCompleted();
                    return;
                }
            }
            else
            {
                _mainWindow.PrepareWindowOpenAnimation();
                _mainWindow.Show();
                _ = _mainWindow.PlayWindowOpenAnimationAsync();
            }
            _singleInstanceActivationService = new SingleInstanceActivationService(
                () => Dispatcher.UIThread.Post(ShowMainWindow));
            _ = _mainViewModel.InitializeAsync();
#if DEBUG
            if (string.Equals(
                    Environment.GetEnvironmentVariable("WITHERCHAT_UI_ONBOARDING"),
                    "1",
                    StringComparison.Ordinal))
            {
                DispatcherTimer.RunOnce(
                    () => _mainViewModel.StartOnboardingCommand.Execute(null),
                    TimeSpan.FromMilliseconds(650));
            }
            if (string.Equals(
                    Environment.GetEnvironmentVariable("WITHERCHAT_UI_STRESS"),
                    "1",
                    StringComparison.Ordinal))
            {
                _mainViewModel.LoadStressMessagesForTesting();
                if (string.Equals(
                        Environment.GetEnvironmentVariable("WITHERCHAT_UI_OPEN_COMPACT"),
                        "1",
                        StringComparison.Ordinal))
                {
                    _mainWindow.EnterCompactModeForTesting();
                }
                if (string.Equals(
                        Environment.GetEnvironmentVariable("WITHERCHAT_UI_BROWSE_HISTORY"),
                        "1",
                        StringComparison.Ordinal))
                {
                    DispatcherTimer.RunOnce(
                        _mainWindow.BrowseMessageHistoryForTesting,
                        TimeSpan.FromMilliseconds(1_500));
                }
                if (string.Equals(
                        Environment.GetEnvironmentVariable("WITHERCHAT_UI_STRESS_FEED"),
                        "1",
                        StringComparison.Ordinal))
                {
                    _mainViewModel.StartStressFeedForTesting();
                }
            }
            if (string.Equals(
                    Environment.GetEnvironmentVariable("WITHERCHAT_UI_OPEN_MODERATION"),
                    "1",
                    StringComparison.Ordinal))
            {
                _mainViewModel.OpenModerationForTesting();
            }
            if (string.Equals(
                    Environment.GetEnvironmentVariable("WITHERCHAT_UI_MEDIA_LOADING"),
                    "1",
                    StringComparison.Ordinal))
            {
                DispatcherTimer.RunOnce(
                    () => _mainViewModel.IsChatMediaLoading = true,
                    TimeSpan.FromSeconds(1));
            }
#else
            DispatcherTimer.RunOnce(
                _mainViewModel.StartInitialOnboarding,
                TimeSpan.FromMilliseconds(650));
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void ShowMainWindow()
    {
        if (IsExitRequested || _mainWindow is null)
        {
            return;
        }

        if (_obsDockHost?.IsAttached == true)
        {
            _obsDonationDockHost?.Detach(show: true);
            _obsDockHost.Detach(show: false);
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.PrepareWindowOpenAnimation();
            _mainWindow.Show();
            _ = _mainWindow.PlayWindowOpenAnimationAsync();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            var platformHandle = _mainWindow.TryGetPlatformHandle();
            if (platformHandle is null ||
                !string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.Ordinal) ||
                !WindowsWindowAnimation.TryRestore(platformHandle.Handle))
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
        }

        _mainWindow.Activate();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The application exit boundary must always release the tray and terminate the desktop lifetime.")]
    public async Task RequestExitAsync()
    {
        if (IsExitRequested)
        {
            return;
        }

        IsExitRequested = true;
        try
        {
            if (_obsDockIpcService is not null)
            {
                var dockService = _obsDockIpcService;
                _obsDockIpcService = null;
                await dockService.DisposeAsync();
            }
            _obsDonationDockHost?.Dispose();
            _obsDonationDockHost = null;
            _obsDockHost?.Dispose();
            _obsDockHost = null;
            if (_singleInstanceActivationService is not null)
            {
                var activationService = _singleInstanceActivationService;
                _singleInstanceActivationService = null;
                await activationService.DisposeAsync();
            }
            if (_mainViewModel is not null)
            {
                var viewModel = _mainViewModel;
                _mainViewModel = null;
                viewModel.ExitRequested -= OnExitRequested;
                viewModel.ThemeChanged -= OnThemeChanged;
                viewModel.LanguageChanged -= OnLanguageChanged;
                viewModel.FontFamilyChanged -= OnFontFamilyChanged;
                viewModel.SavedChannels.CollectionChanged -= OnSavedChannelsChanged;
                viewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
                viewModel.DonationWindowRequested -= OnDonationWindowRequested;
                if (_donationAlertsWindow is not null)
                {
                    _donationAlertsWindow.ObsDockBackToChatRequested -= OnObsDockBackToChatRequested;
                    _donationAlertsWindow.CloseForApplicationExit();
                    _donationAlertsWindow = null;
                }
                await viewModel.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write("Application shutdown cleanup", exception);
        }
        finally
        {
            if (_trayIcon is not null)
            {
                _trayIcon.IsVisible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }

    internal void PrepareForSystemShutdown() => IsSystemShutdownRequested = true;

    internal void CompleteSystemShutdown(bool isEnding)
    {
        IsSystemShutdownRequested = isEnding;
        if (isEnding)
        {
            _ = RequestExitAsync();
        }
    }

    private void OnDonationWindowRequested(object? sender, EventArgs eventArgs) => ShowDonationWindow();

    public void ShowDonationWindow()
    {
        if (_mainViewModel is null)
        {
            return;
        }
        EnsureDonationWindow();
        if (_obsDockHost?.IsAttached == true)
        {
            if (_obsDonationDockHost?.IsAttached == true) _donationAlertsWindow!.Show();
            _obsDockHost.ShowDonationsTab();
            return;
        }
        if (!_donationAlertsWindow.IsVisible)
        {
            _donationAlertsWindow.Show();
        }
        if (_donationAlertsWindow.WindowState == WindowState.Minimized)
        {
            _donationAlertsWindow.WindowState = WindowState.Normal;
        }
        _donationAlertsWindow.Activate();
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_donationAlertsWindow))]
    private void EnsureDonationWindow()
    {
        if (_donationAlertsWindow is not null) return;
        _donationAlertsWindow = new DonationAlertsWindow { DataContext = _mainViewModel };
        _donationAlertsWindow.ObsDockBackToChatRequested += OnObsDockBackToChatRequested;
    }

    private void OnObsDockBackToChatRequested(object? sender, EventArgs args) => _obsDonationDockHost?.ShowChatTab();

    private string HandleObsDockRequest(ObsDockRequest request)
    {
        if (!request.Attach)
        {
            if (_obsDonationDockHost?.Owns(request) == true) return _obsDonationDockHost.Handle(request);
            if (_obsDockHost?.Owns(request) == true) _obsDonationDockHost?.Detach(show: true);
            return _obsDockHost?.Handle(request) ?? "ERROR shutting-down";
        }
        if (!request.Donations) return _obsDockHost?.Handle(request) ?? "ERROR shutting-down";
        if (_obsDockHost?.OwnerProcessId != request.ProcessId || !WindowsObsDockHost.IsValidParent(request))
            return "ERROR main-chat-not-attached";
        EnsureDonationWindow();
        _obsDonationDockHost ??= new WindowsObsDockHost(_donationAlertsWindow!, () => _donationAlertsWindow!.Show());
        return _obsDonationDockHost.Handle(request);
    }

    private void TryCreateTrayIcon()
    {
        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://WitherChat/Assets/WitherChat.ico"));
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                ToolTipText = "WitherChat",
                Menu = CreateTrayMenu(),
                IsVisible = true
            };
        }
        catch (Exception)
        {
            _trayIcon = null;
        }
    }

    private NativeMenu CreateTrayMenu()
    {
        if (_mainViewModel is not { } viewModel)
        {
            return new NativeMenu();
        }

        return TrayMenuFactory.Create(
            viewModel.Texts,
            viewModel.SavedChannels,
            ShowMainWindow,
            ActivateTrayChannel,
            RestartApplication,
            ExitFromTray);
    }

    private void ActivateTrayChannel(string login)
    {
        if (_mainViewModel is null)
        {
            return;
        }

        var channel = _mainViewModel.SavedChannels.FirstOrDefault(item =>
            string.Equals(item.Login, login, StringComparison.OrdinalIgnoreCase));
        if (channel is not null &&
            _mainViewModel.SwitchSavedChannelCommand.CanExecute(channel))
        {
            _mainViewModel.SwitchSavedChannelCommand.Execute(channel);
        }

        ShowMainWindow();
    }

    private void RefreshTrayMenu()
    {
        if (OperatingSystem.IsMacOS() || _trayIcon is null || _trayMenuRefreshPending)
        {
            return;
        }

        _trayMenuRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _trayMenuRefreshPending = false;
            try
            {
                if (_trayIcon is not null && !IsExitRequested)
                {
                    _trayIcon.Menu = CreateTrayMenu();
                }
            }
            catch (Exception)
            {
                if (_trayIcon is not null)
                {
                    _trayIcon.IsVisible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
            }
        }, DispatcherPriority.Background);
    }

    private void OnSavedChannelsChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) =>
        RefreshTrayMenu();

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MainWindowViewModel.SelectedSavedChannel) or
            nameof(MainWindowViewModel.Channel))
        {
            RefreshTrayMenu();
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Restart failure must leave the current application instance available.")]
    private async void RestartApplication()
    {
        if (IsExitRequested || string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return;
        }

        try
        {
            var startInfo = CreateRestartStartInfo(
                Environment.ProcessPath,
                Environment.GetCommandLineArgs().Skip(1),
                Environment.ProcessId);

            if (Process.Start(startInfo) is null)
            {
                return;
            }
        }
        catch (Exception)
        {
            ShowMainWindow();
            return;
        }

        await RequestExitAsync();
    }

    internal static ProcessStartInfo CreateRestartStartInfo(
        string processPath,
        IEnumerable<string> arguments,
        int parentProcessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment[RestartParentPidEnvironmentVariable] =
            parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return startInfo;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A stale or inaccessible restart parent must never prevent application startup.")]
    internal static void WaitForRestartParentExit()
    {
        var rawParentId = Environment.GetEnvironmentVariable(RestartParentPidEnvironmentVariable);
        if (!int.TryParse(
                rawParentId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parentProcessId) ||
            parentProcessId <= 0 ||
            parentProcessId == Environment.ProcessId)
        {
            return;
        }

        Environment.SetEnvironmentVariable(RestartParentPidEnvironmentVariable, null);

        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            _ = parent.WaitForExit(30_000);
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write("Wait for restart parent", exception);
        }
    }

    private async void ExitFromTray() => await RequestExitAsync();

    private async void OnExitRequested(object? sender, EventArgs eventArgs) => await RequestExitAsync();

    private void OnThemeChanged(object? sender, ValueEventArgs<string> eventArgs) => ApplyTheme(eventArgs.Value);

    private void OnLanguageChanged(object? sender, EventArgs eventArgs)
    {
        if (_mainViewModel is null)
        {
            return;
        }

        RefreshTrayMenu();
    }

    private void OnFontFamilyChanged(object? sender, ValueEventArgs<string> eventArgs) =>
        ApplyFontFamily(eventArgs.Value);

    private void ApplyFontFamily(string fontId)
    {
        var source = fontId switch
        {
            "Inter" => "avares://WitherChat/Assets/Fonts#Inter",
            "SegoeUI" when OperatingSystem.IsWindows() => "Segoe UI",
            "Aptos" when OperatingSystem.IsWindows() => "Aptos, Segoe UI Variable, Segoe UI",
            "Bahnschrift" when OperatingSystem.IsWindows() => "Bahnschrift, Segoe UI Variable, Segoe UI",
            "Calibri" when OperatingSystem.IsWindows() => "Calibri, Segoe UI",
            "Candara" when OperatingSystem.IsWindows() => "Candara, Segoe UI",
            "Trebuchet" when OperatingSystem.IsWindows() => "Trebuchet MS, Segoe UI",
            "SegoeUIVariable" when OperatingSystem.IsWindows() => "Segoe UI Variable, Segoe UI",
            _ => "avares://WitherChat/Assets/Fonts#Inter"
        };
        Resources["AppFontFamily"] = new FontFamily(source);
    }

    private void ApplyTheme(string theme)
    {
        RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
    }
}

#pragma warning restore CA2007
