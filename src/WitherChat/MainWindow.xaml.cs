using System.ComponentModel;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Threading;
using WitherChat.Controls;
using WitherChat.Models;
using WitherChat.Services;
using WitherChat.ViewModels;
using WitherChat.Views;

namespace WitherChat;

public enum ActiveOverlayPanel
{
    None,
    Settings,
    ChatLogs,
    CreatorControls,
    ConnectTwitch,
    SilentDialog
}

public partial class MainWindow : Window
{
    public static readonly DependencyProperty IsCompactChatModeProperty = DependencyProperty.Register(
        nameof(IsCompactChatMode),
        typeof(bool),
        typeof(MainWindow),
        new PropertyMetadata(false));

    private const double NormalMinimumWidth = 860;
    private const double NormalMinimumHeight = 560;
    private const double CompactMinimumWidth = 280;
    private const double CompactMinimumHeight = 180;
    private const double CompactDefaultWidth = 360;
    private const double CompactDefaultHeight = 320;
    private const string CompactIconGeometry =
        "M2,2 L7,7 M7,3 V7 H3 M18,2 L13,7 M13,3 V7 H17 M2,18 L7,13 M3,13 H7 V17 M18,18 L13,13 M17,13 H13 V17";
    private const string RestoreIconGeometry =
        "M7,7 L2,2 M2,6 V2 H6 M13,7 L18,2 M14,2 H18 V6 M7,13 L2,18 M2,14 V18 H6 M13,13 L18,18 M14,18 H18 V14";
    private const double FollowBottomThreshold = 32;
    private const double SmoothScrollDurationMs = 170;
    private const double SmoothScrollMaxFrameStepMs = 34;
    private const int SmoothScrollSettleFrameLimit = 4;
    private const double WheelScrollPixelsPerNotch = 72;
    private readonly ChatViewModel _viewModel = new();
    private readonly DispatcherTimer _userScrollIdleTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ScrollViewer? _messagesScrollViewer;
    private VirtualizingStackPanel? _messagesVirtualizingPanel;
    private long _scrollAnimationStarted;
    private double _scrollAnimationElapsedMs;
    private double _scrollStartOffset;
    private double _scrollTargetOffset;
    private double _scrollStartDistanceFromBottom;
    private double _scrollTargetDistanceFromBottom;
    private double _scrollRequestedDistanceFromBottom;
    private int _scrollSettleFrames;
    private int _scrollStableSettleFrames;
    private bool _scrollTargetsTop;
    private bool _followOnScrollComplete;
    private bool _isSmoothScrolling;
    private bool _isProgrammaticScroll;
    private bool _pendingScrollToBottom;
    private bool _userScrolledAwayFromBottom;
    private bool _followWhenUserScrollEnds;
    private int _scrollToBottomPasses;
    private bool _jumpButtonShown;
    private readonly Queue<object> _pendingAnimationItems = new();
    private readonly Dictionary<ListBoxItem, ChatMessageModel> _messageContainerModels = new();
    private bool _messageAnimationQueued;
    private bool _suppressPendingMessageAnimations;
    private bool _isUserScrolling;
    private long _lastUserScrollInputAt;
    private bool _shutdownInProgress;
    private bool _shutdownComplete;
    private bool _exitRequested;
    private bool _trayNoticeShown;
    private bool _windowTransitionInProgress;
    private TrayIconService? _trayIcon;
    private HwndSource? _windowSource;
    private Task? _initializationTask;
    private ActiveOverlayPanel _activeOverlay = ActiveOverlayPanel.None;
    private readonly SemaphoreSlim _overlayTransitionGate = new(1, 1);
    private bool _dialogOpen;
    private bool _dialogClosing;
    private bool _dialogResult;
    private DispatcherFrame? _dialogFrame;
    private ObservableCollection<ChatMessageModel>? _subscribedMessages;
    private ChannelSessionViewModel? _subscribedChannel;
    private long _scrollStateVersion;
    private long _channelSwitcherTransitionVersion;
    private int _headerPanelAnimationVersion;
    private int _composerPanelAnimationVersion;
    private bool _suppressPanelToggleAnimations;
    private bool _normalHeaderExpanded = true;
    private bool _normalComposerExpanded = true;
    private Rect _normalWindowBounds;
    private WindowState _normalWindowState = WindowState.Normal;
#if DEBUG
    private readonly DispatcherTimer _scrollDiagnosticsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _debugCollectionChanges;
    private long _debugScrollChanges;
    private long _debugScrollCommands;
    private long _debugLayoutPasses;
    private TimeSpan _debugLastFrameTime;
    private double _debugFrameMilliseconds;
    private int _debugFrameCount;
#endif

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        SilentDialog.RegisterHost(ShowSilentDialog);
        AttachMessageCollection(_viewModel.Messages);
        AttachActiveChannel(_viewModel.ActiveChannel);
        _viewModel.ActiveChannelChanging += ViewModel_ActiveChannelChanging;
        _viewModel.ActiveMessagesChanged += ViewModel_ActiveMessagesChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _trayIcon = TryCreateTrayIcon();
        MessagesList.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(MessagesList_PreviewMouseDown), true);
        _userScrollIdleTimer.Tick += UserScrollIdleTimer_Tick;
#if DEBUG
        _scrollDiagnosticsTimer.Tick += ScrollDiagnosticsTimer_Tick;
        CompositionTarget.Rendering += DebugCompositionTarget_Rendering;
        MessagesList.LayoutUpdated += MessagesList_LayoutUpdated;
        _scrollDiagnosticsTimer.Start();
