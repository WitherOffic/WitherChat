using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TwitchChatMvp.Models;
using TwitchChatMvp.Services;
using TwitchChatMvp.ViewModels;
using TwitchChatMvp.Views;

namespace TwitchChatMvp;

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
    private const double FollowBottomThreshold = 32;
    private const double SmoothScrollDurationMs = 130;
    private readonly ChatViewModel _viewModel = new();
    private readonly DispatcherTimer _smoothScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private ScrollViewer? _messagesScrollViewer;
    private DateTime _scrollAnimationStarted;
    private double _scrollStartOffset;
    private double _scrollTargetOffset;
    private bool _followOnScrollComplete;
    private bool _isProgrammaticScroll;
    private bool _scrollToEndQueued;
    private bool _jumpButtonShown;
    private int _unreadMessageCount;
    private bool _shutdownInProgress;
    private bool _shutdownComplete;
    private ActiveOverlayPanel _activeOverlay = ActiveOverlayPanel.None;
    private bool _overlayTransitioning;
    private bool _dialogOpen;
    private bool _dialogResult;
    private DispatcherFrame? _dialogFrame;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        SilentDialog.RegisterHost(ShowSilentDialog);
        _viewModel.Messages.CollectionChanged += Messages_CollectionChanged;
        MessagesList.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(MessagesList_PreviewMouseDown), true);
        _smoothScrollTimer.Tick += SmoothScrollTimer_Tick;
        SizeChanged += (_, _) => UpdateOverlayPanelSize();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AnimationService.AnimateWindowIn(this, offsetY: 10);
        await _viewModel.InitializeAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        _viewModel.Messages.CollectionChanged -= Messages_CollectionChanged;
        SilentDialog.ClearHost(ShowSilentDialog);
        _smoothScrollTimer.Stop();
        try
        {
            await _viewModel.DisposeAsync();
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
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
        var completion = new TaskCompletionSource<SettingsPanelResult?>();
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
            await CloseOverlayAsync().ConfigureAwait(true);
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
        var completion = new TaskCompletionSource<ConnectTwitchPanelResult?>();
        var panel = new ConnectTwitchPanel(language, lastReadOnlyChannel, channelSearchService);
        panel.Completed += async (_, result) =>
        {
            await CloseOverlayAsync().ConfigureAwait(true);
            completion.TrySetResult(result);
        };

        await OpenOverlayAsync(ActiveOverlayPanel.ConnectTwitch, panel).ConfigureAwait(true);
        return await completion.Task.ConfigureAwait(true);
    }

    public async Task OpenChatLogsPanelAsync(AppSettings settings)
    {
        await OpenOverlayAsync(ActiveOverlayPanel.ChatLogs, new ChatLogsPanel(settings)).ConfigureAwait(true);
    }

    public async Task OpenCreatorControlsPanelAsync()
    {
        var panel = new Border
        {
            Padding = new Thickness(28),
            Child = new TextBlock
            {
                Text = "Creator Controls",
                Foreground = TryFindResource("PrimaryText") as Brush ?? Brushes.White,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold
            }
        };

        await OpenOverlayAsync(ActiveOverlayPanel.CreatorControls, panel).ConfigureAwait(true);
    }

    private async Task OpenOverlayAsync(ActiveOverlayPanel panelType, FrameworkElement panel)
    {
        if (_overlayTransitioning)
        {
            return;
        }

        if (_activeOverlay != ActiveOverlayPanel.None)
        {
            await CloseOverlayAsync().ConfigureAwait(true);
        }

        _activeOverlay = panelType;
        OverlayContent.Content = panel;
        UpdateOverlayPanelSize();
        OverlayHost.Visibility = Visibility.Visible;
        _overlayTransitioning = true;
        try
        {
            await AnimationService.AnimatePanelInAsync(OverlayScrim, OverlayPanelChrome).ConfigureAwait(true);
        }
        finally
        {
            _overlayTransitioning = false;
        }
    }

    public async Task CloseOverlayAsync()
    {
        if (_activeOverlay == ActiveOverlayPanel.None || _overlayTransitioning)
        {
            return;
        }

        _overlayTransitioning = true;
        try
        {
            await AnimationService.AnimatePanelOutAsync(OverlayScrim, OverlayPanelChrome).ConfigureAwait(true);
        }
        finally
        {
            OverlayContent.Content = null;
            OverlayHost.Visibility = Visibility.Collapsed;
            _activeOverlay = ActiveOverlayPanel.None;
            _overlayTransitioning = false;
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
                _ = CloseOverlayAsync();
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

        _ = CloseOverlayAsync();
    }

    private bool ShowSilentDialog(string title, string message, bool confirm)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(() => ShowSilentDialog(title, message, confirm));
        }

        DialogTitleText.Text = title;
        DialogMessageText.Text = message;
        DialogCancelButton.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        var language = new SettingsService().Load().Language;
        DialogOkButton.Content = confirm
            ? (LocalizationService.NormalizeLanguage(language) == LocalizationService.English ? "Yes" : "Да")
            : "OK";

        _dialogOpen = true;
        _dialogResult = false;
        DialogOverlayHost.Visibility = Visibility.Visible;
        _ = AnimationService.AnimatePanelInAsync(DialogScrim, DialogCard, 0.45, offsetX: 0);

        _dialogFrame = new DispatcherFrame();
        Dispatcher.PushFrame(_dialogFrame);
        return _dialogResult;
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
        if (!_dialogOpen)
        {
            return;
        }

        _dialogResult = result;
        await AnimationService.AnimatePanelOutAsync(DialogScrim, DialogCard, offsetX: 0).ConfigureAwait(true);
        DialogOverlayHost.Visibility = Visibility.Collapsed;
        _dialogOpen = false;
        if (_dialogFrame is not null)
        {
            _dialogFrame.Continue = false;
            _dialogFrame = null;
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ResetChatFollowState();
            return;
        }

        if (e.NewItems is null)
        {
            return;
        }

        var shouldFollow = _viewModel.AutoScroll;
        if (!shouldFollow)
        {
            SetFollowMode(false);
            _unreadMessageCount += e.NewItems.Count;
            UpdateUnreadBadge();
        }
        else
        {
            QueueScrollToEnd();
        }

        foreach (var item in e.NewItems)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MessagesList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem listBoxItem)
                {
                    AnimationService.AnimateListBoxItem(listBoxItem);
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void MessagesList_Loaded(object sender, RoutedEventArgs e)
    {
        _messagesScrollViewer = FindVisualChild<ScrollViewer>(MessagesList);
        if (_messagesScrollViewer is not null)
        {
            _messagesScrollViewer.ScrollChanged += MessagesScrollViewer_ScrollChanged;
            ScrollToEndProgrammatically();
        }
    }

    private void MessagesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_messagesScrollViewer is null)
        {
            return;
        }

        if (_isProgrammaticScroll)
        {
            return;
        }

        if (e.VerticalChange < -0.01)
        {
            SetFollowMode(false);
            return;
        }

        if (e.VerticalChange > 0.01 && IsNearBottom())
        {
            SetFollowMode(true);
        }

        if (Math.Abs(e.ExtentHeightChange) > 0.01 && _viewModel.AutoScroll)
        {
            var previousScrollableHeight = _messagesScrollViewer.ScrollableHeight - e.ExtentHeightChange;
            var previousOffset = _messagesScrollViewer.VerticalOffset - e.VerticalChange;
            if (previousScrollableHeight - previousOffset <= FollowBottomThreshold)
            {
                QueueScrollToEnd();
            }
        }
    }

    private void MessagesList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            _smoothScrollTimer.Stop();
            _isProgrammaticScroll = false;
        }
    }

    private void MessagesList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_messagesScrollViewer is null || _messagesScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var currentTarget = _smoothScrollTimer.IsEnabled
            ? _scrollTargetOffset
            : _messagesScrollViewer.VerticalOffset;
        var target = Math.Clamp(currentTarget - (e.Delta * 0.55), 0, _messagesScrollViewer.ScrollableHeight);
        var followAtEnd = e.Delta < 0 && target >= _messagesScrollViewer.ScrollableHeight - FollowBottomThreshold;

        if (e.Delta > 0 || !followAtEnd)
        {
            SetFollowMode(false);
        }

        StartSmoothScroll(target, followAtEnd);
        e.Handled = true;
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

        _smoothScrollTimer.Stop();
        _isProgrammaticScroll = true;
        _scrollStartOffset = _messagesScrollViewer.VerticalOffset;
        _scrollTargetOffset = Math.Clamp(target, 0, _messagesScrollViewer.ScrollableHeight);
        _scrollAnimationStarted = DateTime.UtcNow;
        _followOnScrollComplete = followOnComplete;

        if (AnimationService.ReduceMotion || Math.Abs(_scrollTargetOffset - _scrollStartOffset) < 0.5)
        {
            _messagesScrollViewer.ScrollToVerticalOffset(_scrollTargetOffset);
            _isProgrammaticScroll = false;
            if (followOnComplete)
            {
                SetFollowMode(true);
            }
            return;
        }

        _smoothScrollTimer.Start();
    }

    private void SmoothScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (_messagesScrollViewer is null)
        {
            _smoothScrollTimer.Stop();
            _isProgrammaticScroll = false;
            return;
        }

        var progress = Math.Clamp((DateTime.UtcNow - _scrollAnimationStarted).TotalMilliseconds / SmoothScrollDurationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        _messagesScrollViewer.ScrollToVerticalOffset(_scrollStartOffset + ((_scrollTargetOffset - _scrollStartOffset) * eased));

        if (progress < 1)
        {
            return;
        }

        _smoothScrollTimer.Stop();
        _isProgrammaticScroll = false;
        if (_followOnScrollComplete || IsNearBottom())
        {
            SetFollowMode(true);
            QueueScrollToEnd();
        }
    }

    private bool IsNearBottom() =>
        _messagesScrollViewer is null ||
        _messagesScrollViewer.ScrollableHeight - _messagesScrollViewer.VerticalOffset <= FollowBottomThreshold;

    private void QueueScrollToEnd()
    {
        if (_scrollToEndQueued)
        {
            return;
        }

        _scrollToEndQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _scrollToEndQueued = false;
            if (_viewModel.AutoScroll)
            {
                ScrollToEndProgrammatically();
            }
        }), DispatcherPriority.Loaded);
    }

    private void ScrollToEndProgrammatically()
    {
        if (_messagesScrollViewer is null || !_viewModel.AutoScroll)
        {
            return;
        }

        _smoothScrollTimer.Stop();
        _isProgrammaticScroll = true;
        _messagesScrollViewer.ScrollToEnd();
        Dispatcher.BeginInvoke(new Action(() => _isProgrammaticScroll = false), DispatcherPriority.ContextIdle);
    }

    private void SetFollowMode(bool enabled)
    {
        _viewModel.AutoScroll = enabled;
        if (enabled)
        {
            _unreadMessageCount = 0;
            UpdateUnreadBadge();
        }

        SetJumpButtonVisible(!enabled);
    }

    private void ResetChatFollowState()
    {
        _smoothScrollTimer.Stop();
        _isProgrammaticScroll = false;
        _unreadMessageCount = 0;
        _viewModel.AutoScroll = true;
        UpdateUnreadBadge();
        SetJumpButtonVisible(false);
    }

    private void UpdateUnreadBadge()
    {
        UnreadMessagesText.Text = _unreadMessageCount > 99 ? "99+" : _unreadMessageCount.ToString();
        UnreadMessagesBadge.Visibility = _unreadMessageCount > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void MessageContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        var moderationVisible = _viewModel.CanModerate ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in menu.Items)
        {
            switch (item)
            {
                case MenuItem { Tag: "Danger" } menuItem:
                    menuItem.Visibility = moderationVisible;
                    break;
                case Separator { Tag: "ModerationSeparator" } separator:
                    separator.Visibility = moderationVisible;
                    break;
            }
        }
    }

    private async void BanUser_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.BanUserAsync(GetContextMessage(sender));
    }

    private async void TimeoutTen_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.TimeoutUserAsync(GetContextMessage(sender), 600);
    }

    private async void TimeoutCustom_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.TimeoutUserAsync(GetContextMessage(sender), 600);
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
}