#endif
        SizeChanged += (_, _) => UpdateOverlayPanelSize();
    }

    public bool IsCompactChatMode
    {
        get => (bool)GetValue(IsCompactChatModeProperty);
        private set => SetValue(IsCompactChatModeProperty, value);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Keep the root window fully rendered during startup. A whole-window
        // opacity animation can leave the WPF surface on its black first frame
        // when initialization and layout overlap.
        Opacity = 1;
        var initialization = _viewModel.InitializeAsync();
        _initializationTask = initialization;
        try
        {
            await initialization;
        }
        finally
        {
            if (ReferenceEquals(_initializationTask, initialization))
            {
                _initializationTask = null;
            }
        }
    }

    private void CustomTitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _ = ToggleWindowMaximizedAsync();
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            var pointer = e.GetPosition(this);
            var horizontalRatio = ActualWidth <= 0 ? 0.5 : Math.Clamp(pointer.X / ActualWidth, 0.0, 1.0);
            var screenPoint = PointToScreen(pointer);
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is { } compositionTarget)
            {
                screenPoint = compositionTarget.TransformFromDevice.Transform(screenPoint);
            }

            var restoredWidth = RestoreBounds.Width > 0 ? RestoreBounds.Width : Width;
            WindowState = WindowState.Normal;
            Left = screenPoint.X - (restoredWidth * horizontalRatio);
            Top = Math.Max(0, screenPoint.Y - 20);
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void CloseTitleBarButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void MinimizeTitleBarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_windowTransitionInProgress || WindowState == WindowState.Minimized)
        {
            return;
        }

        _windowTransitionInProgress = true;
        try
        {
            await AnimationService.AnimateWindowCloseAsync(this, offsetY: 8).ConfigureAwait(true);
            WindowState = WindowState.Minimized;
        }
        finally
        {
            AnimationService.ResetWindowVisuals(this);
            _windowTransitionInProgress = false;
        }
    }

    private async void MaximizeTitleBarButton_Click(object sender, RoutedEventArgs e) =>
        await ToggleWindowMaximizedAsync().ConfigureAwait(true);

    private async Task ToggleWindowMaximizedAsync()
    {
        if (_windowTransitionInProgress || WindowState == WindowState.Minimized)
        {
            return;
        }

        _windowTransitionInProgress = true;
        try
        {
            await AnimationService.AnimateWindowStateChangeAsync(
                    this,
                    () => WindowState = WindowState == WindowState.Maximized
                        ? WindowState.Normal
                        : WindowState.Maximized)
                .ConfigureAwait(true);
        }
        finally
        {
            AnimationService.ResetWindowVisuals(this);
            _windowTransitionInProgress = false;
        }
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != NativeMethods.WmGetMinMaxInfo || lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var bounds = NativeMethods.IsTaskbarAutoHidden()
            ? monitorInfo.Monitor
            : monitorInfo.WorkArea;
        var minMaxInfo = Marshal.PtrToStructure<NativeMethods.MinMaxInfo>(lParam);
        minMaxInfo.MaxPosition.X = bounds.Left - monitorInfo.Monitor.Left;
        minMaxInfo.MaxPosition.Y = bounds.Top - monitorInfo.Monitor.Top;
        minMaxInfo.MaxSize.X = bounds.Right - bounds.Left;
        minMaxInfo.MaxSize.Y = bounds.Bottom - bounds.Top;
        minMaxInfo.MaxTrackSize = minMaxInfo.MaxSize;
        Marshal.StructureToPtr(minMaxInfo, lParam, fDeleteOld: false);
        handled = true;
        return IntPtr.Zero;
    }

    private void CompactModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsCompactChatMode)
        {
            ExitCompactMode();
        }
        else
        {
            EnterCompactMode();
        }
    }

    private void EnterCompactMode()
    {
        _normalWindowState = WindowState;
        _normalWindowBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        _normalHeaderExpanded = HeaderPanelToggle.IsChecked == true;
        _normalComposerExpanded = ComposerPanelToggle.IsChecked == true;
        IsCompactChatMode = true;

        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        SetPanelsForCompactMode();
        ChatLayoutRoot.Margin = new Thickness(4, 46, 4, 4);
        ChatPanel.Padding = new Thickness(4);
        MinWidth = CompactMinimumWidth;
        MinHeight = CompactMinimumHeight;
        Width = CompactDefaultWidth;
        Height = CompactDefaultHeight;
        KeepCompactWindowOnScreen();
        UpdateCompactModeButton();
    }

    private void ExitCompactMode()
    {
        IsCompactChatMode = false;
        HeaderPanelToggleHost.Visibility = Visibility.Visible;
        ComposerPanelToggleHost.Visibility = Visibility.Visible;
        ChatLayoutRoot.Margin = new Thickness(16, 54, 16, 16);
        ChatPanel.Padding = new Thickness(8);
        MinWidth = NormalMinimumWidth;
        MinHeight = NormalMinimumHeight;

        _suppressPanelToggleAnimations = true;
        try
        {
            HeaderPanelToggle.IsChecked = _normalHeaderExpanded;
            ComposerPanelToggle.IsChecked = _normalComposerExpanded;
            ApplyPanelState(HeaderPanel, HeaderPanelChevronRotation, _normalHeaderExpanded, expandedAngle: 0, collapsedAngle: 180);
            ApplyPanelState(ComposerPanel, ComposerPanelChevronRotation, _normalComposerExpanded, expandedAngle: 0, collapsedAngle: 180);
        }
        finally
        {
            _suppressPanelToggleAnimations = false;
        }

        if (!_normalWindowBounds.IsEmpty &&
            _normalWindowBounds.Width >= NormalMinimumWidth &&
            _normalWindowBounds.Height >= NormalMinimumHeight)
        {
            WindowState = WindowState.Normal;
            Left = _normalWindowBounds.Left;
            Top = _normalWindowBounds.Top;
            Width = _normalWindowBounds.Width;
            Height = _normalWindowBounds.Height;
        }

        if (_normalWindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        UpdateCompactModeButton();
    }

    private void SetPanelsForCompactMode()
    {
        _headerPanelAnimationVersion++;
        _composerPanelAnimationVersion++;
        _suppressPanelToggleAnimations = true;
        try
        {
            HeaderPanelToggle.IsChecked = false;
            ComposerPanelToggle.IsChecked = false;
            ApplyPanelState(HeaderPanel, HeaderPanelChevronRotation, false, expandedAngle: 0, collapsedAngle: 180);
            ApplyPanelState(ComposerPanel, ComposerPanelChevronRotation, false, expandedAngle: 0, collapsedAngle: 180);
        }
        finally
        {
            _suppressPanelToggleAnimations = false;
        }

        HeaderPanelToggleHost.Visibility = Visibility.Collapsed;
        ComposerPanelToggleHost.Visibility = Visibility.Collapsed;
    }

    private static void ApplyPanelState(
        FrameworkElement panel,
        RotateTransform rotation,
        bool expanded,
        double expandedAngle,
        double collapsedAngle)
    {
        panel.BeginAnimation(HeightProperty, null);
        panel.BeginAnimation(OpacityProperty, null);
        rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        panel.Height = double.NaN;
        panel.Opacity = 1;
        panel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        rotation.Angle = expanded ? expandedAngle : collapsedAngle;
    }

    private void KeepCompactWindowOnScreen()
    {
        var workArea = SystemParameters.WorkArea;
        var sourceBounds = _normalWindowBounds.IsEmpty
            ? new Rect(Left, Top, Width, Height)
            : _normalWindowBounds;
        var centeredLeft = sourceBounds.Left + ((sourceBounds.Width - Width) / 2);
        var centeredTop = sourceBounds.Top + ((sourceBounds.Height - Height) / 2);
        Left = Math.Clamp(centeredLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(centeredTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
    }

    private void UpdateCompactModeButton()
    {
        var resourceKey = IsCompactChatMode ? "RestoreFullMode" : "CompactMode";
        CompactModeIcon.Data = Geometry.Parse(IsCompactChatMode ? RestoreIconGeometry : CompactIconGeometry);
        CompactModeButton.SetResourceReference(ToolTipService.ToolTipProperty, resourceKey);
    }

    private async void HeaderPanelToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (HeaderPanel is null || HeaderPanelChevronRotation is null)
        {
            return;
        }
        if (_suppressPanelToggleAnimations)
        {
            ApplyPanelState(HeaderPanel, HeaderPanelChevronRotation, true, expandedAngle: 0, collapsedAngle: 180);
            return;
        }
        AnimateChevron(HeaderPanelChevronRotation, 0);
        await AnimateCollapsiblePanelAsync(HeaderPanel, true, ++_headerPanelAnimationVersion, () => _headerPanelAnimationVersion);
    }

    private async void HeaderPanelToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (HeaderPanel is null || HeaderPanelChevronRotation is null)
        {
            return;
        }
        if (_suppressPanelToggleAnimations)
        {
            ApplyPanelState(HeaderPanel, HeaderPanelChevronRotation, false, expandedAngle: 0, collapsedAngle: 180);
            return;
        }
        AnimateChevron(HeaderPanelChevronRotation, 180);
        await AnimateCollapsiblePanelAsync(HeaderPanel, false, ++_headerPanelAnimationVersion, () => _headerPanelAnimationVersion);
    }

    private async void ComposerPanelToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (ComposerPanel is null || ComposerPanelChevronRotation is null)
        {
            return;
        }
        if (_suppressPanelToggleAnimations)
        {
            ApplyPanelState(ComposerPanel, ComposerPanelChevronRotation, true, expandedAngle: 0, collapsedAngle: 180);
            return;
        }
        AnimateChevron(ComposerPanelChevronRotation, 0);
        await AnimateCollapsiblePanelAsync(ComposerPanel, true, ++_composerPanelAnimationVersion, () => _composerPanelAnimationVersion);
    }

    private async void ComposerPanelToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (ComposerPanel is null || ComposerPanelChevronRotation is null)
        {
            return;
        }
        if (_suppressPanelToggleAnimations)
        {
            ApplyPanelState(ComposerPanel, ComposerPanelChevronRotation, false, expandedAngle: 0, collapsedAngle: 180);
            return;
        }
        AnimateChevron(ComposerPanelChevronRotation, 180);
        await AnimateCollapsiblePanelAsync(ComposerPanel, false, ++_composerPanelAnimationVersion, () => _composerPanelAnimationVersion);
    }

    private async Task AnimateCollapsiblePanelAsync(
        FrameworkElement panel,
        bool show,
        int animationVersion,
        Func<int> currentVersion)
    {
        panel.BeginAnimation(HeightProperty, null);
        panel.BeginAnimation(OpacityProperty, null);

        if (!IsLoaded || AnimationService.ReduceMotion)
        {
            panel.Height = double.NaN;
            panel.Opacity = 1;
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        double targetHeight;
        if (show)
        {
            panel.Visibility = Visibility.Visible;
            panel.Height = double.NaN;
            panel.Opacity = 0;
            panel.UpdateLayout();
            targetHeight = Math.Max(1, Math.Max(panel.ActualHeight, panel.DesiredSize.Height));
            panel.Height = 0;
            panel.UpdateLayout();
        }
        else
        {
            targetHeight = Math.Max(1, panel.ActualHeight);
            panel.Height = targetHeight;
            panel.Opacity = 1;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var heightAnimation = new DoubleAnimation(
            show ? 0 : targetHeight,
            show ? targetHeight : 0,
            TimeSpan.FromMilliseconds(210))
        {
            EasingFunction = easing
        };
        var opacityAnimation = new DoubleAnimation(
            show ? 0 : 1,
            show ? 1 : 0,
            TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = easing
        };
        var completion = new TaskCompletionSource<object?>();
        heightAnimation.Completed += (_, _) => completion.TrySetResult(null);
        panel.BeginAnimation(HeightProperty, heightAnimation, HandoffBehavior.SnapshotAndReplace);
        panel.BeginAnimation(OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        await completion.Task.ConfigureAwait(true);

        if (animationVersion != currentVersion())
        {
            return;
        }

        panel.BeginAnimation(HeightProperty, null);
        panel.BeginAnimation(OpacityProperty, null);
        panel.Height = double.NaN;
        panel.Opacity = 1;
        panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void AnimateChevron(RotateTransform rotation, double targetAngle)
    {
        rotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(targetAngle, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            },
            HandoffBehavior.SnapshotAndReplace);
        rotation.Angle = targetAngle;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        if (!_exitRequested && _trayIcon is not null)
        {
            e.Cancel = true;
            if (_windowTransitionInProgress)
            {
                return;
            }

            _windowTransitionInProgress = true;
            IsEnabled = false;
            try
            {
                await AnimationService.AnimateWindowCloseAsync(this, offsetY: 8).ConfigureAwait(true);
                Hide();
            }
            finally
            {
                AnimationService.ResetWindowVisuals(this);
                IsEnabled = true;
                _windowTransitionInProgress = false;
            }
            if (!_trayNoticeShown && _viewModel.Settings.ToastNotifications)
            {
                _trayNoticeShown = true;
                _trayIcon.ShowStillRunningNotice();
            }

            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        IsEnabled = false;
        try
        {
            await AnimationService.AnimateWindowCloseAsync(this, offsetY: 8).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            new FileLogger().Warn($"Window close animation failed: {ex.GetType().Name}");
        }
        finally
        {
            Hide();
            AnimationService.ResetWindowVisuals(this);
        }
        try
        {
            await CancelPendingInteractivePanelAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            new FileLogger().Warn($"Interactive panel cancellation failed during shutdown: {ex.GetType().Name}");
        }
        _viewModel.BeginShutdown();
        AttachMessageCollection(null);
        AttachActiveChannel(null);
        _viewModel.ActiveChannelChanging -= ViewModel_ActiveChannelChanging;
        _viewModel.ActiveMessagesChanged -= ViewModel_ActiveMessagesChanged;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ReleaseAllMessageContainers();
        SilentDialog.ClearHost(ShowSilentDialog);
        StopSmoothScrollAnimation();
        if (MessagesList.IsMouseCaptureWithin)
        {
            Mouse.Capture(null);
        }
        if (_messagesScrollViewer is not null)
        {
            _messagesScrollViewer.ScrollChanged -= MessagesScrollViewer_ScrollChanged;
        }
        _userScrollIdleTimer.Stop();
        AnimatedEmoteImage.SetFastScrolling(false);
#if DEBUG
        _scrollDiagnosticsTimer.Stop();
        CompositionTarget.Rendering -= DebugCompositionTarget_Rendering;
        MessagesList.LayoutUpdated -= MessagesList_LayoutUpdated;
#endif
        try
        {
            var initialization = _initializationTask;
            if (initialization is not null)
            {
                try
                {
                    await initialization;
                }
                catch (Exception ex)
                {
                    new FileLogger().Warn($"Initialization stopped during shutdown: {ex.GetType().Name}");
                }
            }

            await _viewModel.DisposeAsync();
        }
        catch (Exception ex)
        {
            new FileLogger().Error("WitherChat shutdown failed", ex);
        }
        finally
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            _windowSource?.RemoveHook(WindowMessageHook);
            _windowSource = null;
            _shutdownComplete = true;
            Close();
        }
    }

    private async Task CancelPendingInteractivePanelAsync()
    {
        switch (OverlayContent.Content)
        {
            case SettingsPanel settingsPanel:
                settingsPanel.CancelFromHost();
                break;
            case ConnectTwitchPanel connectPanel:
                connectPanel.CancelFromHost();
                break;
            case ChatLogsPanel chatLogsPanel:
                await chatLogsPanel.CancelFromHostAsync().ConfigureAwait(true);
                break;
        }
    }

    public void RequestApplicationExit()
    {
        if (_shutdownInProgress || _shutdownComplete)
        {
            return;
        }

        _exitRequested = true;
        Close();
    }

    public void RequestApplicationRestart()
    {
        if (_shutdownInProgress || _shutdownComplete)
        {
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            new FileLogger().Error("WitherChat restart failed: executable path is unavailable.");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--restart-from");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Process.Start(startInfo);
            RequestApplicationExit();
        }
        catch (Exception ex)
        {
            new FileLogger().Error("WitherChat restart failed", ex);
            SilentDialog.ShowMessage(
                AppInfo.Name,
                LocalizationService.Get(_viewModel.Settings.Language, "RestartFailed"));
        }
    }

    private TrayIconService? TryCreateTrayIcon()
    {
        try
        {
            return new TrayIconService(
                RestoreFromTray,
                RequestApplicationRestart,
                RequestApplicationExit,
                key => LocalizationService.Get(_viewModel.Settings.Language, key));
        }
        catch (Exception ex)
        {
            new FileLogger().Error("Failed to initialize the WitherChat tray icon", ex);
            return null;
        }
    }

    private void RestoreFromTray()
    {
        if (_shutdownInProgress || _shutdownComplete)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        AnimationService.ResetWindowVisuals(this);
        AnimationService.AnimateWindowIn(this, offsetY: 8);
    }

    public async Task<SettingsPanelResult?> OpenSettingsPanelAsync(
        AppSettings settings,
        Func<AppSettings, Task<string>>? testOverlay,
        bool isSignedIn,
        bool isReadOnlyMode,
        string accountDisplayName,
        string accountLogin,
        string accountProfileImageUrl,
        string readOnlyChannel)
    {
        var completion = new TaskCompletionSource<SettingsPanelResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new SettingsPanel(
            settings,
            testOverlay,
            isSignedIn,
            isReadOnlyMode,
            accountDisplayName,
            accountLogin,
            accountProfileImageUrl,
            readOnlyChannel);

        panel.Completed += async (_, result) =>
        {
            await CloseOverlaySafelyAsync().ConfigureAwait(true);
            completion.TrySetResult(result);
        };

        await OpenOverlayAsync(ActiveOverlayPanel.Settings, panel).ConfigureAwait(true);
        return await completion.Task.ConfigureAwait(true);
    }

    public async Task<ConnectTwitchPanelResult?> OpenConnectTwitchPanelAsync(
        string language,
        string lastReadOnlyChannel,
        IChannelSearchService channelSearchService)
    {
        var completion = new TaskCompletionSource<ConnectTwitchPanelResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new ConnectTwitchPanel(language, lastReadOnlyChannel, channelSearchService);
        panel.Completed += async (_, result) =>
        {
            await CloseOverlaySafelyAsync().ConfigureAwait(true);
            completion.TrySetResult(result);
        };

        await OpenOverlayAsync(ActiveOverlayPanel.ConnectTwitch, panel).ConfigureAwait(true);
        return await completion.Task.ConfigureAwait(true);
    }

    public async Task OpenChatLogsPanelAsync(AppSettings settings, ChatLogService chatLogService)
    {
        var panel = new ChatLogsPanel(settings, chatLogService);
        panel.CloseRequested += async (_, _) => await CloseOverlaySafelyAsync().ConfigureAwait(true);
        await OpenOverlayAsync(ActiveOverlayPanel.ChatLogs, panel).ConfigureAwait(true);
    }

    public async Task OpenModerationPanelAsync(ChatViewModel viewModel)
    {
        var panel = new ModerationPanel(viewModel);
        panel.CloseRequested += async (_, _) => await CloseOverlaySafelyAsync().ConfigureAwait(true);
        await OpenOverlayAsync(ActiveOverlayPanel.CreatorControls, panel).ConfigureAwait(true);
    }

    private async Task OpenOverlayAsync(ActiveOverlayPanel panelType, FrameworkElement panel)
    {
        await _overlayTransitionGate.WaitAsync().ConfigureAwait(true);
        try
        {
            _viewModel.IsChannelSwitcherOpen = false;

            if (_activeOverlay != ActiveOverlayPanel.None)
            {
                await CloseOverlayCoreAsync().ConfigureAwait(true);
            }

            _activeOverlay = panelType;
            OverlayContent.Content = panel;
            UpdateOverlayPanelSize();
            OverlayHost.Visibility = Visibility.Visible;
            var entranceOffset = panelType == ActiveOverlayPanel.Settings ? 96 : 40;
            await AnimationService.AnimatePanelInAsync(
                OverlayScrim,
                OverlayPanelChrome,
                offsetX: entranceOffset).ConfigureAwait(true);
        }
        finally
        {
            _overlayTransitionGate.Release();
        }
    }

    public async Task CloseOverlayAsync()
    {
        await _overlayTransitionGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await CloseOverlayCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _overlayTransitionGate.Release();
        }
    }

    private async Task CloseOverlaySafelyAsync()
    {
        try
        {
            await CloseOverlayAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            new FileLogger().Warn($"Overlay close failed: {ex.GetType().Name}");
            OverlayContent.Content = null;
            OverlayHost.Visibility = Visibility.Collapsed;
            _activeOverlay = ActiveOverlayPanel.None;
        }
    }

    private async Task CloseOverlayCoreAsync()
    {
        if (_activeOverlay == ActiveOverlayPanel.None)
        {
            return;
        }

        try
        {
            var exitOffset = _activeOverlay == ActiveOverlayPanel.Settings ? 96 : 40;
            await AnimationService.AnimatePanelOutAsync(
                OverlayScrim,
                OverlayPanelChrome,
                offsetX: exitOffset).ConfigureAwait(true);
        }
        finally
        {
            OverlayContent.Content = null;
            OverlayHost.Visibility = Visibility.Collapsed;
            _activeOverlay = ActiveOverlayPanel.None;
        }
    }

    private void UpdateOverlayPanelSize()
    {
        if (!IsLoaded || OverlayPanelChrome is null)
        {
            return;
        }

        var availableWidth = Math.Max(320, ActualWidth - 36);
        OverlayPanelChrome.Width = Math.Min(940, availableWidth);
        DialogCard.Width = Math.Min(460, Math.Max(280, ActualWidth - 48));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_viewModel.IsChannelSwitcherOpen)
        {
            e.Handled = true;
            _viewModel.IsChannelSwitcherOpen = false;
            return;
        }

        if (_dialogOpen)
        {
            e.Handled = true;
            _ = CloseDialogOverlayAsync(false);
            return;
        }

        if (_activeOverlay == ActiveOverlayPanel.None)
        {
            return;
        }

        e.Handled = true;
        switch (OverlayContent.Content)
        {
            case SettingsPanel settingsPanel:
                settingsPanel.CancelFromHost();
                break;
            case ConnectTwitchPanel connectPanel:
                connectPanel.CancelFromHost();
                break;
            default:
                _ = CloseOverlaySafelyAsync();
                break;
        }
    }

    private void OverlayScrim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeOverlay == ActiveOverlayPanel.None)
        {
            return;
        }

        if (OverlayContent.Content is SettingsPanel settingsPanel)
        {
            if (settingsPanel.HasUnsavedChanges)
            {
                settingsPanel.ShowUnsavedHint();
                return;
            }

            settingsPanel.CancelFromHost();
            return;
        }

        if (OverlayContent.Content is ConnectTwitchPanel connectPanel)
        {
            connectPanel.CancelFromHost();
            return;
        }

        _ = CloseOverlaySafelyAsync();
    }

    private void ChannelSwitcherDismiss_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.IsChannelSwitcherOpen = false;
        e.Handled = true;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ChatViewModel.IsChannelSwitcherOpen), StringComparison.Ordinal))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AnimateChannelSwitcher(_viewModel.IsChannelSwitcherOpen));
            return;
        }

        AnimateChannelSwitcher(_viewModel.IsChannelSwitcherOpen);
    }

    private void AnimateChannelSwitcher(bool open)
    {
        var version = ++_channelSwitcherTransitionVersion;
        if (AnimationService.ReduceMotion)
        {
            CompleteChannelSwitcherTransition(open, version);
            return;
        }

        if (open)
        {
            if (ChannelSwitcherFlyout.Visibility != Visibility.Visible)
            {
                ClearChannelSwitcherAnimations();
                ChannelSwitcherFlyout.Opacity = 0;
                ChannelSwitcherScale.ScaleX = 0.97;
                ChannelSwitcherScale.ScaleY = 0.97;
                ChannelSwitcherTranslate.Y = -8;
            }

            ChannelSwitcherFlyout.Visibility = Visibility.Visible;
            ChannelSwitcherFlyout.IsHitTestVisible = true;
            ChannelSwitcherDismissLayer.Visibility = Visibility.Visible;
            StartChannelSwitcherAnimations(
                opacity: 1,
                scale: 1,
                translateY: 0,
                opacityDuration: TimeSpan.FromMilliseconds(175),
                transformDuration: TimeSpan.FromMilliseconds(205),
                new CubicEase { EasingMode = EasingMode.EaseOut },
                version,
                open: true);
            return;
        }

        ChannelSwitcherDismissLayer.Visibility = Visibility.Collapsed;
        ChannelSwitcherFlyout.IsHitTestVisible = false;
        if (ChannelSwitcherFlyout.Visibility != Visibility.Visible)
        {
            CompleteChannelSwitcherTransition(false, version);
            return;
        }

        var closeDuration = TimeSpan.FromMilliseconds(135);
        StartChannelSwitcherAnimations(
            opacity: 0,
            scale: 0.985,
            translateY: -5,
            opacityDuration: closeDuration,
            transformDuration: closeDuration,
            new CubicEase { EasingMode = EasingMode.EaseIn },
            version,
            open: false);
    }

    private void StartChannelSwitcherAnimations(
        double opacity,
        double scale,
        double translateY,
        TimeSpan opacityDuration,
        TimeSpan transformDuration,
        IEasingFunction easing,
        long version,
        bool open)
    {
        var currentOpacity = ChannelSwitcherFlyout.Opacity;
        var currentScaleX = ChannelSwitcherScale.ScaleX;
        var currentScaleY = ChannelSwitcherScale.ScaleY;
        var currentTranslateY = ChannelSwitcherTranslate.Y;
        ClearChannelSwitcherAnimations();

        ChannelSwitcherFlyout.Opacity = opacity;
        ChannelSwitcherScale.ScaleX = scale;
        ChannelSwitcherScale.ScaleY = scale;
        ChannelSwitcherTranslate.Y = translateY;

        var opacityAnimation = CreateChannelSwitcherAnimation(currentOpacity, opacity, opacityDuration, easing);
        var scaleXAnimation = CreateChannelSwitcherAnimation(currentScaleX, scale, transformDuration, easing);
        var scaleYAnimation = CreateChannelSwitcherAnimation(currentScaleY, scale, transformDuration, easing);
        var translateAnimation = CreateChannelSwitcherAnimation(currentTranslateY, translateY, transformDuration, easing);
        translateAnimation.Completed += (_, _) => CompleteChannelSwitcherTransition(open, version);

        ChannelSwitcherFlyout.BeginAnimation(OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        ChannelSwitcherScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnimation, HandoffBehavior.SnapshotAndReplace);
        ChannelSwitcherScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnimation, HandoffBehavior.SnapshotAndReplace);
        ChannelSwitcherTranslate.BeginAnimation(TranslateTransform.YProperty, translateAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimation CreateChannelSwitcherAnimation(
        double from,
        double to,
        TimeSpan duration,
        IEasingFunction easing) =>
        new(from, to, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };

    private void CompleteChannelSwitcherTransition(bool open, long version)
    {
        if (version != _channelSwitcherTransitionVersion || _viewModel.IsChannelSwitcherOpen != open)
        {
            return;
        }

        ClearChannelSwitcherAnimations();
        if (open)
        {
            ChannelSwitcherFlyout.Visibility = Visibility.Visible;
            ChannelSwitcherFlyout.IsHitTestVisible = true;
            ChannelSwitcherDismissLayer.Visibility = Visibility.Visible;
            ChannelSwitcherFlyout.Opacity = 1;
            ChannelSwitcherScale.ScaleX = 1;
            ChannelSwitcherScale.ScaleY = 1;
            ChannelSwitcherTranslate.Y = 0;
            ChannelSwitcherFlyout.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return;
        }

        ChannelSwitcherFlyout.Opacity = 0;
        ChannelSwitcherScale.ScaleX = 0.97;
        ChannelSwitcherScale.ScaleY = 0.97;
        ChannelSwitcherTranslate.Y = -8;
        ChannelSwitcherFlyout.Visibility = Visibility.Collapsed;
        ChannelSwitcherFlyout.IsHitTestVisible = false;
        ChannelSwitcherDismissLayer.Visibility = Visibility.Collapsed;
        if (_activeOverlay == ActiveOverlayPanel.None && !_dialogOpen && IsActive)
        {
            ChannelSwitcherButton.Focus();
        }
    }

    private void ClearChannelSwitcherAnimations()
    {
        ChannelSwitcherFlyout.BeginAnimation(OpacityProperty, null);
        ChannelSwitcherScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ChannelSwitcherScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ChannelSwitcherTranslate.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private bool ShowSilentDialog(string title, string message, bool confirm)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(() => ShowSilentDialog(title, message, confirm));
        }

        // A nested DispatcherFrame continues pumping UI work. Ignore a reentrant
        // dialog request so it cannot replace the frame that the visible dialog owns.
        if (_dialogOpen || _dialogFrame is not null)
        {
            return false;
        }

        DialogTitleText.Text = title;
        DialogMessageText.Text = message;
        DialogCancelButton.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        DialogOkButton.Content = confirm
            ? Application.Current.Resources["DialogYes"] as string ?? LocalizationService.Get(LocalizationService.CurrentLanguage, "DialogYes")
            : Application.Current.Resources["DialogOk"] as string ?? LocalizationService.Get(LocalizationService.CurrentLanguage, "DialogOk");

        _dialogOpen = true;
        _dialogClosing = false;
        _dialogResult = false;
        DialogOkButton.IsEnabled = true;
        DialogCancelButton.IsEnabled = true;
        DialogOverlayHost.Visibility = Visibility.Visible;
        _ = AnimationService.AnimatePanelInAsync(DialogScrim, DialogCard, 0.45, offsetX: 0);

        var frame = new DispatcherFrame();
        _dialogFrame = frame;
        try
        {
            Dispatcher.PushFrame(frame);
            return _dialogResult;
        }
        finally
        {
            if (ReferenceEquals(_dialogFrame, frame))
            {
                _dialogFrame = null;
            }
        }
    }

    private async void DialogOk_Click(object sender, RoutedEventArgs e)
    {
        await CloseDialogOverlayAsync(true).ConfigureAwait(true);
    }

    private async void DialogCancel_Click(object sender, RoutedEventArgs e)
    {
        await CloseDialogOverlayAsync(false).ConfigureAwait(true);
    }

    private async Task CloseDialogOverlayAsync(bool result)
    {
        if (!_dialogOpen || _dialogClosing)
        {
            return;
        }

        _dialogClosing = true;
        DialogOkButton.IsEnabled = false;
        DialogCancelButton.IsEnabled = false;
        _dialogResult = result;
        try
        {
            await AnimationService.AnimatePanelOutAsync(DialogScrim, DialogCard, offsetX: 0).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Closing the dialog must not be blocked by a failed visual transition.
        }
        finally
        {
            DialogOverlayHost.Visibility = Visibility.Collapsed;
            _dialogOpen = false;
            _dialogClosing = false;
            if (_dialogFrame is not null)
            {
                _dialogFrame.Continue = false;
                _dialogFrame = null;
            }
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
#if DEBUG
        _debugCollectionChanges++;
#endif
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            if (_subscribedMessages is LiveMessageCollection<ChatMessageModel> liveMessages &&
                (liveMessages.IsTrimming || liveMessages.IsBatchUpdating))
            {
                PreserveViewportAfterTrim();
            }
            else
            {
                ResetChatFollowState();
            }

            return;
        }

        if (e.NewItems is null)
        {
            return;
        }

        var shouldFollow = _viewModel.AutoScroll;
        if (!shouldFollow)
        {
            if (_viewModel.ActiveChannel is not null)
            {
                _viewModel.ActiveChannel.NewMessagesBelowCount += e.NewItems.Count;
            }

            UpdateUnreadBadge();
            SetJumpButtonVisible(_userScrolledAwayFromBottom);
        }
        else
        {
            SetJumpButtonVisible(false);
            QueueScrollToEnd();
        }

        QueueMessageAnimations(e.NewItems);
    }

    private void QueueMessageAnimations(System.Collections.IList items)
    {
        if (AnimationService.ReduceMotion || _isUserScrolling || _suppressPendingMessageAnimations)
        {
            return;
        }

        foreach (var item in items)
        {
            _pendingAnimationItems.Enqueue(item!);
            while (_pendingAnimationItems.Count > 64)
            {
                _pendingAnimationItems.Dequeue();
            }

            if (_pendingAnimationItems.Count > 12)
            {
                _pendingAnimationItems.Clear();
                _suppressPendingMessageAnimations = true;
                break;
            }
        }

        if (_messageAnimationQueued)
        {
            return;
        }

        _messageAnimationQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _messageAnimationQueued = false;
            if (_suppressPendingMessageAnimations)
            {
                _suppressPendingMessageAnimations = false;
                _pendingAnimationItems.Clear();
                return;
            }

            while (_pendingAnimationItems.TryDequeue(out var item))
            {
                if (MessagesList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem listBoxItem)
                {
                    AnimationService.AnimateListBoxItem(listBoxItem);
                }
            }
        }), DispatcherPriority.Loaded);
    }

    private void PreserveViewportAfterTrim()
    {
        _pendingAnimationItems.Clear();
        if (_messagesScrollViewer is null)
        {
            return;
        }

        if (_viewModel.AutoScroll)
        {
            SetJumpButtonVisible(false);
            QueueScrollToEnd();
            return;
        }

        var previousExtent = _messagesScrollViewer.ExtentHeight;
        var previousOffset = _messagesScrollViewer.VerticalOffset;
        var messages = _subscribedMessages;
        var scrollVersion = _scrollStateVersion;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_messagesScrollViewer is null ||
                !ReferenceEquals(_subscribedMessages, messages) ||
                scrollVersion != _scrollStateVersion)
            {
                return;
            }

            var removedHeight = Math.Max(0, previousExtent - _messagesScrollViewer.ExtentHeight);
            _isProgrammaticScroll = true;
            _messagesScrollViewer.ScrollToVerticalOffset(Math.Max(0, previousOffset - removedHeight));
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_subscribedMessages, messages) ||
                    scrollVersion != _scrollStateVersion)
                {
                    return;
                }

                _isProgrammaticScroll = false;
                _userScrolledAwayFromBottom = true;
                SetJumpButtonVisible(true);
            }), DispatcherPriority.ContextIdle);
        }), DispatcherPriority.Loaded);
    }

    private void AttachMessageCollection(ObservableCollection<ChatMessageModel>? messages)
    {
        if (_subscribedMessages is not null)
        {
            _subscribedMessages.CollectionChanged -= Messages_CollectionChanged;
        }

        _subscribedMessages = messages;
        if (_subscribedMessages is not null)
        {
            _subscribedMessages.CollectionChanged += Messages_CollectionChanged;
        }
    }

    private void AttachActiveChannel(ChannelSessionViewModel? channel)
    {
        if (_subscribedChannel is not null)
        {
            _subscribedChannel.PropertyChanged -= ActiveChannel_PropertyChanged;
        }

        _subscribedChannel = channel;
        if (_subscribedChannel is not null)
        {
            _subscribedChannel.PropertyChanged += ActiveChannel_PropertyChanged;
        }
    }

    private void ActiveChannel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelSessionViewModel.NewMessagesBelowCount))
        {
            UpdateUnreadBadge();
        }
    }

    private void ViewModel_ActiveChannelChanging(object? sender, EventArgs e)
    {
        if (_viewModel.ActiveChannel is not null && _messagesScrollViewer is not null)
        {
            _viewModel.ActiveChannel.SavedVerticalOffset = _messagesScrollViewer.VerticalOffset;
            _viewModel.ActiveChannel.AutoScroll = _viewModel.AutoScroll;
        }

        ReleaseAllMessageContainers();

        _scrollStateVersion++;
        StopSmoothScrollAnimation();
        _pendingScrollToBottom = false;
        _followWhenUserScrollEnds = false;
        _scrollToBottomPasses = 0;
        _userScrollIdleTimer.Stop();
        if (_isUserScrolling)
        {
            _isUserScrolling = false;
            _viewModel.SetUserScrolling(false);
            AnimatedEmoteImage.SetFastScrolling(false);
        }
    }

    private void ViewModel_ActiveMessagesChanged(object? sender, EventArgs e)
    {
        AttachMessageCollection(_viewModel.Messages);
        AttachActiveChannel(_viewModel.ActiveChannel);
        _userScrolledAwayFromBottom = !_viewModel.AutoScroll;
        UpdateUnreadBadge();
        SetJumpButtonVisible(false);
        var activeChannel = _viewModel.ActiveChannel;
        var scrollVersion = _scrollStateVersion;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_messagesScrollViewer is null ||
                activeChannel is null ||
                !ReferenceEquals(_viewModel.ActiveChannel, activeChannel) ||
                scrollVersion != _scrollStateVersion)
            {
                return;
            }

            if (activeChannel.AutoScroll)
            {
                QueueScrollToEnd();
            }
            else
            {
                _isProgrammaticScroll = true;
                _messagesScrollViewer.ScrollToVerticalOffset(activeChannel.SavedVerticalOffset);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ReferenceEquals(_viewModel.ActiveChannel, activeChannel) ||
                        scrollVersion != _scrollStateVersion)
                    {
                        return;
                    }

                    _isProgrammaticScroll = false;
                    _userScrolledAwayFromBottom = true;
                    SetJumpButtonVisible(true);
                }), DispatcherPriority.ContextIdle);
            }
        }), DispatcherPriority.Loaded);
    }

    private void MessagesList_Loaded(object sender, RoutedEventArgs e)
    {
        _messagesScrollViewer = FindVisualChild<ScrollViewer>(MessagesList);
        _messagesVirtualizingPanel = FindVisualChild<VirtualizingStackPanel>(MessagesList);
        if (_messagesScrollViewer is not null)
        {
            _messagesScrollViewer.ScrollChanged -= MessagesScrollViewer_ScrollChanged;
            _messagesScrollViewer.ScrollChanged += MessagesScrollViewer_ScrollChanged;
            QueueScrollToEnd();
        }
    }

    private void MessageListItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item)
        {
            return;
        }

        item.DataContextChanged -= MessageListItem_DataContextChanged;
        item.DataContextChanged += MessageListItem_DataContextChanged;
        TrackMessageContainer(item, item.DataContext as ChatMessageModel);
    }

    private void MessageListItem_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item)
        {
            return;
        }

        item.DataContextChanged -= MessageListItem_DataContextChanged;
        if (_messageContainerModels.Remove(item, out var message))
        {
            ReleaseMessageImagesIfNoContainerUses(message);
        }
    }

    private void MessageListItem_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            TrackMessageContainer(item, e.NewValue as ChatMessageModel);
        }
    }

    private void TrackMessageContainer(ListBoxItem item, ChatMessageModel? message)
    {
        if (_messageContainerModels.Remove(item, out var previous) && !ReferenceEquals(previous, message))
        {
            ReleaseMessageImagesIfNoContainerUses(previous);
        }

        if (message is not null)
        {
            _messageContainerModels[item] = message;
            _viewModel.SetMessageImagesVisible(message, true);
        }
    }

    private void ReleaseMessageImagesIfNoContainerUses(ChatMessageModel message)
    {
        if (!_messageContainerModels.Values.Any(candidate => ReferenceEquals(candidate, message)))
        {
            _viewModel.SetMessageImagesVisible(message, false);
        }
    }

    private void ReleaseAllMessageContainers()
    {
        var messages = new HashSet<ChatMessageModel>(
            _messageContainerModels.Values,
            ReferenceEqualityComparer.Instance).ToArray();
        _messageContainerModels.Clear();
        foreach (var message in messages)
        {
            _viewModel.SetMessageImagesVisible(message, false);
        }
    }

    private void MessagesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
#if DEBUG
        _debugScrollChanges++;
#endif
        if (_messagesScrollViewer is null)
        {
            return;
        }

        if (_viewModel.ActiveChannel is not null)
        {
            _viewModel.ActiveChannel.SavedVerticalOffset = _messagesScrollViewer.VerticalOffset;
        }

        if (_isProgrammaticScroll || _pendingScrollToBottom)
        {
            if (_viewModel.AutoScroll)
            {
                SetJumpButtonVisible(false);
            }

            return;
        }

        var hasUserScrollInput = Mouse.Captured is Thumb or RepeatButton ||
                                 ReferenceEquals(Mouse.Captured, MessagesList) ||
                                 MessagesList.IsStylusCaptureWithin ||
                                 MessagesList.AreAnyTouchesCapturedWithin;
        if (hasUserScrollInput)
        {
            MarkUserScrollInput();
            if (e.VerticalChange < -0.01 || !IsNearBottom(_messagesScrollViewer))
            {
                _followWhenUserScrollEnds = false;
                SetFollowMode(false);
            }
            else if (e.VerticalChange > 0.01)
            {
                _followWhenUserScrollEnds = true;
            }

            return;
        }

        if (_viewModel.AutoScroll)
        {
            SetJumpButtonVisible(false);
            if (!_isUserScrolling && Math.Abs(e.ExtentHeightChange) > 0.01)
            {
                QueueScrollToEnd();
            }

            return;
        }
    }

    private void MessagesList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            MarkUserScrollInput();
            StopSmoothScrollAnimation();
        }
        else if (e.ChangedButton == MouseButton.Left && IsWithinScrollBar(e.OriginalSource as DependencyObject))
        {
            MarkUserScrollInput();
            StopSmoothScrollAnimation();
        }
    }

    private void MessagesList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_messagesScrollViewer is null || _messagesScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        MarkUserScrollInput();
        var baseDistanceFromBottom = _isSmoothScrolling
            ? _scrollTargetDistanceFromBottom
            : _messagesScrollViewer.ScrollableHeight - _messagesScrollViewer.VerticalOffset;
        var targetDistanceFromBottom = Math.Clamp(
            baseDistanceFromBottom + ((e.Delta / 120.0) * WheelScrollPixelsPerNotch),
            0,
            _messagesScrollViewer.ScrollableHeight);
        var targetOffset = _messagesScrollViewer.ScrollableHeight - targetDistanceFromBottom;
        var reachesBottom = e.Delta < 0 &&
                            targetDistanceFromBottom <= 0.5;

        if (!reachesBottom)
        {
            SetFollowMode(false);
        }

        StartSmoothScroll(targetOffset, followOnComplete: reachesBottom);
        e.Handled = true;
    }

    private void MessagesList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            MarkUserScrollInput();
            StopSmoothScrollAnimation();
        }

        if (e.Key is Key.Up or Key.PageUp or Key.Home)
        {
            SetFollowMode(false);
            return;
        }

        if (e.Key is Key.Down or Key.PageDown or Key.End)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_messagesScrollViewer is not null && IsNearBottom(_messagesScrollViewer))
                {
                    SetFollowMode(true);
                }
            }), DispatcherPriority.ContextIdle);
        }
    }

    private void JumpToLatestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_messagesScrollViewer is not null)
        {
            SetFollowMode(true);
            StartSmoothScroll(_messagesScrollViewer.ScrollableHeight, followOnComplete: true);
        }
    }

    private void StartSmoothScroll(double target, bool followOnComplete)
    {
        if (_messagesScrollViewer is null)
        {
            return;
        }

        var isRetargetingActiveAnimation = _isSmoothScrolling;
        if (!isRetargetingActiveAnimation)
        {
            StopSmoothScrollAnimation(clearProgrammaticScroll: false);
        }
        _isProgrammaticScroll = true;
        _scrollStartOffset = _messagesScrollViewer.VerticalOffset;
        _scrollTargetOffset = Math.Clamp(target, 0, _messagesScrollViewer.ScrollableHeight);
        _scrollStartDistanceFromBottom = Math.Max(
            0,
            _messagesScrollViewer.ScrollableHeight - _scrollStartOffset);
        _scrollTargetDistanceFromBottom = Math.Max(
            0,
            _messagesScrollViewer.ScrollableHeight - _scrollTargetOffset);
        _scrollRequestedDistanceFromBottom =
            _scrollTargetDistanceFromBottom - _scrollStartDistanceFromBottom;
        _scrollSettleFrames = 0;
        _scrollStableSettleFrames = 0;
        _scrollTargetsTop = !followOnComplete && _scrollTargetOffset <= 0.5;
        _scrollAnimationStarted = 0;
        _scrollAnimationElapsedMs = 0;
        _followOnScrollComplete = followOnComplete;

        if (AnimationService.ReduceMotion || Math.Abs(_scrollTargetOffset - _scrollStartOffset) < 0.5)
        {
            if (isRetargetingActiveAnimation)
            {
                StopSmoothScrollAnimation(clearProgrammaticScroll: false);
            }
#if DEBUG
            _debugScrollCommands++;
#endif
            SetMessagesVerticalOffset(_scrollTargetOffset);
            _isProgrammaticScroll = false;
            if (followOnComplete)
            {
                SetFollowMode(true);
            }
            return;
        }

        if (!isRetargetingActiveAnimation)
        {
            _isSmoothScrolling = true;
            CompositionTarget.Rendering += SmoothScroll_Rendering;
        }
    }

    private void SmoothScroll_Rendering(object? sender, EventArgs e)
    {
        if (_messagesScrollViewer is null)
        {
            StopSmoothScrollAnimation();
            return;
        }

        if (_scrollAnimationStarted == 0)
        {
            _scrollAnimationStarted = Stopwatch.GetTimestamp();
            _scrollStartOffset = _messagesScrollViewer.VerticalOffset;
            if (_followOnScrollComplete)
            {
                _scrollTargetOffset = _messagesScrollViewer.ScrollableHeight;
            }
            else
            {
                _scrollStartDistanceFromBottom = Math.Max(
                    0,
                    _messagesScrollViewer.ScrollableHeight - _scrollStartOffset);
                _scrollTargetDistanceFromBottom = _scrollTargetsTop
                    ? _messagesScrollViewer.ScrollableHeight
                    : Math.Clamp(
                        _scrollTargetDistanceFromBottom,
                        0,
                        _messagesScrollViewer.ScrollableHeight);
                _scrollRequestedDistanceFromBottom =
                    _scrollTargetDistanceFromBottom - _scrollStartDistanceFromBottom;
                _scrollTargetOffset = Math.Clamp(
                    _messagesScrollViewer.ScrollableHeight - _scrollTargetDistanceFromBottom,
                    0,
                    _messagesScrollViewer.ScrollableHeight);
            }
            return;
        }

        var currentTimestamp = Stopwatch.GetTimestamp();
        var frameElapsed = Stopwatch.GetElapsedTime(_scrollAnimationStarted, currentTimestamp).TotalMilliseconds;
        _scrollAnimationStarted = currentTimestamp;
        _scrollAnimationElapsedMs += Math.Min(frameElapsed, SmoothScrollMaxFrameStepMs);
        var progress = Math.Clamp(_scrollAnimationElapsedMs / SmoothScrollDurationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var continueSettling = false;
        if (progress >= 1 && !_followOnScrollComplete)
        {
            var actualDistanceFromBottom =
                _messagesScrollViewer.ScrollableHeight - _messagesScrollViewer.VerticalOffset;
            var settledTargetDistanceFromBottom = _scrollTargetsTop
                ? _messagesScrollViewer.ScrollableHeight
                : Math.Clamp(
                    _scrollTargetDistanceFromBottom,
                    0,
                    _messagesScrollViewer.ScrollableHeight);
            _scrollStableSettleFrames = Math.Abs(
                actualDistanceFromBottom - settledTargetDistanceFromBottom) <= 0.5
                ? _scrollStableSettleFrames + 1
                : 0;
            continueSettling =
                ++_scrollSettleFrames < SmoothScrollSettleFrameLimit &&
                _scrollStableSettleFrames < 2;
        }
#if DEBUG
        _debugScrollCommands++;
#endif
        if (_followOnScrollComplete)
        {
            _scrollTargetOffset = _messagesScrollViewer.ScrollableHeight;
            SetMessagesVerticalOffset(
                _scrollStartOffset + ((_scrollTargetOffset - _scrollStartOffset) * eased));
        }
        else
        {
            var targetDistanceFromBottom = _scrollTargetsTop
                ? _messagesScrollViewer.ScrollableHeight
                : Math.Clamp(
                    _scrollTargetDistanceFromBottom,
                    0,
                    _messagesScrollViewer.ScrollableHeight);
            if (_scrollTargetsTop)
            {
                _scrollTargetDistanceFromBottom = targetDistanceFromBottom;
            }
            _scrollRequestedDistanceFromBottom =
                targetDistanceFromBottom - _scrollStartDistanceFromBottom;
            var distanceFromBottom = Math.Clamp(
                _scrollStartDistanceFromBottom + (_scrollRequestedDistanceFromBottom * eased),
                0,
                _messagesScrollViewer.ScrollableHeight);
            _scrollTargetOffset = Math.Clamp(
                _messagesScrollViewer.ScrollableHeight - targetDistanceFromBottom,
                0,
                _messagesScrollViewer.ScrollableHeight);
            SetMessagesVerticalOffset(
                _messagesScrollViewer.ScrollableHeight - distanceFromBottom);
        }

        if (progress < 1 || continueSettling)
        {
            return;
        }

        StopSmoothScrollAnimation();
        if (_followOnScrollComplete)
        {
            SetFollowMode(true);
            QueueScrollToEnd();
        }
    }

    private void StopSmoothScrollAnimation(bool clearProgrammaticScroll = true)
    {
        if (_isSmoothScrolling)
        {
            CompositionTarget.Rendering -= SmoothScroll_Rendering;
            _isSmoothScrolling = false;
        }

        _scrollAnimationStarted = 0;
        _scrollAnimationElapsedMs = 0;
        _scrollStartDistanceFromBottom = 0;
        _scrollTargetDistanceFromBottom = 0;
        _scrollRequestedDistanceFromBottom = 0;
        _scrollSettleFrames = 0;
        _scrollStableSettleFrames = 0;
        _scrollTargetsTop = false;

        if (clearProgrammaticScroll)
        {
            _isProgrammaticScroll = false;
        }
    }

    private void SetMessagesVerticalOffset(double offset)
    {
        if (_messagesScrollViewer is null)
        {
            return;
        }

        var clampedOffset = Math.Clamp(offset, 0, _messagesScrollViewer.ScrollableHeight);
        if (_messagesVirtualizingPanel is not null &&
            ReferenceEquals(_messagesVirtualizingPanel.ScrollOwner, _messagesScrollViewer))
        {
            // ScrollViewer queues offset commands. During a frame-by-frame animation those
            // commands can overtake one another while recycled variable-height rows are
            // measured, briefly applying an older offset. Setting IScrollInfo directly keeps
            // each rendered frame on the latest requested position.
            _messagesVirtualizingPanel.SetVerticalOffset(clampedOffset);
            return;
        }

        _messagesScrollViewer.ScrollToVerticalOffset(clampedOffset);
    }

    private static bool IsNearBottom(ScrollViewer viewer) =>
        viewer.ScrollableHeight <= 0 ||
        viewer.VerticalOffset >= viewer.ScrollableHeight - FollowBottomThreshold;

    private void QueueScrollToEnd()
    {
        if (_pendingScrollToBottom || _messagesScrollViewer is null || !_viewModel.AutoScroll || _isUserScrolling)
        {
            return;
        }

        _pendingScrollToBottom = true;
        var scrollVersion = _scrollStateVersion;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (scrollVersion != _scrollStateVersion)
            {
                return;
            }

            if (_messagesScrollViewer is null || !_viewModel.AutoScroll || _isUserScrolling)
            {
                _pendingScrollToBottom = false;
                return;
            }

            ScrollToEndProgrammatically(scrollVersion);
        }), DispatcherPriority.Loaded);
    }

    private void ScrollToEndProgrammatically(long scrollVersion)
    {
        if (_messagesScrollViewer is null || !_viewModel.AutoScroll || _isUserScrolling)
        {
            _pendingScrollToBottom = false;
            return;
        }

        StopSmoothScrollAnimation(clearProgrammaticScroll: false);
        _isProgrammaticScroll = true;
#if DEBUG
        _debugScrollCommands++;
#endif
        _messagesScrollViewer.ScrollToVerticalOffset(_messagesScrollViewer.ScrollableHeight);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (scrollVersion != _scrollStateVersion)
            {
                return;
            }

            _isProgrammaticScroll = false;
            _pendingScrollToBottom = false;
            if (_viewModel.AutoScroll)
            {
                if (_messagesScrollViewer is not null && IsNearBottom(_messagesScrollViewer))
                {
                    _scrollToBottomPasses = 0;
                    _userScrolledAwayFromBottom = false;
                    SetJumpButtonVisible(false);
                }
                else if (!_isUserScrolling && ++_scrollToBottomPasses < 8)
                {
                    QueueScrollToEnd();
                }
                else
                {
                    _scrollToBottomPasses = 0;
                }
            }
        }), DispatcherPriority.ContextIdle);
    }

    private void MarkUserScrollInput()
    {
        _lastUserScrollInputAt = Stopwatch.GetTimestamp();
        if (!_isUserScrolling)
        {
            _isUserScrolling = true;
            _viewModel.SetUserScrolling(true);
            AnimatedEmoteImage.SetFastScrolling(true);
        }

        _userScrollIdleTimer.Stop();
        _userScrollIdleTimer.Start();
    }

    private void UserScrollIdleTimer_Tick(object? sender, EventArgs e)
    {
        if (Stopwatch.GetElapsedTime(_lastUserScrollInputAt).TotalMilliseconds < 230)
        {
            return;
        }

        if (Mouse.Captured is Thumb or RepeatButton ||
            ReferenceEquals(Mouse.Captured, MessagesList) ||
            MessagesList.IsStylusCaptureWithin ||
            MessagesList.AreAnyTouchesCapturedWithin)
        {
            _lastUserScrollInputAt = Stopwatch.GetTimestamp();
            return;
        }

        _userScrollIdleTimer.Stop();
        _isUserScrolling = false;
        _viewModel.SetUserScrolling(false);
        AnimatedEmoteImage.SetFastScrolling(false);
        if (_followWhenUserScrollEnds && _messagesScrollViewer is not null && IsNearBottom(_messagesScrollViewer))
        {
            _followWhenUserScrollEnds = false;
            SetFollowMode(true);
        }

        if (_viewModel.AutoScroll)
        {
            QueueScrollToEnd();
        }
    }

    private static bool IsWithinScrollBar(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is ScrollBar or Thumb or RepeatButton)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject child) =>
        child is Visual ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);

#if DEBUG
    private void MessagesList_LayoutUpdated(object? sender, EventArgs e) => _debugLayoutPasses++;

    private void DebugCompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (!_isUserScrolling || e is not RenderingEventArgs rendering)
        {
            _debugLastFrameTime = TimeSpan.Zero;
            return;
        }

        if (_debugLastFrameTime != TimeSpan.Zero)
        {
            _debugFrameMilliseconds += (rendering.RenderingTime - _debugLastFrameTime).TotalMilliseconds;
            _debugFrameCount++;
        }
        _debugLastFrameTime = rendering.RenderingTime;
    }

    private void ScrollDiagnosticsTimer_Tick(object? sender, EventArgs e)
    {
        var realized = 0;
        for (var index = 0; index < MessagesList.Items.Count; index++)
        {
            if (MessagesList.ItemContainerGenerator.ContainerFromIndex(index) is not null)
            {
                realized++;
            }
        }

        var averageFrame = _debugFrameCount == 0 ? 0 : _debugFrameMilliseconds / _debugFrameCount;
        Debug.WriteLine(
            $"WitherChat scroll: items={MessagesList.Items.Count}, realized={realized}, collection/s={_debugCollectionChanges}, " +
            $"scrollChanged/s={_debugScrollChanges}, scrollCommands/s={_debugScrollCommands}, layouts/s={_debugLayoutPasses}, " +
            $"animations={AnimatedEmoteImage.ActiveCount}, pending={_viewModel.ActiveChannel?.PendingVisualMessages.Count ?? 0}, " +
            $"userScrolling={_isUserScrolling}, follow={_viewModel.AutoScroll}, frameMs={averageFrame:F1}, filterRefreshes={_viewModel.FilterRefreshCount}");
        _debugCollectionChanges = 0;
        _debugScrollChanges = 0;
        _debugScrollCommands = 0;
        _debugLayoutPasses = 0;
        _debugFrameMilliseconds = 0;
        _debugFrameCount = 0;
    }
#endif

    private void SetFollowMode(bool enabled)
    {
        if (!enabled)
        {
            _followWhenUserScrollEnds = false;
            _scrollToBottomPasses = 0;
        }

        _viewModel.AutoScroll = enabled;
        _userScrolledAwayFromBottom = !enabled;
        if (enabled)
        {
            if (_viewModel.ActiveChannel is not null)
            {
                _viewModel.ActiveChannel.NewMessagesBelowCount = 0;
            }

            UpdateUnreadBadge();
            SetJumpButtonVisible(false);
            return;
        }

        SetJumpButtonVisible(true);
    }

    private void ResetChatFollowState()
    {
        StopSmoothScrollAnimation();
        _pendingScrollToBottom = false;
        _userScrolledAwayFromBottom = false;
        _followWhenUserScrollEnds = false;
        _scrollToBottomPasses = 0;
        if (_viewModel.ActiveChannel is not null)
        {
            _viewModel.ActiveChannel.NewMessagesBelowCount = 0;
        }
        _viewModel.AutoScroll = true;
        UpdateUnreadBadge();
        SetJumpButtonVisible(false);
    }

    private void UpdateUnreadBadge()
    {
        var count = _viewModel.ActiveChannel?.NewMessagesBelowCount ?? 0;
        UnreadMessagesText.Text = count > 99 ? "99+" : count.ToString(CultureInfo.CurrentCulture);
        UnreadMessagesBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetJumpButtonVisible(bool visible)
    {
        if (_jumpButtonShown == visible)
        {
            return;
        }

        _jumpButtonShown = visible;
        var translate = (TranslateTransform)JumpToLatestHost.RenderTransform;
        if (AnimationService.ReduceMotion)
        {
            JumpToLatestHost.BeginAnimation(OpacityProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            JumpToLatestHost.Opacity = visible ? 1 : 0;
            translate.Y = visible ? 0 : 8;
            JumpToLatestHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (visible)
        {
            JumpToLatestHost.Visibility = Visibility.Visible;
        }

        var duration = TimeSpan.FromMilliseconds(160);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacity = new DoubleAnimation(visible ? 1 : 0, duration) { EasingFunction = easing };
        var movement = new DoubleAnimation(visible ? 0 : 8, duration) { EasingFunction = easing };
        if (!visible)
        {
            opacity.Completed += (_, _) =>
            {
                if (!_jumpButtonShown)
                {
                    JumpToLatestHost.Visibility = Visibility.Collapsed;
                }
            };
        }

        JumpToLatestHost.BeginAnimation(OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.YProperty, movement, HandoffBehavior.SnapshotAndReplace);
    }

    private void MessageInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (_viewModel.SendMessageCommand.CanExecute(null))
            {
                _viewModel.SendMessageCommand.Execute(null);
            }
        }
    }

    private void MessageItem_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<ListBoxItem>((DependencyObject)e.OriginalSource) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void MessageExpansionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChatMessageModel { IsLongMessage: true } message })
        {
            message.IsMessageExpanded = !message.IsMessageExpanded;
        }
    }

    private async void MessageContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        var message = menu.PlacementTarget is FrameworkElement element
            ? element.DataContext as ChatMessageModel
            : null;
        var moderationVisible = _viewModel.CanModerateTarget(message) ? Visibility.Visible : Visibility.Collapsed;
        var punishmentActionsVisible = _viewModel.CanBanOrTimeoutTarget(message) ? Visibility.Visible : Visibility.Collapsed;
        var punishment = _viewModel.GetActivePunishment(message);
        foreach (var item in menu.Items)
        {
            switch (item)
            {
                case MenuItem { Tag: "BanAction" } menuItem:
                    menuItem.Visibility = punishmentActionsVisible == Visibility.Visible && punishment?.Type != PunishmentType.Ban
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    menuItem.IsEnabled = !_viewModel.IsModerationOperationInProgress(message, true);
                    break;
                case MenuItem { Tag: "TimeoutAction" } menuItem:
                    menuItem.Visibility = punishmentActionsVisible == Visibility.Visible && punishment is null
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    menuItem.IsEnabled = !_viewModel.IsModerationOperationInProgress(message, false);
                    break;
                case MenuItem { Tag: "RemovePunishment" } menuItem:
                    menuItem.Visibility = _viewModel.CanRemovePunishment(message) ? Visibility.Visible : Visibility.Collapsed;
                    menuItem.Header = LocalizationService.Get(
                        LocalizationService.CurrentLanguage,
                        punishment?.Type switch
                        {
                            PunishmentType.Ban => "Unban",
                            PunishmentType.Timeout => "RemoveTimeout",
                            _ => "RemovePunishment"
                        });
                    break;
                case MenuItem { Tag: "DeleteMessage" } menuItem:
                    menuItem.Visibility = moderationVisible == Visibility.Visible && _viewModel.CanShowDeleteMessage(message)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    menuItem.IsEnabled = _viewModel.CanDeleteMessage(message);
                    break;
                case Separator { Tag: "ModerationSeparator" } separator:
                    separator.Visibility = moderationVisible;
                    break;
            }
        }

        await _viewModel.ObserveInteractiveTaskAsync(_viewModel.LoadUserProfileAsync(message));
    }

    private async void BanUser_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ObserveInteractiveTaskAsync(_viewModel.BanUserAsync(GetContextMessage(sender)));
    }

    private async void DeleteMessage_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ObserveInteractiveTaskAsync(_viewModel.DeleteMessageAsync(GetContextMessage(sender)));
    }

    private async void TimeoutTen_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ObserveInteractiveTaskAsync(_viewModel.TimeoutTenMinutesAsync(GetContextMessage(sender)));
    }

    private async void TimeoutCustom_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ObserveInteractiveTaskAsync(_viewModel.CustomTimeoutUserAsync(GetContextMessage(sender)));
    }

    private async void RemovePunishment_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ObserveInteractiveTaskAsync(_viewModel.RemovePunishmentAsync(GetContextMessage(sender)));
    }

    private void CopyUsername_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CopyUsername(GetContextMessage(sender));
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CopyMessage(GetContextMessage(sender));
    }

    private void OpenUser_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenUserOnTwitch(GetContextMessage(sender));
    }

    private static ChatMessageModel? GetContextMessage(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu menu } &&
            menu.PlacementTarget is FrameworkElement element)
        {
            return element.DataContext as ChatMessageModel;
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                return typed;
            }

            var result = FindVisualChild<T>(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T typed)
            {
                return typed;
            }

            parent = VisualTreeHelper.GetParent(parent);
        }

        return null;
    }

    private static class NativeMethods
    {
        public const int WmGetMinMaxInfo = 0x0024;
        public const uint MonitorDefaultToNearest = 0x00000002;
        private const uint AbmGetState = 0x00000004;
        private const uint AbsAutoHide = 0x00000001;

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

        [DllImport("shell32.dll")]
        private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);

        public static bool IsTaskbarAutoHidden()
        {
            var data = new AppBarData
            {
                Size = (uint)Marshal.SizeOf<AppBarData>()
            };
            return (SHAppBarMessage(AbmGetState, ref data).ToUInt64() & AbsAutoHide) != 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect WorkArea;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData
        {
            public uint Size;
            public IntPtr Window;
            public uint CallbackMessage;
            public uint Edge;
            public NativeRect Rect;
            public IntPtr Parameter;
        }
    }
}
