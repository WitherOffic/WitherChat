using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Runtime.InteropServices;
using WitherChat.Core.Models;
using WitherChat.Desktop.ViewModels;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Platforms;
using WitherChat.Desktop.Controls;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.Views;

public partial class MainWindow : Window
{
    private const double FollowLatestThreshold = 32;
    private readonly List<Action> _settingsLayoutRestorations = [];
    private bool _usingNarrowSettingsLayout;
    private const double WheelScrollPixelsPerNotch = 72;
    private const double MiddleScrollDeadZone = 12;
    private const int ScrollToBottomPassLimit = 8;
    private static readonly TimeSpan ScrollIdleDelay = TimeSpan.FromMilliseconds(250);
    private ScrollViewer? _messageScrollViewer;
    private ScrollViewer? _twitchSplitScrollViewer;
    private ScrollViewer? _youTubeSplitScrollViewer;
    private MainWindowViewModel? _subscribedViewModel;
    private bool _pendingScrollToBottom;
    private bool _isProgrammaticScroll;
    private bool _isUserScrolling;
    private bool _scrollBarPointerActive;
    private bool _followTwitchSplit = true;
    private bool _followYouTubeSplit = true;
    private bool _splitScrollProgrammatic;
    private bool _splitScrollUpdatePending;
    private bool _middleScrollActive;
    private IPointer? _middleScrollPointer;
    private ListBox? _middleScrollTargetList;
    private ScrollViewer? _middleScrollTargetViewer;
    private Point _middleScrollAnchor;
    private Point _middleScrollCurrent;
    private bool _followWhenUserScrollEnds;
    private int _scrollToBottomPasses;
    private long _scrollStateVersion;
    private long _lastUserScrollInputAt;
    private bool _trayNoticeShown;
    private readonly DispatcherTimer _scrollIdleTimer = new();
    private readonly DispatcherTimer _middleScrollTimer = new();
    private readonly DispatcherTimer _contextTutorialLayoutTimer = new();
    private double _normalWidth = 1100;
    private double _normalHeight = 760;
    private WindowState _normalWindowState = WindowState.Normal;
    private bool _normalHeaderExpanded = true;
    private bool _normalComposerExpanded = true;
    private int _channelEditorTransitionVersion;
    private int _headerPanelTransitionVersion;
    private int _composerPanelTransitionVersion;
    private int _onboardingStepTransitionVersion;
    private int _onboardingTextTransitionVersion;
    private bool _onboardingFocusInitialized;
    private Rect? _lastOnboardingTargetRect;
    private bool _onboardingFocusUpdatePending;
    private int _onboardingFocusTransitionVersion;
    private Rect? _renderedOnboardingTargetRect;
    private bool _windowTransitionInProgress;
    private bool _compactModeTransitionInProgress;
    private bool _closeAnimationInProgress;
    private bool _wasMinimized;
    private int _windowOpenAnimationVersion;
    private int _headerMoreToolsAnimationVersion;
    private readonly Dictionary<Control, int> _panelTransitionVersions = [];
    private readonly Dictionary<Control, int> _modalFocusVersions = [];
    private readonly Dictionary<Control, Control?> _modalFocusReturnTargets = [];
    private readonly Dictionary<ContextMenu, int> _contextMenuTransitionVersions = [];
    private readonly HashSet<ContextMenu> _contextMenusCompletingClose = [];

    public MainWindow()
    {
        InitializeComponent();
        CaptureResponsivePanelLayout();
        if (HeaderMoreToolsButton.Flyout is { } headerMoreToolsFlyout)
        {
            headerMoreToolsFlyout.Opened += OnHeaderMoreToolsFlyoutOpened;
            headerMoreToolsFlyout.Closed += OnHeaderMoreToolsFlyoutClosed;
        }
        _scrollIdleTimer.Interval = ScrollIdleDelay;
        _scrollIdleTimer.Tick += OnScrollIdleTimerTick;
        _middleScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
        _middleScrollTimer.Tick += OnMiddleScrollTimerTick;
        _contextTutorialLayoutTimer.Interval = TimeSpan.FromMilliseconds(110);
        _contextTutorialLayoutTimer.Tick += OnContextTutorialLayoutTimerTick;
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        Opened += OnOpened;
        SizeChanged += OnWindowSizeChanged;
        PropertyChanged += OnWindowPropertyChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        AddHandler(KeyDownEvent, OnWindowPreviewKeyDown, RoutingStrategies.Tunnel);
        MessagesList.AddHandler(
            InputElement.PointerPressedEvent,
            MessagesList_OnPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        MessagesList.AddHandler(
            InputElement.PointerWheelChangedEvent,
            MessagesList_OnPointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        MessagesList.AddHandler(
            InputElement.PointerReleasedEvent,
            MessagesList_OnPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        MessagesList.AddHandler(
            InputElement.PointerMovedEvent,
            MessagesList_OnPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        MessagesList.AddHandler(
            InputElement.PointerCaptureLostEvent,
            MessagesList_OnPointerCaptureLost,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        MessagesList.AddHandler(
            InputElement.ScrollGestureEvent,
            MessagesList_OnScrollGesture,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        MessagesList.AddHandler(
            InputElement.KeyDownEvent,
            MessagesList_OnKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddSplitMessageInputHandlers(TwitchMessagesList);
        AddSplitMessageInputHandlers(YouTubeMessagesList);
    }

    private void AddSplitMessageInputHandlers(ListBox list)
    {
        list.AddHandler(
            InputElement.PointerPressedEvent,
            SplitMessagesList_OnPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        list.AddHandler(
            InputElement.PointerMovedEvent,
            SplitMessagesList_OnPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        list.AddHandler(
            InputElement.PointerCaptureLostEvent,
            SplitMessagesList_OnPointerCaptureLost,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        UnsubscribeFromViewModel();
        if (DataContext is not MainWindowViewModel viewModel)
        {
            _modalFocusVersions.Clear();
            _modalFocusReturnTargets.Clear();
            SetChannelEditorStateImmediate(false);
            SetAllOverlayStatesImmediate(null);
            return;
        }

        _subscribedViewModel = viewModel;
        viewModel.UpdateActualTheme(ActualThemeVariant == ThemeVariant.Light);
        AnimatedEmoteImage.SetReduceMotion(viewModel.ReduceMotion);
        viewModel.MessagesChanged += OnMessagesChanged;
        viewModel.ScrollToLatestRequested += OnScrollToLatestRequested;
        viewModel.OpenUriRequested += OnOpenUriRequested;
        viewModel.OpenLogDirectoryRequested += OnOpenLogDirectoryRequested;
        viewModel.CopyTextRequested += OnCopyTextRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SetChannelEditorStateImmediate(viewModel.IsChannelEditorOpen);
        SetAllOverlayStatesImmediate(viewModel);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        AttachMessageScrollViewer();
        AttachSplitMessageScrollViewers();
        Dispatcher.UIThread.Post(AttachMessageScrollViewer, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(AttachSplitMessageScrollViewers, DispatcherPriority.Loaded);
        UpdateSettingsCardSize();
    }

    private void AttachMessageScrollViewer()
    {
        var scrollViewer = MessagesList
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (scrollViewer is null || ReferenceEquals(scrollViewer, _messageScrollViewer))
        {
            return;
        }

        if (_messageScrollViewer is not null)
        {
            _messageScrollViewer.ScrollChanged -= OnMessageScrollChanged;
        }

        _messageScrollViewer = scrollViewer;
        scrollViewer.ScrollChanged += OnMessageScrollChanged;
        QueueScrollToEnd();
    }

    private void AttachSplitMessageScrollViewers()
    {
        AttachSplitMessageScrollViewer(
            TwitchMessagesList,
            ref _twitchSplitScrollViewer,
            OnTwitchSplitScrollChanged);
        AttachSplitMessageScrollViewer(
            YouTubeMessagesList,
            ref _youTubeSplitScrollViewer,
            OnYouTubeSplitScrollChanged);
    }

    private static void AttachSplitMessageScrollViewer(
        ListBox list,
        ref ScrollViewer? current,
        EventHandler<ScrollChangedEventArgs> handler)
    {
        var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is null || ReferenceEquals(viewer, current))
        {
            return;
        }

        if (current is not null)
        {
            current.ScrollChanged -= handler;
        }
        current = viewer;
        current.ScrollChanged += handler;
    }

    private void OnTwitchSplitScrollChanged(object? sender, ScrollChangedEventArgs eventArgs)
    {
        if (!_splitScrollProgrammatic && _twitchSplitScrollViewer is not null)
        {
            _followTwitchSplit = IsNearBottom(_twitchSplitScrollViewer);
            UpdateSplitLatestButtons();
        }
    }

    private void OnYouTubeSplitScrollChanged(object? sender, ScrollChangedEventArgs eventArgs)
    {
        if (!_splitScrollProgrammatic && _youTubeSplitScrollViewer is not null)
        {
            _followYouTubeSplit = IsNearBottom(_youTubeSplitScrollViewer);
            UpdateSplitLatestButtons();
        }
    }

    private void OnOpened(object? sender, EventArgs eventArgs)
    {
        AttachMessageScrollViewer();
        AttachSplitMessageScrollViewers();
        DispatcherTimer.RunOnce(AttachMessageScrollViewer, TimeSpan.FromMilliseconds(50));
        DispatcherTimer.RunOnce(AttachSplitMessageScrollViewers, TimeSpan.FromMilliseconds(50));
        EnableWindowsSystemWindowAnimations();
        InstallWindowsSystemCommandHook();
        DispatcherTimer.RunOnce(
            () =>
            {
                EnableWindowsSystemWindowAnimations();
                InstallWindowsSystemCommandHook();
            },
            TimeSpan.FromMilliseconds(120));
    }

    private void EnableWindowsSystemWindowAnimations()
    {
        if (_isObsDockLayout) return;
        var platformHandle = TryGetPlatformHandle();
        if (platformHandle is not null &&
            string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.Ordinal))
        {
            _ = WindowsWindowAnimation.EnableSystemMinimizeAnimation(platformHandle.Handle);
        }
    }

    private void InstallWindowsSystemCommandHook()
    {
        var platformHandle = TryGetPlatformHandle();
        if (platformHandle is not null &&
            string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.Ordinal) &&
            Avalonia.Application.Current is App app)
        {
            _ = WindowsWindowAnimation.InstallSystemCommandHook(
                platformHandle.Handle,
                app.PrepareForSystemShutdown,
                app.CompleteSystemShutdown);
        }
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        UpdateObsDockLayout();
        UpdateSettingsCardSize();
        if (ChannelEditorCard.IsVisible)
        {
            PositionChannelEditorCard();
        }
        if (DataContext is MainWindowViewModel
            {
                IsOnboardingOpen: true,
                IsMainWindowTutorial: true
            } viewModel)
        {
            PositionOnboardingElements(
                viewModel.OnboardingStep,
                updateFocus: !_onboardingFocusUpdatePending);
        }
    }

    private void UpdateSettingsCardSize()
    {
        if (SettingsCard is null || ConnectPanelCard is null || Bounds.Width <= 0)
        {
            return;
        }

        var narrowMoments = Bounds.Width < 520;
        var reserveMomentTitleBar = narrowMoments || Bounds.Height < 662;
        MomentsPanelOverlay.Margin = new Thickness(0, reserveMomentTitleBar ? 42 : 0, 0, 0);
        MomentsPanelOverlay.Padding = new Thickness(reserveMomentTitleBar ? 10 : 0);
        MomentsPanelCard.Width = Math.Min(760, Math.Max(1, Bounds.Width - (reserveMomentTitleBar ? 20 : 0)));
        MomentsPanelCard.Height = Math.Min(600, Math.Max(1, Bounds.Height - (reserveMomentTitleBar ? 62 : 0)));
        MomentsPanelCard.Padding = new Thickness(narrowMoments ? 12 : 22);
        MomentsPanelTitle.FontSize = narrowMoments ? 20 : 24;
        MomentsPanelDescription.IsVisible = !narrowMoments;
        ExportMomentsButton.HorizontalAlignment = narrowMoments ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        var narrowSettings = Bounds.Width < 740;
        var settingsEdge = narrowSettings ? 8d : 18d;
        SettingsCard.Width = Math.Min(940, Math.Max(1, Bounds.Width - settingsEdge * 2));
        SettingsCard.Margin = new Thickness(settingsEdge, narrowSettings ? 46 : settingsEdge, settingsEdge, settingsEdge);
        SettingsCard.Padding = new Thickness(narrowSettings ? 12 : 24);
        SettingsBodyGrid.ColumnDefinitions = new ColumnDefinitions(narrowSettings ? "*" : "210,18,*");
        SettingsBodyGrid.RowDefinitions = new RowDefinitions(narrowSettings ? "Auto,12,*" : "*");
        SettingsNavigationCard.IsVisible = !narrowSettings;
        CompactSettingsSectionSelector.IsVisible = narrowSettings;
        Grid.SetColumnSpan(CompactSettingsSectionSelector, narrowSettings ? 1 : 3);
        Grid.SetColumn(SettingsContentScrollViewer, narrowSettings ? 0 : 2);
        Grid.SetRow(SettingsContentScrollViewer, narrowSettings ? 2 : 0);
        ApplySettingsNarrowLayout(narrowSettings);
        SettingsIntroLabel.IsVisible = !narrowSettings;
        SettingsVersionLabel.IsVisible = !narrowSettings;
        SettingsActionsGrid.ColumnDefinitions = new ColumnDefinitions(narrowSettings ? "*,8,*" : "*,Auto,Auto");
        Grid.SetColumn(CancelSettingsButton, narrowSettings ? 0 : 1);
        CancelSettingsButton.Margin = narrowSettings ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        CancelSettingsButton.HorizontalAlignment = narrowSettings ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        SettingsActionsGrid.Children.OfType<Button>().Last().HorizontalAlignment = narrowSettings ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        var narrow = Bounds.Width < 520;
        var edge = narrow ? 8d : 18d;
        ConnectPanelCard.Margin = new Thickness(edge, narrow ? 46 : edge, edge, edge);
        ConnectPanelCard.Padding = new Thickness(narrow ? 12 : 24);
        ConnectPanelCard.Width = Math.Min(940, Math.Max(1, Bounds.Width - edge * 2));
        ConnectPanelHeader.ColumnDefinitions = new ColumnDefinitions(
            narrow ? "Auto,*,Auto" : "*,Auto,8,Auto");
        ConnectPanelHeader.RowDefinitions = new RowDefinitions(narrow ? "Auto,12,Auto" : "Auto");
        Grid.SetColumnSpan(ConnectHeaderText, narrow ? 3 : 1);
        Grid.SetRow(ConnectHelpButton, narrow ? 2 : 0);
        Grid.SetColumn(ConnectHelpButton, narrow ? 0 : 1);
        Grid.SetRow(ConnectPanelBackButton, narrow ? 2 : 0);
        Grid.SetColumn(ConnectPanelBackButton, narrow ? 2 : 3);
        ConnectManualInputRow.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,12,Auto");
        ConnectManualInputRow.RowDefinitions = new RowDefinitions(narrow ? "Auto,8,Auto" : "Auto");
        Grid.SetRow(ConnectPanelWatchButton, narrow ? 2 : 0);
        Grid.SetColumn(ConnectPanelWatchButton, narrow ? 0 : 2);
        ConnectPanelWatchButton.Width = narrow ? double.NaN : 128;
        ConnectFollowedPermissionRow.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,12,Auto");
        ConnectFollowedPermissionRow.RowDefinitions = new RowDefinitions(narrow ? "Auto,8,Auto" : "Auto");
        Grid.SetColumn(ConnectFollowedPermissionButton, narrow ? 0 : 2);
        Grid.SetRow(ConnectFollowedPermissionButton, narrow ? 2 : 0);
        ConnectFollowedPermissionButton.HorizontalAlignment = narrow
            ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        ConnectFollowedInputRow.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,10,Auto");
        ConnectFollowedInputRow.RowDefinitions = new RowDefinitions(narrow ? "Auto,8,Auto" : "Auto");
        Grid.SetColumn(ConnectFollowedRefreshButton, narrow ? 0 : 2);
        Grid.SetRow(ConnectFollowedRefreshButton, narrow ? 2 : 0);
        ConnectFollowedRefreshButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        MomentEditorCard.Padding = new Thickness(narrow ? 12 : 20);
        MomentActionsPanel.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "Auto,8,Auto");
        MomentActionsPanel.RowDefinitions = new RowDefinitions(narrow ? "Auto,8,Auto" : "Auto");
        MomentActionsPanel.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        Grid.SetColumn(SaveMomentButton, narrow ? 0 : 2);
        Grid.SetRow(SaveMomentButton, narrow ? 2 : 0);
        ApplyResponsivePanelLayout();
    }

    private void MomentRow_OnSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        if (sender is not Grid row || row.Bounds.Width <= 0 || row.Children.Count != 5 ||
            row.Children[0] is not Grid identity || identity.Children.Count != 3 ||
            identity.Children[1] is not TextBlock channel || identity.Children[2] is not TextBlock user ||
            row.Children[1] is not TextBlock time || row.Children[2] is not TextBlock text ||
            row.Children[3] is not TextBlock note || row.Children[4] is not Button delete) return;
        var narrow = row.Bounds.Width < 360;
        if (row.ColumnDefinitions.Count == (narrow ? 1 : 2)) return;
        row.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,Auto");
        row.RowDefinitions = new RowDefinitions(narrow ? "Auto,4,Auto,8,Auto,8,Auto,8,Auto" : "Auto,Auto,Auto");
        identity.ColumnDefinitions = new ColumnDefinitions(narrow ? "Auto,8,*" : "Auto,8,Auto,8,*");
        identity.RowDefinitions = new RowDefinitions(narrow ? "Auto,4,Auto" : "Auto");
        channel.MaxWidth = narrow ? double.PositiveInfinity : 150;
        Grid.SetColumn(user, narrow ? 0 : 4);
        Grid.SetRow(user, narrow ? 2 : 0);
        Grid.SetColumnSpan(user, narrow ? 3 : 1);
        Grid.SetColumn(time, narrow ? 0 : 1);
        Grid.SetRow(time, narrow ? 2 : 0);
        Grid.SetRow(text, narrow ? 4 : 1);
        Grid.SetColumnSpan(text, narrow ? 1 : 2);
        text.MaxHeight = narrow ? 72 : double.PositiveInfinity;
        Grid.SetRow(note, narrow ? 6 : 2);
        note.MaxHeight = narrow ? 56 : double.PositiveInfinity;
        Grid.SetRow(delete, narrow ? 8 : 2);
        Grid.SetColumn(delete, narrow ? 0 : 1);
    }
    private void ChannelResultRow_OnSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        if (sender is not Grid row || row.Bounds.Width <= 0 || row.Children.Count != 3 ||
            row.Children[0] is not Border avatar ||
            row.Children[1] is not StackPanel identity || identity.Children.Count != 2 ||
            identity.Children[1] is not Grid metadata ||
            row.Children[2] is not Grid state || state.Children.Count != 2 ||
            state.Children[0] is not StackPanel liveStatus ||
            state.Children[1] is not StackPanel viewers)
        {
            return;
        }

        // React to the actual row width, not the window mode: a small panel
        // or a resize must preserve the same readable channel selection rows.
        var narrow = row.Bounds.Width < 360;
        if (row.ColumnDefinitions.Count == (narrow ? 3 : 5))
        {
            return;
        }
        row.ColumnDefinitions = new ColumnDefinitions(narrow ? "32,10,*" : "40,12,*,16,112");
        row.RowDefinitions = new RowDefinitions(narrow ? "Auto,8,Auto" : "Auto");
        avatar.Width = avatar.Height = narrow ? 32 : 40;
        metadata.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "Auto,7,*");
        metadata.RowDefinitions = new RowDefinitions(narrow ? "Auto,4,Auto" : "Auto");
        Grid.SetColumn(metadata.Children[1], narrow ? 0 : 2);
        Grid.SetRow(metadata.Children[1], narrow ? 2 : 0);
        Grid.SetColumn(state, narrow ? 2 : 4);
        Grid.SetRow(state, narrow ? 2 : 0);
        state.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        state.ColumnDefinitions = new ColumnDefinitions(narrow ? "Auto,12,Auto" : "*");
        state.RowDefinitions = new RowDefinitions(narrow ? "Auto" : "Auto,Auto");
        Grid.SetColumn(viewers, narrow ? 2 : 0);
        Grid.SetRow(viewers, narrow ? 0 : 1);
        liveStatus.HorizontalAlignment = viewers.HorizontalAlignment = narrow
            ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        viewers.Margin = narrow ? new Thickness(0) : new Thickness(0, 3, 0, 0);
    }

    private void ApplySettingsNarrowLayout(bool narrow)
    {
        foreach (var card in new[] { ProgramSettingsCard, ChatSettingsCard, ChatLogsSettingsCard,
                     OverlaySettingsCard, AccountSettingsCard, DonateSettingsCard, AdvancedSettingsCard })
            card.Padding = new Thickness(narrow ? 12 : 22);
        if (_usingNarrowSettingsLayout == narrow) return;
        _usingNarrowSettingsLayout = narrow;
        if (!narrow)
        {
            foreach (var restore in _settingsLayoutRestorations) restore();
            _settingsLayoutRestorations.Clear();
            return;
        }
        foreach (var panel in SettingsContentScrollViewer.GetLogicalDescendants().OfType<UniformGrid>())
        {
            var columns = panel.Columns;
            var rows = panel.Rows;
            _settingsLayoutRestorations.Add(() => { panel.Columns = columns; panel.Rows = rows; });
            panel.Columns = 1;
            panel.Rows = 0;
        }
        foreach (var button in DonateSettingsCard.GetLogicalDescendants().OfType<Button>()
                     .Where(button => button.Classes.Contains("support-brand") || button.Classes.Contains("support-wallet")))
        {
            var height = button.Height;
            _settingsLayoutRestorations.Add(() => button.Height = height);
            button.Height = double.NaN;
            if (!button.Classes.Contains("support-wallet") || button.Content is not Grid grid) continue;
            var columns = grid.ColumnDefinitions;
            var rows = grid.RowDefinitions;
            var icon = grid.Children[0];
            var copy = grid.Children[2];
            var copyColumn = Grid.GetColumn(copy);
            var copyRow = Grid.GetRow(copy);
            var iconSpan = Grid.GetRowSpan(icon);
            _settingsLayoutRestorations.Add(() =>
            {
                grid.ColumnDefinitions = columns;
                grid.RowDefinitions = rows;
                Grid.SetColumn(copy, copyColumn);
                Grid.SetRow(copy, copyRow);
                Grid.SetRowSpan(icon, iconSpan);
            });
            grid.ColumnDefinitions = new ColumnDefinitions("42,*");
            grid.RowDefinitions = new RowDefinitions("Auto,4,Auto");
            Grid.SetColumn(copy, 1);
            Grid.SetRow(copy, 2);
            Grid.SetRowSpan(icon, 3);
        }
        foreach (var panel in SettingsContentScrollViewer.GetLogicalDescendants().OfType<StackPanel>()
                     .Where(panel => panel.Orientation == Orientation.Horizontal &&
                                     !(panel.Parent?.Parent is Button button &&
                                       button.Classes.Contains("support-wallet"))))
        {
            _settingsLayoutRestorations.Add(() => panel.Orientation = Orientation.Horizontal);
            panel.Orientation = Orientation.Vertical;
        }
        foreach (var text in SettingsContentScrollViewer.GetLogicalDescendants().OfType<TextBlock>()
                     .Where(text => text.TextWrapping != TextWrapping.Wrap &&
                                    text.TextTrimming == TextTrimming.None))
        {
            var wrapping = text.TextWrapping;
            _settingsLayoutRestorations.Add(() => text.TextWrapping = wrapping);
            text.TextWrapping = TextWrapping.Wrap;
        }
        foreach (var grid in SettingsContentScrollViewer.GetLogicalDescendants().OfType<Grid>()
                     .Where(grid => grid.ColumnDefinitions.Count > 1 &&
                                    grid.ColumnDefinitions[0].Width.IsStar && grid.Children.Count > 1))
        {
            var columns = grid.ColumnDefinitions;
            var rows = grid.RowDefinitions;
            var spacing = grid.ColumnSpacing;
            var cells = grid.Children.Select(child => (
                Child: child, Row: Grid.GetRow(child), Column: Grid.GetColumn(child),
                RowSpan: Grid.GetRowSpan(child), ColumnSpan: Grid.GetColumnSpan(child))).ToArray();
            // Restore exact original definitions and placements on expansion.
            _settingsLayoutRestorations.Add(() =>
            {
                grid.ColumnDefinitions = columns;
                grid.RowDefinitions = rows;
                grid.ColumnSpacing = spacing;
                foreach (var cell in cells)
                {
                    Grid.SetRow(cell.Child, cell.Row);
                    Grid.SetColumn(cell.Child, cell.Column);
                    Grid.SetRowSpan(cell.Child, cell.RowSpan);
                    Grid.SetColumnSpan(cell.Child, cell.ColumnSpan);
                }
            });
            grid.ColumnSpacing = 0;
            if (cells.Length == 4 && cells[0].Child is TextBlock &&
                cells[1].Child is Button && cells[2].Child is TextBox && cells[3].Child is Button)
            {
                grid.ColumnDefinitions = new ColumnDefinitions("38,6,*,6,38");
                grid.RowDefinitions = new RowDefinitions("Auto,8,Auto");
                Grid.SetColumnSpan(cells[0].Child, 5);
                Grid.SetColumn(cells[0].Child, 0);
                Grid.SetRow(cells[0].Child, 0);
                for (var index = 1; index < cells.Length; index++)
                {
                    Grid.SetRow(cells[index].Child, 2);
                    Grid.SetColumn(cells[index].Child, (index - 1) * 2);
                }
            }
            else
            {
                grid.ColumnDefinitions = new ColumnDefinitions("*");
                grid.RowDefinitions = new RowDefinitions(string.Join(",", cells.SelectMany(
                    (_, index) => index == 0 ? new[] { "Auto" } : new[] { "8", "Auto" })));
                var ordered = cells.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column).ToArray();
                for (var index = 0; index < ordered.Length; index++)
                {
                    Grid.SetRow(ordered[index].Child, index * 2);
                    Grid.SetColumn(ordered[index].Child, 0);
                    Grid.SetColumnSpan(ordered[index].Child, 1);
                    Grid.SetRowSpan(ordered[index].Child, 1);
                }
            }
        }
    }

    private void OnMessageScrollChanged(object? sender, ScrollChangedEventArgs eventArgs)
    {
        if (_messageScrollViewer is null ||
            DataContext is not MainWindowViewModel viewModel ||
            _compactModeTransitionInProgress ||
            viewModel.IsMessageRenderingSuspended)
        {
            return;
        }

        var isAtBottom = IsNearBottom(_messageScrollViewer);
        if (_isProgrammaticScroll || _pendingScrollToBottom)
        {
            return;
        }

        if (_isUserScrolling)
        {
            if (_scrollBarPointerActive)
            {
                _lastUserScrollInputAt = Stopwatch.GetTimestamp();
            }
            if (eventArgs.OffsetDelta.Y < -0.01 || !isAtBottom)
            {
                _followWhenUserScrollEnds = false;
                SetFollowMode(false);
            }
            else if (eventArgs.OffsetDelta.Y > 0.01)
            {
                _followWhenUserScrollEnds = true;
            }

            return;
        }

        if (viewModel.IsFollowingLatest)
        {
            if (Math.Abs(eventArgs.ExtentDelta.Y) > 0.01 ||
                Math.Abs(eventArgs.ViewportDelta.Y) > 0.01)
            {
                QueueScrollToEnd();
            }

            return;
        }

        if (isAtBottom)
        {
            SetFollowMode(true);
            QueueScrollToEnd();
        }
    }

    private void MessagesList_OnPointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
    {
        if (ApplyMessageWheelDelta(eventArgs.Delta.Y))
        {
            eventArgs.Handled = true;
        }
    }

    private bool ApplyMessageWheelDelta(double deltaY)
    {
        if (_compactModeTransitionInProgress ||
            DataContext is not MainWindowViewModel viewModel ||
            viewModel.IsMessageRenderingSuspended ||
            Math.Abs(deltaY) < 0.001)
        {
            return false;
        }

        AttachMessageScrollViewer();
        return ApplyMessageScrollStep(-deltaY * WheelScrollPixelsPerNotch);
    }

    private void MessagesList_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        var point = eventArgs.GetCurrentPoint(MessagesList);
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
        {
            if (_middleScrollActive)
            {
                StopMiddleScroll();
            }
            else
            {
                AttachMessageScrollViewer();
                StartMiddleScroll(
                    MessagesList,
                    _messageScrollViewer,
                    eventArgs.Pointer,
                    eventArgs.GetPosition(ChatViewportCard));
            }

            eventArgs.Handled = true;
            return;
        }

        if (_middleScrollActive &&
            point.Properties.PointerUpdateKind is PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.RightButtonPressed)
        {
            StopMiddleScroll();
        }

        if (eventArgs.Source is not Visual source ||
            source is not ScrollBar && !source.GetVisualAncestors().OfType<ScrollBar>().Any())
        {
            return;
        }

        _scrollBarPointerActive = true;
        MarkUserScrollInput();
    }

    private void MessagesList_OnPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_scrollBarPointerActive)
        {
            return;
        }

        _scrollBarPointerActive = false;
        _lastUserScrollInputAt = Stopwatch.GetTimestamp();
        _scrollIdleTimer.Stop();
        _scrollIdleTimer.Start();
    }

    private void MessagesList_OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_middleScrollActive || !ReferenceEquals(eventArgs.Pointer, _middleScrollPointer))
        {
            return;
        }

        _middleScrollCurrent = eventArgs.GetPosition(ChatViewportCard);
        eventArgs.Handled = true;
    }

    private void MessagesList_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs)
    {
        if (_middleScrollActive && ReferenceEquals(eventArgs.Pointer, _middleScrollPointer))
        {
            StopMiddleScroll(releasePointer: false);
        }
    }

    private void StartMiddleScroll(
        ListBox targetList,
        ScrollViewer? targetViewer,
        IPointer pointer,
        Point position)
    {
        if (targetViewer is null ||
            targetViewer.Extent.Height <= targetViewer.Viewport.Height)
        {
            return;
        }

        StopMiddleScroll();
        MarkUserScrollInput();
        _middleScrollActive = true;
        _middleScrollPointer = pointer;
        _middleScrollTargetList = targetList;
        _middleScrollTargetViewer = targetViewer;
        _middleScrollAnchor = position;
        _middleScrollCurrent = position;
        MiddleScrollIndicator.RenderTransform = new TranslateTransform(
            Math.Clamp(position.X - 16, 0, Math.Max(0, ChatViewportCard.Bounds.Width - 32)),
            Math.Clamp(position.Y - 16, 0, Math.Max(0, ChatViewportCard.Bounds.Height - 32)));
        JumpToLatestContainer.Opacity = 0;
        JumpToLatestContainer.IsHitTestVisible = false;
        MiddleScrollIndicator.IsVisible = true;
        pointer.Capture(targetList);
        _middleScrollTimer.Start();
    }

    private void StopMiddleScroll(bool releasePointer = true)
    {
        var pointer = _middleScrollPointer;
        _middleScrollActive = false;
        _middleScrollPointer = null;
        _middleScrollTargetList = null;
        _middleScrollTargetViewer = null;
        _middleScrollTimer.Stop();
        MiddleScrollIndicator.IsVisible = false;
        JumpToLatestContainer.Opacity = 1;
        JumpToLatestContainer.IsHitTestVisible = true;
        if (releasePointer)
        {
            pointer?.Capture(null);
        }

        if (_isUserScrolling)
        {
            _lastUserScrollInputAt = Stopwatch.GetTimestamp();
            _scrollIdleTimer.Stop();
            _scrollIdleTimer.Start();
        }
    }

    private void OnMiddleScrollTimerTick(object? sender, EventArgs eventArgs) =>
        ApplyMiddleScrollDistance(_middleScrollCurrent.Y - _middleScrollAnchor.Y);

    private bool ApplyMiddleScrollDistance(double distance)
    {
        if (!_middleScrollActive && Math.Abs(distance) <= MiddleScrollDeadZone)
        {
            return false;
        }

        var magnitude = Math.Abs(distance) - MiddleScrollDeadZone;
        if (magnitude <= 0)
        {
            return false;
        }

        var step = Math.CopySign(
            Math.Min(34, 1 + (magnitude * 0.18) + (magnitude * magnitude * 0.003)),
            distance);
        if (_middleScrollTargetViewer is not null &&
            !ReferenceEquals(_middleScrollTargetViewer, _messageScrollViewer))
        {
            return ApplySplitMessageScrollStep(_middleScrollTargetViewer, step);
        }
        return ApplyMessageScrollStep(step);
    }

    private bool ApplySplitMessageScrollStep(ScrollViewer viewer, double step)
    {
        var maximumOffset = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        if (maximumOffset <= 0)
        {
            return false;
        }

        var targetOffset = Math.Clamp(viewer.Offset.Y + step, 0, maximumOffset);
        viewer.Offset = new Vector(viewer.Offset.X, targetOffset);
        var followsLatest = targetOffset >= maximumOffset - FollowLatestThreshold;
        if (ReferenceEquals(viewer, _twitchSplitScrollViewer))
        {
            _followTwitchSplit = followsLatest;
        }
        else if (ReferenceEquals(viewer, _youTubeSplitScrollViewer))
        {
            _followYouTubeSplit = followsLatest;
        }
        UpdateSplitLatestButtons();
        return true;
    }

    private void SplitMessagesList_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is not ListBox list)
        {
            return;
        }
        var point = eventArgs.GetCurrentPoint(list);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed)
        {
            if (_middleScrollActive &&
                point.Properties.PointerUpdateKind is PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.RightButtonPressed)
            {
                StopMiddleScroll();
            }
            return;
        }

        AttachSplitMessageScrollViewers();
        var viewer = ReferenceEquals(list, TwitchMessagesList)
            ? _twitchSplitScrollViewer
            : _youTubeSplitScrollViewer;
        if (_middleScrollActive)
        {
            StopMiddleScroll();
        }
        else
        {
            StartMiddleScroll(
                list,
                viewer,
                eventArgs.Pointer,
                eventArgs.GetPosition(ChatViewportCard));
        }
        eventArgs.Handled = true;
    }

    private void SplitMessagesList_OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_middleScrollActive ||
            !ReferenceEquals(sender, _middleScrollTargetList) ||
            !ReferenceEquals(eventArgs.Pointer, _middleScrollPointer))
        {
            return;
        }
        _middleScrollCurrent = eventArgs.GetPosition(ChatViewportCard);
        eventArgs.Handled = true;
    }

    private void SplitMessagesList_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs)
    {
        if (_middleScrollActive &&
            ReferenceEquals(sender, _middleScrollTargetList) &&
            ReferenceEquals(eventArgs.Pointer, _middleScrollPointer))
        {
            StopMiddleScroll(releasePointer: false);
        }
    }

    private bool ApplyMessageScrollStep(double step)
    {
        if (_messageScrollViewer is null)
        {
            return false;
        }

        var maximumOffset = Math.Max(
            0,
            _messageScrollViewer.Extent.Height - _messageScrollViewer.Viewport.Height);
        if (maximumOffset <= 0)
        {
            return false;
        }

        MarkUserScrollInput();
        var targetOffset = Math.Clamp(
            _messageScrollViewer.Offset.Y + step,
            0,
            maximumOffset);
        if (targetOffset < maximumOffset - 0.5)
        {
            SetFollowMode(false);
        }
        else
        {
            _followWhenUserScrollEnds = true;
        }

        _messageScrollViewer.Offset = new Vector(_messageScrollViewer.Offset.X, targetOffset);
        return true;
    }

    private void MessagesList_OnScrollGesture(object? sender, ScrollGestureEventArgs eventArgs) =>
        MarkUserScrollInput();

    private void MessagesList_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key is not (Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End))
        {
            return;
        }

        MarkUserScrollInput();
        if (eventArgs.Key is Key.Up or Key.PageUp or Key.Home)
        {
            SetFollowMode(false);
        }
    }

    internal void BeginMessageHistoryNavigation()
    {
        if (_compactModeTransitionInProgress ||
            DataContext is not MainWindowViewModel viewModel ||
            viewModel.IsMessageRenderingSuspended)
        {
            return;
        }

        MarkUserScrollInput();
        SetFollowMode(false);
    }

    private void OnMessagesChanged(object? sender, EventArgs eventArgs)
    {
        if (DataContext is MainWindowViewModel { ShowSplitMessageLists: true })
        {
            QueueSplitScrollToEnd();
            return;
        }
        if (DataContext is MainWindowViewModel
            {
                IsFollowingLatest: true,
                IsMessageRenderingSuspended: false
            })
        {
            QueueScrollToEnd();
        }
    }

    private void QueueSplitScrollToEnd()
    {
        if (_splitScrollUpdatePending)
        {
            return;
        }

        _splitScrollUpdatePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _splitScrollUpdatePending = false;
            AttachSplitMessageScrollViewers();
            _splitScrollProgrammatic = true;
            if (_followTwitchSplit && TwitchMessagesList.ItemCount > 0)
            {
                TwitchMessagesList.ScrollIntoView(TwitchMessagesList.ItemCount - 1);
                _twitchSplitScrollViewer?.ScrollToEnd();
            }
            if (_followYouTubeSplit && YouTubeMessagesList.ItemCount > 0)
            {
                YouTubeMessagesList.ScrollIntoView(YouTubeMessagesList.ItemCount - 1);
                _youTubeSplitScrollViewer?.ScrollToEnd();
            }
            Dispatcher.UIThread.Post(
                () =>
                {
                    _splitScrollProgrammatic = false;
                    UpdateSplitLatestButtons();
                },
                DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private void TwitchChatLatestButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _followTwitchSplit = true;
        UpdateSplitLatestButtons();
        QueueSplitScrollToEnd();
    }

    private void YouTubeChatLatestButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _followYouTubeSplit = true;
        UpdateSplitLatestButtons();
        QueueSplitScrollToEnd();
    }

    private void UpdateSplitLatestButtons()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            TwitchChatLatestButton.IsVisible = false;
            YouTubeChatLatestButton.IsVisible = false;
            return;
        }

        TwitchChatLatestButton.IsVisible = viewModel.ShowSplitMessageLists &&
                                           viewModel.HasTwitchVisibleMessages &&
                                           !_followTwitchSplit;
        YouTubeChatLatestButton.IsVisible = viewModel.ShowSplitMessageLists &&
                                            viewModel.HasYouTubeVisibleMessages &&
                                            !_followYouTubeSplit;
    }

    private void OnScrollToLatestRequested(object? sender, EventArgs eventArgs)
    {
        CancelFollowLatest();
        SetFollowMode(true);
        QueueScrollToEnd();
    }

    private static bool IsNearBottom(ScrollViewer viewer)
    {
        var remaining = Math.Max(
            0,
            viewer.Extent.Height - viewer.Viewport.Height - viewer.Offset.Y);
        return remaining <= FollowLatestThreshold;
    }

    private void QueueScrollToEnd()
    {
        if (_pendingScrollToBottom ||
            _messageScrollViewer is null ||
            _isUserScrolling ||
            _compactModeTransitionInProgress ||
            DataContext is not MainWindowViewModel
            {
                IsFollowingLatest: true,
                IsMessageRenderingSuspended: false
            })
        {
            return;
        }

        _pendingScrollToBottom = true;
        var scrollVersion = _scrollStateVersion;
        Dispatcher.UIThread.Post(
            () => ScrollToEndProgrammatically(scrollVersion),
            DispatcherPriority.Loaded);
    }

    private void ScrollToEndProgrammatically(long scrollVersion)
    {
        if (scrollVersion != _scrollStateVersion ||
            _messageScrollViewer is null ||
            _isUserScrolling ||
            _compactModeTransitionInProgress ||
            DataContext is not MainWindowViewModel
            {
                IsFollowingLatest: true,
                IsMessageRenderingSuspended: false
            })
        {
            _pendingScrollToBottom = false;
            return;
        }

        _isProgrammaticScroll = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (scrollVersion != _scrollStateVersion || _messageScrollViewer is null)
            {
                return;
            }

            _messageScrollViewer.ScrollToEnd();
            Dispatcher.UIThread.Post(
                () => CompleteScrollToEnd(scrollVersion),
                DispatcherPriority.Background);
        }, DispatcherPriority.Render);
    }

    private void CompleteScrollToEnd(long scrollVersion)
    {
        if (scrollVersion != _scrollStateVersion)
        {
            return;
        }

        _isProgrammaticScroll = false;
        _pendingScrollToBottom = false;
        if (_messageScrollViewer is null ||
            _isUserScrolling ||
            DataContext is not MainWindowViewModel { IsFollowingLatest: true } viewModel)
        {
            _scrollToBottomPasses = 0;
            return;
        }

        if (IsNearBottom(_messageScrollViewer) && IsLatestMessageFullyVisible(viewModel))
        {
            _scrollToBottomPasses = 0;
            return;
        }

        if (++_scrollToBottomPasses < ScrollToBottomPassLimit)
        {
            QueueScrollToEnd();
        }
        else
        {
            _scrollToBottomPasses = 0;
        }
    }

    private bool IsLatestMessageFullyVisible(MainWindowViewModel viewModel)
    {
        if (_messageScrollViewer is null || viewModel.VisibleMessages.Count == 0)
        {
            return true;
        }

        var latestMessage = viewModel.VisibleMessages[^1];
        var latestContainer = MessagesList
            .GetVisualDescendants()
            .OfType<ListBoxItem>()
            .FirstOrDefault(item => ReferenceEquals(item.DataContext, latestMessage));
        var origin = latestContainer?.TranslatePoint(default, _messageScrollViewer);
        if (latestContainer is null || origin is null)
        {
            return false;
        }

        return latestContainer.Bounds.Height >= _messageScrollViewer.Viewport.Height - 2 ||
               origin.Value.Y + latestContainer.Bounds.Height <=
               _messageScrollViewer.Viewport.Height + 2;
    }

    private void MarkUserScrollInput()
    {
        if (_compactModeTransitionInProgress ||
            DataContext is not MainWindowViewModel viewModel ||
            viewModel.IsMessageRenderingSuspended)
        {
            return;
        }

        CancelFollowLatest();
        _lastUserScrollInputAt = Stopwatch.GetTimestamp();
        if (!_isUserScrolling)
        {
            _isUserScrolling = true;
            viewModel.SetUserScrolling(true);
            AnimatedEmoteImage.SetFastScrolling(true);
        }

        _scrollIdleTimer.Stop();
        _scrollIdleTimer.Start();
    }

    private void OnScrollIdleTimerTick(object? sender, EventArgs eventArgs)
    {
        _scrollIdleTimer.Stop();
        if (Stopwatch.GetElapsedTime(_lastUserScrollInputAt).TotalMilliseconds < 230)
        {
            _scrollIdleTimer.Start();
            return;
        }
        if (_scrollBarPointerActive)
        {
            _lastUserScrollInputAt = Stopwatch.GetTimestamp();
            _scrollIdleTimer.Start();
            return;
        }

        _isUserScrolling = false;
        _scrollBarPointerActive = false;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetUserScrolling(false);
            if (_followWhenUserScrollEnds &&
                _messageScrollViewer is not null &&
                IsNearBottom(_messageScrollViewer))
            {
                _followWhenUserScrollEnds = false;
                SetFollowMode(true);
            }

            if (viewModel.IsFollowingLatest)
            {
                QueueScrollToEnd();
            }
        }

        AnimatedEmoteImage.SetFastScrolling(false);
    }

    private void SetFollowMode(bool enabled)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (!enabled)
        {
            _followWhenUserScrollEnds = false;
            _scrollToBottomPasses = 0;
        }

        viewModel.SetFollowingLatest(enabled);
    }

    private void PauseMessageViewportWork()
    {
        CancelFollowLatest();
        _scrollIdleTimer.Stop();
        _isUserScrolling = false;
        _scrollBarPointerActive = false;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetUserScrolling(false);
        }
        AnimatedEmoteImage.SetFastScrolling(true);
    }

    private void CancelFollowLatest()
    {
        _scrollStateVersion++;
        _pendingScrollToBottom = false;
        _isProgrammaticScroll = false;
        _scrollToBottomPasses = 0;
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(eventArgs);
        }
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private async void MinimizeButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_windowTransitionInProgress)
        {
            return;
        }

        _windowTransitionInProgress = true;
        try
        {
            var platformHandle = TryGetPlatformHandle();
            if (platformHandle is not null &&
                string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.Ordinal))
            {
                if (!WindowsWindowAnimation.TryMinimize(platformHandle.Handle))
                {
                    WindowState = WindowState.Minimized;
                }
                return;
            }

            await AnimateWindowMinimizeVisualAsync();
            WindowState = WindowState.Minimized;
            ResetWindowVisual();
        }
        finally
        {
            _windowTransitionInProgress = false;
        }
    }

    private async void MaximizeButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_windowTransitionInProgress || WindowState == WindowState.Minimized)
        {
            return;
        }

        _windowTransitionInProgress = true;
        try
        {
            if (WindowsWindowAnimation.TrySetMaximized(
                    TryGetPlatformHandle()?.Handle ?? IntPtr.Zero,
                    WindowState != WindowState.Maximized))
            {
                return;
            }

            await AnimateWindowVisualAsync(0.82, 0.992, 90, new CubicEaseIn());
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            await AnimateWindowVisualAsync(1, 1, 190, new CubicEaseOut());
        }
        finally
        {
            ResetWindowVisual();
            _windowTransitionInProgress = false;
        }
    }

    private async void CompactModeButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isObsDockLayout || _compactModeTransitionInProgress ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _compactModeTransitionInProgress = true;
        var button = sender as Button;
        if (button is not null)
        {
            button.IsEnabled = false;
        }

        try
        {
            if (viewModel.IsCompactMode)
            {
                await ExitCompactModeAsync(viewModel);
            }
            else
            {
                await EnterCompactModeAsync(viewModel);
            }
        }
        finally
        {
            ResetWindowVisual();
            _compactModeTransitionInProgress = false;
            if (viewModel.IsFollowingLatest)
            {
                QueueScrollToEnd();
            }
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }

#if DEBUG
    internal void EnterCompactModeForTesting()
    {
        CompactModeButton_OnClick(CompactModeButton, new RoutedEventArgs(Button.ClickEvent));
    }

    internal void BrowseMessageHistoryForTesting()
    {
        AttachMessageScrollViewer();
        if (_messageScrollViewer is null)
        {
            return;
        }

        BeginMessageHistoryNavigation();
        var maximumOffset = Math.Max(
            0,
            _messageScrollViewer.Extent.Height - _messageScrollViewer.Viewport.Height);
        _messageScrollViewer.Offset = new Vector(0, maximumOffset * 0.65);
    }
#endif

    internal void ScrollMessagesWithWheelForTesting(double deltaY)
    {
        AttachMessageScrollViewer();
        ApplyMessageWheelDelta(deltaY);
    }

    internal void ScrollMessagesWithMiddleButtonForTesting(double verticalDistance)
    {
        AttachMessageScrollViewer();
        ApplyMiddleScrollDistance(verticalDistance);
    }

    private async Task EnterCompactModeAsync(MainWindowViewModel viewModel)
    {
        _normalWidth = Math.Max(MinWidth, Bounds.Width);
        _normalHeight = Math.Max(MinHeight, Bounds.Height);
        _normalWindowState = WindowState;
        _normalHeaderExpanded = viewModel.IsHeaderExpanded;
        _normalComposerExpanded = viewModel.IsComposerExpanded;

        var sourcePosition = Position;
        var sourceWidth = Bounds.Width;
        var sourceHeight = Bounds.Height;
        var scale = Math.Max(0.1, RenderScaling);
        var compactTarget = GetCompactTargetMetrics(scale);
        var targetWidth = compactTarget.LogicalSize.Width;
        var targetHeight = compactTarget.LogicalSize.Height;
        var targetPixelSize = compactTarget.PixelSize;
        var sourcePixelWidth = (int)Math.Round(sourceWidth * scale);
        var sourcePixelHeight = (int)Math.Round(sourceHeight * scale);
        var nativeWindowHandle = GetWindowsWindowHandle();
        if (WindowsWindowAnimation.TryGetWindowSize(
                nativeWindowHandle,
                out var actualPixelWidth,
                out var actualPixelHeight))
        {
            sourcePixelWidth = actualPixelWidth;
            sourcePixelHeight = actualPixelHeight;
        }
        var targetPosition = new PixelPoint(
            sourcePosition.X + (int)Math.Round((sourcePixelWidth - targetPixelSize.Width) / 2d),
            sourcePosition.Y + (int)Math.Round((sourcePixelHeight - targetPixelSize.Height) / 2d));

        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }
        MinWidth = Math.Min(280, targetWidth);
        MinHeight = Math.Min(340, targetHeight);
        await AnimateCompactMorphAsync(
            viewModel,
            targetPosition,
            targetWidth,
            targetHeight,
            targetPixelSize,
            () =>
            {
                CloseTransientUiForCompactMode(viewModel);
                viewModel.IsCompactMode = true;
            },
            viewModel.ReduceMotion);
    }

    internal static (Size LogicalSize, PixelSize PixelSize) GetCompactTargetMetrics(double renderScaling) =>
        GetWindowTargetMetrics(new Size(360, 400), renderScaling);

    internal static (Size LogicalSize, PixelSize PixelSize) GetExpandedTargetMetrics(
        Size savedLogicalSize, double renderScaling) =>
        GetWindowTargetMetrics(
            new Size(Math.Max(860, savedLogicalSize.Width), Math.Max(560, savedLogicalSize.Height)),
            renderScaling);

    private static (Size LogicalSize, PixelSize PixelSize) GetWindowTargetMetrics(
        Size logicalSize, double renderScaling)
    {
        var scale = Math.Max(0.1, renderScaling);
        return (
            logicalSize,
            new PixelSize(
                Math.Max(1, (int)Math.Round(logicalSize.Width * scale)),
                Math.Max(1, (int)Math.Round(logicalSize.Height * scale))));
    }

    private void CloseTransientUiForCompactMode(MainWindowViewModel viewModel)
    {
        viewModel.IsSettingsOpen = false;
        viewModel.IsConnectPanelOpen = false;
        viewModel.IsChannelEditorOpen = false;
        viewModel.IsLogViewerOpen = false;
        viewModel.IsModerationPanelOpen = false;
        viewModel.IsDeleteLogConfirmationOpen = false;
        viewModel.IsModerationDialogOpen = false;
        viewModel.IsRecentMessagesOpen = false;
        viewModel.IsFiltersVisible = false;
        viewModel.IsAddingChannel = false;

        foreach (var comboBox in this.GetVisualDescendants().OfType<ComboBox>())
        {
            comboBox.IsDropDownOpen = false;
        }

        foreach (var menu in _contextMenuTransitionVersions.Keys.ToArray())
        {
            if (!menu.IsOpen)
            {
                continue;
            }

            _contextMenusCompletingClose.Add(menu);
            menu.Transitions = [];
            menu.Opacity = 0;
            menu.IsHitTestVisible = false;
            menu.Close();
        }

        _channelEditorTransitionVersion++;
        SetChannelEditorStateImmediate(false);
        foreach (var overlay in _panelTransitionVersions.Keys.ToArray())
        {
            _panelTransitionVersions[overlay] =
                _panelTransitionVersions.GetValueOrDefault(overlay) + 1;
        }
        SetAllOverlayStatesImmediate(viewModel);
    }

    private async Task ExitCompactModeAsync(MainWindowViewModel viewModel)
    {
        var target = GetExpandedTargetMetrics(new Size(_normalWidth, _normalHeight), RenderScaling);
        var targetWidth = target.LogicalSize.Width;
        var targetHeight = target.LogicalSize.Height;
        var targetPixelSize = target.PixelSize;
        var targetPosition = GetExpansionPositionOnCurrentScreen(targetPixelSize);
        await AnimateCompactMorphAsync(
            viewModel,
            targetPosition,
            targetWidth,
            targetHeight,
            targetPixelSize,
            () =>
            {
                viewModel.IsCompactMode = false;
                viewModel.IsHeaderExpanded = _normalHeaderExpanded;
                viewModel.IsComposerExpanded = _normalComposerExpanded;
            },
            viewModel.ReduceMotion);
        MinWidth = 860;
        MinHeight = 560;
        if (_normalWindowState == WindowState.Maximized)
        {
            await AnimateWindowStateChangeAsync(() => WindowState = WindowState.Maximized);
        }
    }

    private PixelPoint GetExpansionPositionOnCurrentScreen(PixelSize targetPixelSize)
    {
        var scale = Math.Max(0.1, RenderScaling);
        var currentPixelWidth = (int)Math.Round(Bounds.Width * scale);
        var currentPixelHeight = (int)Math.Round(Bounds.Height * scale);
        if (WindowsWindowAnimation.TryGetWindowSize(
                GetWindowsWindowHandle(),
                out var actualPixelWidth,
                out var actualPixelHeight))
        {
            currentPixelWidth = actualPixelWidth;
            currentPixelHeight = actualPixelHeight;
        }
        var compactCenter = new PixelPoint(
            Position.X + (int)Math.Round(currentPixelWidth / 2d),
            Position.Y + (int)Math.Round(currentPixelHeight / 2d));
        var screen = Screens.ScreenFromPoint(compactCenter) ?? Screens.ScreenFromWindow(this);
        if (screen is null)
        {
            return Position;
        }

        var workingArea = screen.WorkingArea;
        var maximumX = Math.Max(workingArea.X, workingArea.Right - targetPixelSize.Width);
        var maximumY = Math.Max(workingArea.Y, workingArea.Bottom - targetPixelSize.Height);
        var centeredX = compactCenter.X - (int)Math.Round(targetPixelSize.Width / 2d);
        var centeredY = compactCenter.Y - (int)Math.Round(targetPixelSize.Height / 2d);
        return new PixelPoint(
            Math.Clamp(centeredX, workingArea.X, maximumX),
            Math.Clamp(centeredY, workingArea.Y, maximumY));
    }

    private async Task AnimateCompactMorphAsync(
        MainWindowViewModel viewModel,
        PixelPoint targetPosition,
        double targetWidth,
        double targetHeight,
        PixelSize targetPixelSize,
        Action applyTargetLayout,
        bool reduceMotion)
    {
        if (reduceMotion)
        {
            applyTargetLayout();
            await AnimateWindowBoundsAsync(
                targetPosition,
                targetWidth,
                targetHeight,
                targetPixelSize,
                0,
                true);
            return;
        }

        viewModel.SuspendMessageBatchProcessing();
        PauseMessageViewportWork();
        try
        {
            await AnimateCompactContentOpacityAsync(0, 150);
            viewModel.IsMessageRenderingSuspended = true;
            applyTargetLayout();
            await AnimateWindowBoundsAsync(
                targetPosition,
                targetWidth,
                targetHeight,
                targetPixelSize,
                520,
                false);

            await RestoreMessageViewportAsync(viewModel);
            await AnimateCompactContentOpacityAsync(1, 240);
        }
        finally
        {
            viewModel.IsMessageRenderingSuspended = false;
            viewModel.ResumeMessageBatchProcessing();
            AnimatedEmoteImage.SetFastScrolling(false);
            ChatLayoutRoot.Transitions = [];
            ChatLayoutRoot.Opacity = 1;
            if (viewModel.IsFollowingLatest)
            {
                QueueScrollToEnd();
            }
        }
    }

    private async Task RestoreMessageViewportAsync(MainWindowViewModel viewModel)
    {
        viewModel.IsMessageRenderingSuspended = false;
        ChatLayoutRoot.InvalidateMeasure();
        MessagesList.InvalidateMeasure();
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Task.Delay(16);
        if (!viewModel.IsFollowingLatest)
        {
            AlignFirstVisibleMessage();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        }
    }

    private void AlignFirstVisibleMessage()
    {
        if (_messageScrollViewer is null)
        {
            return;
        }

        var firstPartiallyVisibleItem = MessagesList
            .GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Select(item => (Item: item, Origin: item.TranslatePoint(default, _messageScrollViewer)))
            .Where(entry => entry.Origin is { } origin &&
                            origin.Y < -0.5 &&
                            origin.Y + entry.Item.Bounds.Height > 0)
            .OrderBy(entry => entry.Origin!.Value.Y)
            .FirstOrDefault();
        if (firstPartiallyVisibleItem.Item?.DataContext is { } message)
        {
            MessagesList.ScrollIntoView(message);
        }
    }

    private async Task AnimateCompactContentOpacityAsync(
        double targetOpacity,
        int durationMilliseconds)
    {
        ChatLayoutRoot.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
                Easing = new SineEaseInOut()
            }
        ];
        ChatLayoutRoot.Opacity = targetOpacity;
        await Task.Delay(durationMilliseconds + 16);
    }

    private async void HeaderToggleButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel || viewModel.IsCompactMode)
        {
            return;
        }

        var button = sender as Button;
        if (button is not null)
        {
            button.IsEnabled = false;
        }
        try
        {
            var show = !viewModel.IsHeaderExpanded;
            var version = ++_headerPanelTransitionVersion;
            await AnimateCollapsiblePanelAsync(
                HeaderPanel,
                show,
                value => viewModel.IsHeaderExpanded = value,
                version,
                () => _headerPanelTransitionVersion,
                viewModel.ReduceMotion);
        }
        finally
        {
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }

    private async void ComposerToggleButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel || viewModel.IsCompactMode)
        {
            return;
        }

        var button = sender as Button;
        if (button is not null)
        {
            button.IsEnabled = false;
        }
        try
        {
            var show = !viewModel.IsComposerExpanded;
            var version = ++_composerPanelTransitionVersion;
            await AnimateCollapsiblePanelAsync(
                ComposerPanel,
                show,
                value => viewModel.IsComposerExpanded = value,
                version,
                () => _composerPanelTransitionVersion,
                viewModel.ReduceMotion);
        }
        finally
        {
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }

    private static async Task AnimateCollapsiblePanelAsync(
        Control panel,
        bool show,
        Action<bool> setState,
        int version,
        Func<int> currentVersion,
        bool reduceMotion)
    {
        panel.Transitions = [];
        panel.ClipToBounds = true;
        if (reduceMotion)
        {
            setState(show);
            panel.Height = double.NaN;
            panel.Opacity = 1;
            return;
        }

        double targetHeight;
        if (show)
        {
            setState(true);
            panel.Height = double.NaN;
            panel.Measure(new Size(
                Math.Max(1, panel.Bounds.Width),
                double.PositiveInfinity));
            targetHeight = Math.Max(1, Math.Max(panel.Bounds.Height, panel.DesiredSize.Height));
            panel.Height = 0;
            panel.Opacity = 0;
        }
        else
        {
            targetHeight = Math.Max(1, panel.Bounds.Height);
            panel.Height = targetHeight;
            panel.Opacity = 1;
        }

        await Task.Delay(16);
        if (version != currentVersion())
        {
            return;
        }

        panel.Transitions =
        [
            new DoubleTransition
            {
                Property = Layoutable.HeightProperty,
                Duration = TimeSpan.FromMilliseconds(320),
                Easing = new SineEaseInOut()
            },
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(280),
                Easing = new SineEaseInOut()
            }
        ];
        panel.Height = show ? targetHeight : 0;
        panel.Opacity = show ? 1 : 0;
        await Task.Delay(340);
        if (version != currentVersion())
        {
            return;
        }

        panel.Transitions = [];
        if (!show)
        {
            setState(false);
        }
        panel.Height = double.NaN;
        panel.Opacity = 1;
    }

    private async Task AnimateWindowStateChangeAsync(Action changeState)
    {
        if (DataContext is MainWindowViewModel { ReduceMotion: true })
        {
            changeState();
            return;
        }

        await AnimateWindowVisualAsync(0.82, 0.992, 90, new CubicEaseIn());
        changeState();
        await AnimateWindowVisualAsync(1, 1, 190, new CubicEaseOut());
    }

    private async Task AnimateWindowVisualAsync(
        double opacity,
        double scale,
        int durationMilliseconds,
        Easing easing)
    {
        if (DataContext is MainWindowViewModel { ReduceMotion: true })
        {
            WindowVisualRoot.Opacity = opacity;
            WindowVisualRoot.RenderTransform =
                TransformOperations.Parse($"scale({scale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
            return;
        }

        WindowVisualRoot.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
                Easing = easing
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
                Easing = easing
            }
        ];
        WindowVisualRoot.Opacity = opacity;
        WindowVisualRoot.RenderTransform =
            TransformOperations.Parse($"scale({scale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        await Task.Delay(durationMilliseconds + 16);
    }

    public void PrepareWindowOpenAnimation(double offsetY = 20)
    {
        _windowOpenAnimationVersion++;
        Transitions = [];
        WindowVisualRoot.Transitions = [];
        Opacity = 0;
        WindowVisualRoot.Opacity = 0;
        WindowVisualRoot.RenderTransform = DataContext is MainWindowViewModel { ReduceMotion: true }
            ? TransformOperations.Identity
            : TransformOperations.Parse(
                $"translate(0px, {offsetY.ToString(System.Globalization.CultureInfo.InvariantCulture)}px)");
    }

    public async Task PlayWindowOpenAnimationAsync(int durationMilliseconds = 280)
    {
        var version = _windowOpenAnimationVersion;
        var reduceMotion = DataContext is MainWindowViewModel { ReduceMotion: true };
        var duration = reduceMotion ? 70 : durationMilliseconds;
        await Task.Delay(16);
        if (version != _windowOpenAnimationVersion)
        {
            return;
        }

        Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(duration),
                Easing = new CubicEaseOut()
            }
        ];
        WindowVisualRoot.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(duration),
                Easing = new CubicEaseOut()
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(duration),
                Easing = new CubicEaseOut()
            }
        ];
        Opacity = 1;
        WindowVisualRoot.Opacity = 1;
        WindowVisualRoot.RenderTransform = TransformOperations.Identity;
        await Task.Delay(duration + 16);
        if (version == _windowOpenAnimationVersion)
        {
            ResetWindowVisual();
        }
    }

    internal async Task AnimateWindowCloseVisualAsync(
        double offsetY = 6,
        int durationMilliseconds = 220)
    {
        _windowOpenAnimationVersion++;
        var reduceMotion = DataContext is MainWindowViewModel { ReduceMotion: true };
        var duration = reduceMotion ? 70 : durationMilliseconds;
        Transitions = [];
        Opacity = 1;
        WindowVisualRoot.Transitions =
        [
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(duration),
                Easing = new SineEaseInOut()
            }
        ];
        WindowVisualRoot.Opacity = 1;
        var targetTransform = reduceMotion
            ? TransformOperations.Identity
            : TransformOperations.Parse(
                $"translate(0px, {offsetY.ToString(System.Globalization.CultureInfo.InvariantCulture)}px) scale(0.992)");
        WindowVisualRoot.RenderTransform = targetTransform;
        await Task.Delay(duration + 16);
        Transitions = [];
        WindowVisualRoot.Transitions = [];
        Opacity = 1;
        WindowVisualRoot.Opacity = 1;
        WindowVisualRoot.RenderTransform = targetTransform;
    }

    internal async Task AnimateWindowMinimizeVisualAsync(int durationMilliseconds = 300)
    {
        _windowOpenAnimationVersion++;
        var reduceMotion = DataContext is MainWindowViewModel { ReduceMotion: true };
        var duration = reduceMotion ? 70 : durationMilliseconds;
        var easing = new SineEaseInOut();
        // Keep both the native window and its content opaque. Fading the visual
        // root exposes the dark window background before the window reaches the
        // taskbar or tray and looks like an unintended blackout.
        Transitions = [];
        Opacity = 1;
        WindowVisualRoot.Transitions =
        [
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(duration),
                Easing = easing
            }
        ];

        var targetTransform = reduceMotion
            ? TransformOperations.Identity
            : TransformOperations.Parse("translate(0px, 18px) scale(0.985)");
        WindowVisualRoot.Opacity = 1;
        WindowVisualRoot.RenderTransform = targetTransform;
        await Task.Delay(duration + 16);

        Transitions = [];
        WindowVisualRoot.Transitions = [];
        Opacity = 1;
        WindowVisualRoot.Opacity = 1;
        WindowVisualRoot.RenderTransform = targetTransform;
    }

    private async Task AnimateWindowBoundsAsync(
        PixelPoint targetPosition,
        double targetWidth,
        double targetHeight,
        PixelSize targetPixelSize,
        int durationMilliseconds,
        bool reduceMotion)
    {
        if (reduceMotion)
        {
            if (!WindowsWindowAnimation.TrySetBounds(
                    GetWindowsWindowHandle(),
                    targetPosition.X,
                    targetPosition.Y,
                    targetPixelSize.Width,
                    targetPixelSize.Height))
            {
                Position = targetPosition;
                Width = targetWidth;
                Height = targetHeight;
            }
            return;
        }

        var sourcePosition = Position;
        var sourceWidth = Bounds.Width;
        var sourceHeight = Bounds.Height;
        var scale = Math.Max(0.1, RenderScaling);
        var nativeWindowHandle = GetWindowsWindowHandle();
        var sourcePixelWidth = (int)Math.Round(sourceWidth * scale);
        var sourcePixelHeight = (int)Math.Round(sourceHeight * scale);
        var useNativeBounds = WindowsWindowAnimation.TryGetWindowSize(
            nativeWindowHandle,
            out sourcePixelWidth,
            out sourcePixelHeight);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(
                stopwatch.Elapsed.TotalMilliseconds / Math.Max(1, durationMilliseconds),
                0,
                1);
            var eased = progress * progress * progress *
                        ((progress * ((progress * 6) - 15)) + 10);
            var framePosition = new PixelPoint(
                (int)Math.Round(Lerp(sourcePosition.X, targetPosition.X, eased)),
                (int)Math.Round(Lerp(sourcePosition.Y, targetPosition.Y, eased)));
            var frameWidth = Lerp(sourceWidth, targetWidth, eased);
            var frameHeight = Lerp(sourceHeight, targetHeight, eased);
            if (!useNativeBounds ||
                !WindowsWindowAnimation.TrySetBounds(
                    nativeWindowHandle,
                    framePosition.X,
                    framePosition.Y,
                    Math.Max(
                        1,
                        (int)Math.Round(Lerp(
                            sourcePixelWidth,
                            targetPixelSize.Width,
                            eased))),
                    Math.Max(
                        1,
                        (int)Math.Round(Lerp(
                            sourcePixelHeight,
                            targetPixelSize.Height,
                            eased)))))
            {
                Position = framePosition;
                Width = frameWidth;
                Height = frameHeight;
            }
            if (progress >= 1)
            {
                timer.Stop();
                completion.TrySetResult();
            }
        };
        timer.Start();
        await completion.Task;
        if (useNativeBounds)
        {
            _ = WindowsWindowAnimation.TrySetBounds(
                nativeWindowHandle,
                targetPosition.X,
                targetPosition.Y,
                targetPixelSize.Width,
                targetPixelSize.Height);
        }
        else
        {
            Position = targetPosition;
            Width = targetWidth;
            Height = targetHeight;
        }
    }

    private IntPtr GetWindowsWindowHandle()
    {
        var platformHandle = TryGetPlatformHandle();
        return platformHandle is not null &&
               string.Equals(
                   platformHandle.HandleDescriptor,
                   "HWND",
                   StringComparison.Ordinal)
            ? platformHandle.Handle
            : IntPtr.Zero;
    }

    private void ResetWindowVisual()
    {
        Transitions = [];
        Opacity = 1;
        WindowVisualRoot.Transitions = [];
        WindowVisualRoot.Opacity = 1;
        WindowVisualRoot.RenderTransform = TransformOperations.Identity;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.Property == ActualThemeVariantProperty)
        {
            if (DataContext is MainWindowViewModel themeViewModel)
            {
                themeViewModel.UpdateActualTheme(ActualThemeVariant == ThemeVariant.Light);
            }
            return;
        }

        if (eventArgs.Property != WindowStateProperty)
        {
            return;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.IsWindowMaximized = WindowState == WindowState.Maximized;
        }

        if (WindowState == WindowState.Minimized)
        {
            _wasMinimized = true;
            return;
        }

        if (!_wasMinimized || _windowTransitionInProgress || _closeAnimationInProgress)
        {
            return;
        }

        _wasMinimized = false;
        var platformHandle = TryGetPlatformHandle();
        if (platformHandle is not null &&
            string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.Ordinal))
        {
            ResetWindowVisual();
            EnableWindowsSystemWindowAnimations();
            DispatcherTimer.RunOnce(
                EnableWindowsSystemWindowAnimations,
                TimeSpan.FromMilliseconds(120));
            return;
        }

        PrepareWindowOpenAnimation(offsetY: 24);
        _ = PlayWindowOpenAnimationAsync(durationMilliseconds: 280);
    }

    private static double Lerp(double from, double to, double progress) =>
        from + ((to - from) * progress);

    private void HeaderMoreTool_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        // Avalonia invokes Button.Command after raising Click. Closing the popup
        // synchronously here detaches the button first and can discard its binding,
        // leaving a row that looks clickable but performs no action.
        Dispatcher.UIThread.Post(
            () => HeaderMoreToolsButton.Flyout?.Hide(),
            DispatcherPriority.Background);
    }

    private void OnHeaderMoreToolsFlyoutOpened(object? sender, EventArgs eventArgs)
    {
        var version = ++_headerMoreToolsAnimationVersion;
        HeaderMoreToolsCard.Transitions = [];

        if (DataContext is MainWindowViewModel { ReduceMotion: true })
        {
            HeaderMoreToolsCard.Opacity = 1;
            HeaderMoreToolsCard.RenderTransform =
                TransformOperations.Parse("translate(0px, 0px) scale(1)");
            return;
        }

        HeaderMoreToolsCard.Opacity = 0;
        HeaderMoreToolsCard.RenderTransform =
            TransformOperations.Parse("translate(8px, -8px) scale(0.96)");

        Dispatcher.UIThread.Post(() =>
        {
            if (version != _headerMoreToolsAnimationVersion)
            {
                return;
            }

            var duration = TimeSpan.FromMilliseconds(160);
            HeaderMoreToolsCard.Transitions =
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = duration,
                    Easing = new CubicEaseOut()
                },
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = duration,
                    Easing = new CubicEaseOut()
                }
            ];
            HeaderMoreToolsCard.Opacity = 1;
            HeaderMoreToolsCard.RenderTransform =
                TransformOperations.Parse("translate(0px, 0px) scale(1)");
        }, DispatcherPriority.Render);
    }

    private void OnHeaderMoreToolsFlyoutClosed(object? sender, EventArgs eventArgs)
    {
        _headerMoreToolsAnimationVersion++;
        HeaderMoreToolsCard.Transitions = [];
        HeaderMoreToolsCard.Opacity = 1;
        HeaderMoreToolsCard.RenderTransform =
            TransformOperations.Parse("translate(0px, 0px) scale(1)");
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (sender is not MainWindowViewModel viewModel)
        {
            return;
        }
        HandleModalFocusPropertyChanged(viewModel, eventArgs.PropertyName);
        UpdateDockToolbarInteractivity();
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.SelectedSettingsSection))
            SettingsContentScrollViewer.Offset = default;
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen) && viewModel.IsSettingsOpen)
        {
            UpdateSettingsCardSize();
        }
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.ReduceMotion))
        {
            AnimatedEmoteImage.SetReduceMotion(viewModel.ReduceMotion);
        }
        if (eventArgs.PropertyName is nameof(MainWindowViewModel.IsSplitChatView) or
            nameof(MainWindowViewModel.IsCompactMode))
        {
            Dispatcher.UIThread.Post(() =>
            {
                AttachMessageScrollViewer();
                AttachSplitMessageScrollViewers();
                if (viewModel.ShowSplitMessageLists)
                {
                    _followTwitchSplit = true;
                    _followYouTubeSplit = true;
                    UpdateSplitLatestButtons();
                    QueueSplitScrollToEnd();
                }
                else
                {
                    QueueScrollToEnd();
                }
            }, DispatcherPriority.Loaded);
        }
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.OnboardingStep))
        {
            if (viewModel.IsMainWindowTutorial)
            {
                AnimateOnboardingStep(viewModel.OnboardingStep, viewModel.ReduceMotion);
            }
            return;
        }
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.ActiveTutorialTopic))
        {
            _onboardingFocusInitialized = false;
            _lastOnboardingTargetRect = null;
            if (viewModel.IsMainWindowTutorial)
            {
                PositionOnboardingElements(viewModel.OnboardingStep);
            }
            if (viewModel.IsOnboardingOpen)
            {
                AnimatePanel(
                    OnboardingOverlay,
                    OnboardingCard,
                    viewModel.IsMainWindowTutorial,
                    viewModel.ReduceMotion);
            }
            return;
        }
        if (eventArgs.PropertyName is nameof(MainWindowViewModel.SelectedSettingsSection) or
            nameof(MainWindowViewModel.ModerationPanelSection))
        {
            if (viewModel is { IsOnboardingOpen: true, IsMainWindowTutorial: true, IsContextTutorial: true })
            {
                ScheduleContextTutorialLayoutRefresh();
            }
            return;
        }
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsChannelEditorOpen))
        {
            AnimateChannelEditor(viewModel.IsChannelEditorOpen, viewModel.ReduceMotion);
            return;
        }

        Border? overlay = null;
        Control? card = null;
        var open = false;
        switch (eventArgs.PropertyName)
        {
            case nameof(MainWindowViewModel.IsConnectPanelOpen):
                overlay = ConnectPanelOverlay;
                card = ConnectPanelCard;
                open = viewModel.IsConnectPanelOpen;
                break;
            case nameof(MainWindowViewModel.IsLogViewerOpen):
                overlay = LogViewerOverlay;
                card = LogViewerCard;
                open = viewModel.IsLogViewerOpen;
                break;
            case nameof(MainWindowViewModel.IsModerationPanelOpen):
                overlay = ModerationPanelOverlay;
                card = ModerationPanelCard;
                open = viewModel.IsModerationPanelOpen;
                break;
            case nameof(MainWindowViewModel.IsDeleteLogConfirmationOpen):
                overlay = DeleteLogOverlay;
                card = DeleteLogCard;
                open = viewModel.IsDeleteLogConfirmationOpen;
                break;
            case nameof(MainWindowViewModel.IsModerationDialogOpen):
                overlay = ModerationDialogOverlay;
                card = ModerationDialogCard;
                open = viewModel.IsModerationDialogOpen;
                break;
            case nameof(MainWindowViewModel.IsSettingsOpen):
                overlay = SettingsOverlay;
                card = SettingsCard;
                open = viewModel.IsSettingsOpen;
                break;
            case nameof(MainWindowViewModel.IsOnboardingOpen):
                _onboardingFocusInitialized = false;
                _lastOnboardingTargetRect = null;
                _onboardingFocusUpdatePending = false;
                PositionOnboardingElements(viewModel.OnboardingStep);
                overlay = OnboardingOverlay;
                card = OnboardingCard;
                open = viewModel.IsOnboardingOpen && viewModel.IsMainWindowTutorial;
                if (open)
                {
                    if (viewModel.IsContextTutorial)
                    {
                        ScheduleContextTutorialLayoutRefresh();
                    }
                    Dispatcher.UIThread.Post(
                        () => OnboardingNextButton.Focus(),
                        DispatcherPriority.Input);
                }
                else if (viewModel.IsContextTutorial)
                {
                    var topic = viewModel.ActiveTutorialTopic;
                    Dispatcher.UIThread.Post(
                        () => GetContextTutorialHelpButton(topic)?.Focus(),
                        DispatcherPriority.Input);
                }
                break;
        }
        if (overlay is not null && card is not null)
        {
            AnimatePanel(overlay, card, open, viewModel.ReduceMotion);
        }
    }

    private void ScheduleContextTutorialLayoutRefresh()
    {
        _contextTutorialLayoutTimer.Stop();
        _contextTutorialLayoutTimer.Start();
    }

    private void OnContextTutorialLayoutTimerTick(object? sender, EventArgs eventArgs)
    {
        _contextTutorialLayoutTimer.Stop();
        if (DataContext is not MainWindowViewModel
            {
                IsOnboardingOpen: true,
                IsContextTutorial: true,
                IsMainWindowTutorial: true
            } viewModel)
        {
            return;
        }

        // A step transition already measures the contextual target after its
        // content has settled. Do not interrupt that liquid focus animation
        // with the secondary layout refresh requested by section changes.
        if (_onboardingFocusUpdatePending)
        {
            return;
        }

        PositionOnboardingElements(viewModel.OnboardingStep);
    }

    private void AnimateChannelEditor(bool open, bool reduceMotion)
    {
        var version = ++_channelEditorTransitionVersion;
        if (DataContext is MainWindowViewModel
            {
                IsOnboardingOpen: true,
                OnboardingStep: 2
            })
        {
            SetChannelEditorStateImmediate(open);
            return;
        }
        if (reduceMotion)
        {
            SetChannelEditorStateImmediate(open);
            return;
        }

        var duration = open ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromMilliseconds(240);
        ChannelEditorCard.Transitions = [];
        ChannelEditorCard.RenderTransformOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative);
        if (open)
        {
            PositionChannelEditorCard();
            ChannelEditorCard.IsVisible = true;
            ChannelEditorCard.IsHitTestVisible = true;
            ChannelEditorCard.Opacity = 0;
            ChannelEditorCard.RenderTransform =
                TransformOperations.Parse("translate(0px, 12px) scale(0.992)");
            DispatcherTimer.RunOnce(() =>
            {
                if (version != _channelEditorTransitionVersion ||
                    DataContext is not MainWindowViewModel { IsChannelEditorOpen: true })
                {
                    return;
                }

                ChannelEditorCard.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(240),
                        Easing = new CubicEaseOut()
                    },
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = duration,
                        Easing = new CubicEaseOut()
                    }
                ];
                ChannelEditorCard.Opacity = 1;
                ChannelEditorCard.RenderTransform = TransformOperations.Identity;
                DispatcherTimer.RunOnce(() =>
                {
                    if (version == _channelEditorTransitionVersion &&
                        DataContext is MainWindowViewModel { IsChannelEditorOpen: true })
                    {
                        ChannelEditorCard.Transitions = [];
                        ChannelEditorCard.Opacity = 1;
                        ChannelEditorCard.RenderTransform = TransformOperations.Identity;
                    }
                }, duration + TimeSpan.FromMilliseconds(24));
            }, TimeSpan.FromMilliseconds(16));
            return;
        }

        if (!ChannelEditorCard.IsVisible)
        {
            SetChannelEditorStateImmediate(false);
            return;
        }

        ChannelEditorCard.IsHitTestVisible = false;
        ChannelEditorCard.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = duration,
                Easing = new SineEaseInOut()
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = duration,
                Easing = new SineEaseInOut()
            }
        ];
        ChannelEditorCard.Opacity = 0;
        ChannelEditorCard.RenderTransform =
            TransformOperations.Parse("translate(0px, 4px) scale(0.995)");
        DispatcherTimer.RunOnce(() =>
        {
            if (version == _channelEditorTransitionVersion &&
                DataContext is MainWindowViewModel { IsChannelEditorOpen: false })
            {
                SetChannelEditorStateImmediate(false);
            }
        }, duration + TimeSpan.FromMilliseconds(20));
    }

    private void SetChannelEditorStateImmediate(bool open)
    {
        ChannelEditorCard.Transitions = [];
        ChannelEditorCard.IsVisible = open;
        ChannelEditorCard.IsHitTestVisible = open;
        ChannelEditorCard.Opacity = open ? 1 : 0;
        ChannelEditorCard.RenderTransform = open
            ? TransformOperations.Identity
            : TransformOperations.Parse("translate(0px, 12px) scale(0.992)");
        if (open)
        {
            PositionChannelEditorCard();
        }
    }

    private void PositionChannelEditorCard()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var layoutOrigin = ChatLayoutRoot.TranslatePoint(new Point(), this);
        var caption = HeaderPanel.IsVisible ? (Control)HeaderPanel :
            ObsDockToolbar.IsVisible ? ObsDockToolbar : TitleBarPanel;
        var headerBottom = caption.TranslatePoint(new Point(0, caption.Bounds.Height), this);
        var left = Math.Max(8, Math.Ceiling(layoutOrigin?.X ?? 16));
        var top = Math.Max(8, Math.Ceiling((headerBottom?.Y ?? 42) + 8));
        ChannelEditorCard.Width = Math.Min(330, Math.Max(1, Bounds.Width - left - 8));
        ChannelEditorCard.MaxHeight = Math.Max(1, Bounds.Height - top - 8);
        ChannelEditorCard.Margin = new Thickness(left, top, 0, 0);
    }

    private void PositionOnboardingElements(
        int step,
        bool animateFocus = false,
        bool positionCard = true,
        bool updateFocus = true)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var edge = Bounds.Width < 520 ? 8d : 24d;
        var cardWidth = Math.Min(440, Math.Max(1, Bounds.Width - edge * 2));
        var maximumCardHeight = Math.Max(1, Bounds.Height - 54 - edge);
        OnboardingCard.MaxHeight = maximumCardHeight;
        var cardHeight = Math.Min(maximumCardHeight, OnboardingCard.Bounds.Height > 120
            ? OnboardingCard.Bounds.Height
            : 370);
        var centeredLeft = Math.Max(edge, (Bounds.Width - cardWidth) / 2);
        var centeredTop = Math.Max(54, (Bounds.Height - cardHeight) / 2);
        var cardLeft = centeredLeft;
        var cardTop = centeredTop;

        // Keep every tutorial action available in narrow windows without
        // squeezing the labels into a single footer row.
        var narrowActions = cardWidth < 400;
        OnboardingScrollableBody.Margin = narrowActions ? new Thickness(16, 12, 16, 0) : new Thickness(26, 22, 26, 0);
        OnboardingScrollableBody.Spacing = narrowActions ? 10 : 17;
        OnboardingFixedFooter.Margin = narrowActions ? new Thickness(16, 8, 16, 10) : new Thickness(26, 17, 26, 24);
        OnboardingFixedFooter.Spacing = narrowActions ? 8 : 17;
        OnboardingTitleText.FontSize = narrowActions ? 20 : 25;
        OnboardingTitleText.LineHeight = narrowActions ? 24 : 30;
        var shortTutorial = narrowActions && maximumCardHeight < 320;
        OnboardingGuideHeader.IsVisible = !shortTutorial;
        OnboardingShortProgress.IsVisible = shortTutorial;
        OnboardingActionsPanel.ColumnDefinitions = new ColumnDefinitions(
            narrowActions ? "*,10,*" : "Auto,*,Auto,10,Auto");
        OnboardingActionsPanel.RowDefinitions = new RowDefinitions(
            narrowActions ? "Auto,8,Auto" : "Auto");
        Grid.SetColumn(OnboardingBackButton, narrowActions ? 0 : 2);
        Grid.SetColumn(OnboardingNextButton, narrowActions ? 2 : 4);
        Grid.SetRow(OnboardingSkipButton, narrowActions ? 2 : 0);
        Grid.SetColumnSpan(OnboardingSkipButton, narrowActions ? 3 : 1);
        OnboardingNextButton.MinWidth = narrowActions ? 0 : 132;

        if (DataContext is MainWindowViewModel { IsContextTutorial: true } contextViewModel)
        {
            var contextTarget = GetContextTutorialTargetRect(
                contextViewModel.ActiveTutorialTopic,
                step);
            (cardLeft, cardTop) = PlaceContextTutorialCard(
                contextTarget,
                cardWidth,
                cardHeight,
                centeredLeft,
                centeredTop);
            if (positionCard)
            {
                OnboardingCard.Width = cardWidth;
                OnboardingCard.HorizontalAlignment = HorizontalAlignment.Left;
                OnboardingCard.VerticalAlignment = VerticalAlignment.Top;
                OnboardingCard.Margin = new Thickness(cardLeft, cardTop, 0, 0);
            }
            if (updateFocus)
            {
                ApplyOnboardingFocus(contextTarget, animateFocus);
                OnboardingTargetHighlight.CornerRadius = new CornerRadius(16);
            }
            return;
        }

        switch (step)
        {
            case 1:
                cardTop = Math.Max(54, Bounds.Height - cardHeight - 34);
                break;
            case 2:
                cardLeft = Math.Max(24, Bounds.Width - cardWidth - 28);
                cardTop = Math.Max(64, (Bounds.Height - cardHeight) / 2);
                break;
            case 3:
                cardLeft = 28;
                cardTop = Math.Max(64, (Bounds.Height - cardHeight) / 2);
                break;
            case 4:
                cardTop = 72;
                break;
            case 5:
                cardLeft = 28;
                cardTop = Math.Max(64, (Bounds.Height - cardHeight) / 2);
                break;
            case 6:
                cardLeft = Math.Max(24, Bounds.Width - cardWidth - 24);
                cardTop = Math.Max(64, (Bounds.Height - cardHeight) / 2);
                break;
            case 7:
                cardLeft = 28;
                cardTop = Math.Max(64, (Bounds.Height - cardHeight) / 2);
                break;
            case 8:
                cardLeft = Math.Max(24, Bounds.Width - cardWidth - 24);
                cardTop = Math.Max(64, (Bounds.Height - cardHeight) / 2);
                break;
            case 9:
                cardTop = Math.Max(64, Math.Min(108, Bounds.Height - cardHeight - 24));
                break;
        }

        cardLeft = Math.Clamp(cardLeft, edge, Math.Max(edge, Bounds.Width - cardWidth - edge));
        cardTop = Math.Clamp(cardTop, 54, Math.Max(54, Bounds.Height - cardHeight - edge));

        if (positionCard)
        {
            OnboardingCard.Width = cardWidth;
            OnboardingCard.HorizontalAlignment = HorizontalAlignment.Left;
            OnboardingCard.VerticalAlignment = VerticalAlignment.Top;
            OnboardingCard.Margin = new Thickness(cardLeft, cardTop, 0, 0);
        }

        if (updateFocus)
        {
            var targetRect = GetOnboardingTargetRect(step);
            ApplyOnboardingFocus(targetRect, animateFocus);
            OnboardingTargetHighlight.CornerRadius = new CornerRadius(20);
        }
    }

    private Rect? GetOnboardingTargetRect(int step)
    {
        Control? target = step switch
        {
            1 => HeaderPanel,
            2 => ChannelEditorCard,
            3 => ChatViewportCard,
            4 => OnboardingComposerPreview,
            5 => HeaderToolsPanel,
            6 => LogViewerSidebar,
            7 => CopyOverlayUrlButton,
            10 => ObsPluginStatusText,
            11 => ObsPluginHelpButton,
            8 => SettingsNavigationCard,
            9 => CompactModeButton,
            _ => null
        };
        if (target is null || !target.IsVisible || target.Bounds.Width < 1 || target.Bounds.Height < 1)
        {
            return step switch
            {
                2 => new Rect(12, 140, Math.Min(336, Bounds.Width * 0.39), Bounds.Height - 244),
                4 => new Rect(12, Bounds.Height - 94, Bounds.Width - 24, 82),
                _ => null
            };
        }

        var origin = target.TranslatePoint(new Point(), this);
        if (origin is null)
        {
            return null;
        }

        var padding = step switch
        {
            4 => 0d,
            5 or 9 => 7d,
            _ => 5d
        };
        var targetRect = new Rect(
            origin.Value.X - padding,
            origin.Value.Y - padding,
            target.Bounds.Width + padding * 2,
            target.Bounds.Height + padding * 2);
        if (step is 7 or 10 or 11 &&
            SettingsContentScrollViewer.TranslatePoint(new Point(), this) is { } viewportOrigin)
        {
            var viewportRect = new Rect(viewportOrigin, SettingsContentScrollViewer.Viewport);
            targetRect = targetRect.Intersect(viewportRect);
        }

        var windowBounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var visibleRect = targetRect.Intersect(windowBounds);
        return visibleRect.Width > 0.5 && visibleRect.Height > 0.5
            ? visibleRect
            : null;
    }

    private Control? GetContextTutorialTargetControl(TutorialTopic topic, int step) => topic switch
        {
            TutorialTopic.Channels when step >= 2 => AddChannelButton,
            TutorialTopic.Channels => ChannelEditorCard,
            TutorialTopic.Logs when step == 0 => LogViewerSidebar,
            TutorialTopic.Logs when step == 1 => LogViewerFilters,
            TutorialTopic.Logs when step == 2 => LogViewerEntries,
            TutorialTopic.Logs when step == 3 => LogViewerActions,
            TutorialTopic.Logs => LogViewerContent,
            TutorialTopic.Moderation when step == 0 => ModerationHeader,
            TutorialTopic.Moderation when step == 1 => ModerationPlatformTabsPanel,
            TutorialTopic.Moderation when step == 2 => ModerationTabsPanel,
            TutorialTopic.Moderation => ModerationContentCard,
            TutorialTopic.Connect when step == 0 && ConnectPanelSignIn.IsVisible => ConnectPanelSignIn,
            TutorialTopic.Connect when step == 1 => ConnectWatchCard,
            TutorialTopic.Connect when step >= 2 => ConnectChannelTabs,
            TutorialTopic.Connect => ConnectPanelCard,
            TutorialTopic.Settings when step == 0 => SettingsNavigationCard,
            TutorialTopic.Settings when step == 1 => ProgramSettingsCard,
            TutorialTopic.Settings when step == 2 => ChatSettingsCard,
            TutorialTopic.Settings when step == 3 => ChatLogsSettingsCard,
            TutorialTopic.Settings when step == 4 => CopyOverlayUrlButton,
            TutorialTopic.Settings when step == 5 => AccountSettingsCard,
            TutorialTopic.Settings when step == 6 => DonateSettingsCard,
            TutorialTopic.Settings when step == 8 => ObsPluginStatusText,
            TutorialTopic.Settings when step == 9 => ObsPluginHelpButton,
            TutorialTopic.Settings => AdvancedSettingsCard,
            TutorialTopic.ObsPlugin when step == 0 => ObsPluginStatusText,
            TutorialTopic.ObsPlugin when step == 1 => ObsPluginHelpButton,
            TutorialTopic.ObsPlugin when step == 2 => RemoveObsPluginButton,
            TutorialTopic.ObsPlugin => CopyOverlayUrlButton,
            TutorialTopic.Events when step == 0 => StreamEventsCard,
            TutorialTopic.Events => StreamEventFiltersPanel,
            TutorialTopic.Protection when step == 0 => ProtectionTwitchSettingsCard,
            TutorialTopic.Protection when step == 1 => ProtectionLocalSettingsCard,
            TutorialTopic.Protection => ProtectionActionsPanel,
            TutorialTopic.Moments when step == 0 => MomentsListCard,
            TutorialTopic.Moments => ExportMomentsButton,
            TutorialTopic.SmartChat => SmartChatFiltersPanel,
            _ => null
        };

    private Rect? GetContextTutorialTargetRect(TutorialTopic topic, int step) =>
        GetVisibleTutorialTargetRect(GetContextTutorialTargetControl(topic, step), padding: 6);

    private Control? GetContextTutorialHelpButton(TutorialTopic topic) => topic switch
    {
        TutorialTopic.Channels => ChannelHelpButton,
        TutorialTopic.Logs => ChatLogsHelpButton,
        TutorialTopic.Moderation => ModerationHelpButton,
        TutorialTopic.Connect => ConnectHelpButton,
        TutorialTopic.Settings => SettingsHelpButton,
        TutorialTopic.ObsPlugin => ObsPluginHelpButton,
        TutorialTopic.Events => EventsHelpButton,
        TutorialTopic.Protection => ProtectionHelpButton,
        TutorialTopic.Moments => MomentsHelpButton,
        TutorialTopic.SmartChat => SmartChatHelpButton,
        _ => null
    };

    private Rect? GetVisibleTutorialTargetRect(Control? target, double padding)
    {
        if (target is null || !target.IsEffectivelyVisible ||
            target.Bounds.Width < 1 || target.Bounds.Height < 1)
        {
            return null;
        }

        var origin = target.TranslatePoint(new Point(), this);
        if (origin is null)
        {
            return null;
        }

        var targetRect = new Rect(
            origin.Value.X - padding,
            origin.Value.Y - padding,
            target.Bounds.Width + padding * 2,
            target.Bounds.Height + padding * 2);
        if (target.FindAncestorOfType<ScrollViewer>() is { } scrollViewer &&
            scrollViewer.TranslatePoint(new Point(), this) is { } viewportOrigin)
        {
            targetRect = targetRect.Intersect(new Rect(viewportOrigin, scrollViewer.Viewport));
        }

        var visibleRect = targetRect.Intersect(new Rect(0, 0, Bounds.Width, Bounds.Height));
        return visibleRect.Width > 0.5 && visibleRect.Height > 0.5 ? visibleRect : null;
    }

    private (double Left, double Top) PlaceContextTutorialCard(
        Rect? target,
        double cardWidth,
        double cardHeight,
        double fallbackLeft,
        double fallbackTop)
    {
        var edge = Bounds.Width < 520 ? 8d : 24d;
        const double gap = 18;
        if (target is not { } rect)
        {
            return (fallbackLeft, fallbackTop);
        }

        if (rect.Right + gap + cardWidth <= Bounds.Width - edge)
        {
            return (rect.Right + gap, Math.Clamp(rect.Center.Y - cardHeight / 2, 54, Bounds.Height - cardHeight - edge));
        }
        if (rect.Left - gap - cardWidth >= edge)
        {
            return (rect.Left - gap - cardWidth, Math.Clamp(rect.Center.Y - cardHeight / 2, 54, Bounds.Height - cardHeight - edge));
        }
        if (rect.Bottom + gap + cardHeight <= Bounds.Height - edge)
        {
            return (Math.Clamp(rect.Center.X - cardWidth / 2, edge, Bounds.Width - cardWidth - edge), rect.Bottom + gap);
        }
        if (rect.Top - gap - cardHeight >= 54)
        {
            return (Math.Clamp(rect.Center.X - cardWidth / 2, edge, Bounds.Width - cardWidth - edge), rect.Top - gap - cardHeight);
        }
        return (fallbackLeft, fallbackTop);
    }

    private void ApplyOnboardingFocus(Rect? targetRect, bool animate)
    {
        if (_onboardingFocusInitialized &&
            OnboardingFocusRectsMatch(_lastOnboardingTargetRect, targetRect))
        {
            return;
        }

        _onboardingFocusInitialized = true;
        _lastOnboardingTargetRect = targetRect;
        if (animate && OnboardingOverlay.IsVisible)
        {
            AnimateOnboardingFocusMask(targetRect);
            return;
        }

        ++_onboardingFocusTransitionVersion;
        SetOnboardingFocusGeometry(targetRect);
    }

    private void AnimateOnboardingFocusMask(Rect? targetRect)
    {
        var version = ++_onboardingFocusTransitionVersion;
        OnboardingDimmingMask.Transitions = [];
        OnboardingTargetHighlight.Transitions = [];
        var normalizedTarget = NormalizeOnboardingTargetRect(targetRect);
        var start = _renderedOnboardingTargetRect;
        var startRect = start ?? CollapseOnboardingFocusRect(normalizedTarget);
        var endRect = normalizedTarget ?? CollapseOnboardingFocusRect(start);
        var easing = new SplineEasing
        {
            X1 = 0.22,
            Y1 = 0.62,
            X2 = 0.18,
            Y2 = 1
        };
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        var stopwatch = Stopwatch.StartNew();
        var duration = TimeSpan.FromMilliseconds(1120);
        timer.Tick += (_, _) =>
        {
            if (version != _onboardingFocusTransitionVersion ||
                DataContext is not MainWindowViewModel { IsOnboardingOpen: true })
            {
                timer.Stop();
                return;
            }

            var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            var easedProgress = easing.Ease(progress);
            var current = InterpolateOnboardingFocusRect(startRect, endRect, easedProgress);
            SetOnboardingFocusGeometry(current);
            if (progress < 1)
            {
                return;
            }

            timer.Stop();
            SetOnboardingFocusGeometry(normalizedTarget);
        };
        timer.Start();
    }

    private void SetOnboardingFocusGeometry(Rect? targetRect)
    {
        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(1, Bounds.Height);
        var target = NormalizeOnboardingTargetRect(targetRect);
        _renderedOnboardingTargetRect = target;
        var outerPath = $"F0 M0,0 H{FormatGeometryNumber(width)} " +
                        $"V{FormatGeometryNumber(height)} H0 Z";
        var maskData = target is { Width: > 0.5, Height: > 0.5 }
            ? outerPath + " " + RoundedOnboardingFocusPath(target.Value, 20)
            : outerPath;
        OnboardingDimmingMask.Data = Geometry.Parse(maskData);
        if (target is { } visibleTarget)
        {
            SetCanvasRect(OnboardingTargetHighlight, visibleTarget);
        }
        else
        {
            SetCanvasRect(OnboardingTargetHighlight, default);
        }
    }

    private Rect? NormalizeOnboardingTargetRect(Rect? targetRect)
    {
        if (targetRect is not { } target)
        {
            return null;
        }

        var bounds = new Rect(0, 0, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));
        return target.Intersect(bounds);
    }

    private Rect CollapseOnboardingFocusRect(Rect? source)
    {
        var center = source?.Center ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        return new Rect(center.X, center.Y, 0, 0);
    }

    private static Rect InterpolateOnboardingFocusRect(Rect from, Rect to, double progress) =>
        new(
            InterpolateFocusValue(from.X, to.X, progress),
            InterpolateFocusValue(from.Y, to.Y, progress),
            InterpolateFocusValue(from.Width, to.Width, progress),
            InterpolateFocusValue(from.Height, to.Height, progress));

    private static double InterpolateFocusValue(double from, double to, double progress) =>
        from + ((to - from) * progress);

    private static string RoundedOnboardingFocusPath(Rect rect, double requestedRadius)
    {
        var radius = Math.Min(requestedRadius, Math.Min(rect.Width, rect.Height) / 2);
        var left = rect.Left;
        var top = rect.Top;
        var right = rect.Right;
        var bottom = rect.Bottom;
        return $"M{FormatGeometryNumber(left + radius)},{FormatGeometryNumber(top)} " +
               $"H{FormatGeometryNumber(right - radius)} " +
               $"Q{FormatGeometryNumber(right)},{FormatGeometryNumber(top)} " +
               $"{FormatGeometryNumber(right)},{FormatGeometryNumber(top + radius)} " +
               $"V{FormatGeometryNumber(bottom - radius)} " +
               $"Q{FormatGeometryNumber(right)},{FormatGeometryNumber(bottom)} " +
               $"{FormatGeometryNumber(right - radius)},{FormatGeometryNumber(bottom)} " +
               $"H{FormatGeometryNumber(left + radius)} " +
               $"Q{FormatGeometryNumber(left)},{FormatGeometryNumber(bottom)} " +
               $"{FormatGeometryNumber(left)},{FormatGeometryNumber(bottom - radius)} " +
               $"V{FormatGeometryNumber(top + radius)} " +
               $"Q{FormatGeometryNumber(left)},{FormatGeometryNumber(top)} " +
               $"{FormatGeometryNumber(left + radius)},{FormatGeometryNumber(top)} Z";
    }

    private static string FormatGeometryNumber(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static bool OnboardingFocusRectsMatch(Rect? left, Rect? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        const double tolerance = 0.5;
        return Math.Abs(left.Value.X - right.Value.X) <= tolerance &&
               Math.Abs(left.Value.Y - right.Value.Y) <= tolerance &&
               Math.Abs(left.Value.Width - right.Value.Width) <= tolerance &&
               Math.Abs(left.Value.Height - right.Value.Height) <= tolerance;
    }

    private static void SetCanvasRect(Control control, Rect rect)
    {
        Canvas.SetLeft(control, rect.X);
        Canvas.SetTop(control, rect.Y);
        control.Width = Math.Max(0, rect.Width);
        control.Height = Math.Max(0, rect.Height);
    }

    private void AnimateOnboardingStep(int step, bool reduceMotion)
    {
        var version = ++_onboardingStepTransitionVersion;
        var isContextTutorial = DataContext is MainWindowViewModel { IsContextTutorial: true };
        QueueTutorialTargetVisibility(step, version);
        AnimateOnboardingText(reduceMotion);
        _onboardingFocusUpdatePending = true;
        OnboardingCard.RenderTransformOrigin = RelativePoint.Center;
        if (reduceMotion || !OnboardingOverlay.IsVisible)
        {
            PositionOnboardingElements(step, positionCard: true, updateFocus: false);
            OnboardingCard.Opacity = 1;
            OnboardingCard.RenderTransform = TransformOperations.Identity;
            DispatcherTimer.RunOnce(() =>
            {
                if (version == _onboardingStepTransitionVersion &&
                    DataContext is MainWindowViewModel { IsOnboardingOpen: true })
                {
                    _onboardingFocusUpdatePending = false;
                    PositionOnboardingElements(
                        step,
                        positionCard: isContextTutorial,
                        updateFocus: true);
                }
            }, TimeSpan.FromMilliseconds(isContextTutorial ? 90 : 32));
            return;
        }

        var glideEasing = new SplineEasing
        {
            X1 = 0.22,
            Y1 = 0.62,
            X2 = 0.18,
            Y2 = 1
        };
        var settleEasing = new SpringEasing
        {
            Mass = 1,
            Stiffness = 190,
            Damping = 38,
            InitialVelocity = 0
        };
        OnboardingCard.Transitions =
        [
            new ThicknessTransition
            {
                Property = Layoutable.MarginProperty,
                Duration = TimeSpan.FromMilliseconds(1120),
                Easing = glideEasing
            },
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(420),
                Easing = glideEasing
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(1040),
                Easing = settleEasing
            }
        ];
        OnboardingCard.Opacity = 0.96;
        OnboardingCard.RenderTransform = TransformOperations.Parse("scale(0.997)");
        PositionOnboardingElements(
            step,
            positionCard: true,
            updateFocus: false);
        DispatcherTimer.RunOnce(() =>
        {
            if (version != _onboardingStepTransitionVersion ||
                DataContext is not MainWindowViewModel { IsOnboardingOpen: true })
            {
                return;
            }

            OnboardingCard.Opacity = 1;
            OnboardingCard.RenderTransform = TransformOperations.Identity;
        }, TimeSpan.FromMilliseconds(220));
        DispatcherTimer.RunOnce(() =>
        {
            if (version == _onboardingStepTransitionVersion &&
                DataContext is MainWindowViewModel { IsOnboardingOpen: true })
            {
                _onboardingFocusUpdatePending = false;
                PositionOnboardingElements(
                    step,
                    animateFocus: true,
                    positionCard: isContextTutorial,
                    updateFocus: true);
            }
        }, GetOnboardingFocusLayoutDelay(step));
    }

    private void AnimateOnboardingText(bool reduceMotion)
    {
        var version = ++_onboardingTextTransitionVersion;
        OnboardingAnimatedContent.Transitions = [];
        if (reduceMotion || !OnboardingOverlay.IsVisible)
        {
            OnboardingAnimatedContent.Opacity = 1;
            OnboardingAnimatedContent.RenderTransform = TransformOperations.Identity;
            return;
        }

        OnboardingAnimatedContent.Opacity = 0.38;
        OnboardingAnimatedContent.RenderTransform =
            TransformOperations.Parse("translate(0px, 9px)");
        DispatcherTimer.RunOnce(() =>
        {
            if (version != _onboardingTextTransitionVersion ||
                DataContext is not MainWindowViewModel { IsOnboardingOpen: true })
            {
                return;
            }

            var easing = new SplineEasing
            {
                X1 = 0.2,
                Y1 = 0.72,
                X2 = 0.2,
                Y2 = 1
            };
            OnboardingAnimatedContent.Transitions =
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(520),
                    Easing = easing
                },
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(580),
                    Easing = easing
                }
            ];
            OnboardingAnimatedContent.Opacity = 1;
            OnboardingAnimatedContent.RenderTransform = TransformOperations.Identity;
        }, TimeSpan.FromMilliseconds(24));
    }

    private static TimeSpan GetOnboardingFocusLayoutDelay(int step) =>
        TimeSpan.FromMilliseconds(step switch
        {
            2 => 120,
            4 => 120,
            6 or 7 or 8 => 390,
            _ => 80
        });

    private void AnimatePanel(Border overlay, Control card, bool open, bool reduceMotion)
    {
        var version = _panelTransitionVersions.GetValueOrDefault(overlay) + 1;
        _panelTransitionVersions[overlay] = version;
        var isSettings = ReferenceEquals(card, SettingsCard);
        if (reduceMotion)
        {
            SetOverlayStateImmediate(overlay, card, open);
            return;
        }

        var duration = open
            ? TimeSpan.FromMilliseconds(isSettings ? 340 : 300)
            : TimeSpan.FromMilliseconds(isSettings ? 280 : 250);
        overlay.Transitions = [];
        card.Transitions = [];
        card.RenderTransformOrigin = RelativePoint.Center;

        if (!open)
        {
            if (!overlay.IsVisible)
            {
                SetOverlayStateImmediate(overlay, card, false);
                return;
            }

            overlay.IsHitTestVisible = false;
            overlay.Transitions =
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = duration,
                    Easing = new SineEaseInOut()
                }
            ];
            card.Transitions =
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = duration,
                    Easing = new SineEaseInOut()
                },
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = duration,
                    Easing = new SineEaseInOut()
                }
            ];
            overlay.Opacity = 0;
            card.Opacity = 0;
            card.RenderTransform = TransformOperations.Parse(isSettings
                ? "translate(20px, 0px)"
                : "translate(0px, 6px) scale(0.996)");
            DispatcherTimer.RunOnce(() =>
            {
                if (_panelTransitionVersions.GetValueOrDefault(overlay) == version)
                {
                    SetOverlayStateImmediate(overlay, card, false);
                }
            }, duration + TimeSpan.FromMilliseconds(20));
            return;
        }

        overlay.IsVisible = true;
        overlay.IsHitTestVisible = true;
        overlay.Opacity = 0;
        card.Opacity = 0;
        card.RenderTransform = TransformOperations.Parse(isSettings
                ? "translate(32px, 0px)"
                : "translate(0px, 8px) scale(0.992)");

        DispatcherTimer.RunOnce(() =>
        {
            if (_panelTransitionVersions.GetValueOrDefault(overlay) != version)
            {
                return;
            }

            overlay.Transitions =
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = duration,
                    Easing = new CubicEaseOut()
                }
            ];
            card.Transitions =
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = duration,
                    Easing = new CubicEaseOut()
                },
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = duration,
                    Easing = new CubicEaseOut()
                }
            ];
            overlay.Opacity = 1;
            card.Opacity = 1;
            card.RenderTransform = TransformOperations.Identity;
        }, TimeSpan.FromMilliseconds(16));
    }

    private void SetAllOverlayStatesImmediate(MainWindowViewModel? viewModel)
    {
        SetOverlayStateImmediate(
            ConnectPanelOverlay,
            ConnectPanelCard,
            viewModel?.IsConnectPanelOpen == true);
        SetOverlayStateImmediate(
            LogViewerOverlay,
            LogViewerCard,
            viewModel?.IsLogViewerOpen == true);
        SetOverlayStateImmediate(
            ModerationPanelOverlay,
            ModerationPanelCard,
            viewModel?.IsModerationPanelOpen == true);
        SetOverlayStateImmediate(
            DeleteLogOverlay,
            DeleteLogCard,
            viewModel?.IsDeleteLogConfirmationOpen == true);
        SetOverlayStateImmediate(
            ModerationDialogOverlay,
            ModerationDialogCard,
            viewModel?.IsModerationDialogOpen == true);
        SetOverlayStateImmediate(
            SettingsOverlay,
            SettingsCard,
            viewModel?.IsSettingsOpen == true);
        SetOverlayStateImmediate(
            OnboardingOverlay,
            OnboardingCard,
            viewModel is { IsOnboardingOpen: true, IsMainWindowTutorial: true });
        if (viewModel is { IsOnboardingOpen: true, IsMainWindowTutorial: true })
        {
            PositionOnboardingElements(viewModel.OnboardingStep);
        }
    }

    private static void SetOverlayStateImmediate(Border overlay, Control card, bool open)
    {
        overlay.Transitions = [];
        card.Transitions = [];
        overlay.IsVisible = open;
        overlay.IsHitTestVisible = open;
        overlay.Opacity = open ? 1 : 0;
        card.Opacity = open ? 1 : 0;
        card.RenderTransform = TransformOperations.Identity;
    }

    private void HandleModalFocusPropertyChanged(MainWindowViewModel viewModel, string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(MainWindowViewModel.IsChannelEditorOpen):
                HandleModalFocusChange(ChannelEditorCard, viewModel.IsChannelEditorOpen);
                break;
            case nameof(MainWindowViewModel.IsLogViewerOpen):
                HandleModalFocusChange(LogViewerCard, viewModel.IsLogViewerOpen);
                break;
            case nameof(MainWindowViewModel.IsStreamEventsOpen):
                HandleModalFocusChange(StreamEventsCard, viewModel.IsStreamEventsOpen);
                break;
            case nameof(MainWindowViewModel.IsProtectionPanelOpen):
                HandleModalFocusChange(ProtectionPanelCard, viewModel.IsProtectionPanelOpen);
                break;
            case nameof(MainWindowViewModel.IsClearChatConfirmationOpen):
                HandleModalFocusChange(ClearChatConfirmationCard, viewModel.IsClearChatConfirmationOpen);
                break;
            case nameof(MainWindowViewModel.IsMomentsPanelOpen):
                HandleModalFocusChange(MomentsPanelCard, viewModel.IsMomentsPanelOpen);
                break;
            case nameof(MainWindowViewModel.IsMomentEditorOpen):
                HandleModalFocusChange(MomentEditorCard, viewModel.IsMomentEditorOpen);
                break;
            case nameof(MainWindowViewModel.IsModerationPanelOpen):
                HandleModalFocusChange(ModerationPanelCard, viewModel.IsModerationPanelOpen);
                break;
            case nameof(MainWindowViewModel.IsRecentMessagesOpen):
                HandleModalFocusChange(RecentMessagesCard, viewModel.IsRecentMessagesOpen);
                break;
            case nameof(MainWindowViewModel.IsDeleteLogConfirmationOpen):
                HandleModalFocusChange(DeleteLogCard, viewModel.IsDeleteLogConfirmationOpen);
                break;
            case nameof(MainWindowViewModel.IsModerationDialogOpen):
                HandleModalFocusChange(ModerationDialogCard, viewModel.IsModerationDialogOpen);
                break;
            case nameof(MainWindowViewModel.IsConnectPanelOpen):
                HandleModalFocusChange(ConnectPanelCard, viewModel.IsConnectPanelOpen);
                break;
            case nameof(MainWindowViewModel.IsSettingsOpen):
                HandleModalFocusChange(SettingsCard, viewModel.IsSettingsOpen);
                break;
            case nameof(MainWindowViewModel.IsOnboardingOpen):
                HandleModalFocusChange(
                    OnboardingCard,
                    viewModel.IsOnboardingOpen && viewModel.IsMainWindowTutorial,
                    focusOnOpen: false);
                break;
        }
    }

    private void HandleModalFocusChange(Control card, bool open, bool focusOnOpen = true)
    {
        var version = _modalFocusVersions.GetValueOrDefault(card) + 1;
        _modalFocusVersions[card] = version;

        if (open)
        {
            var focusedControl = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
            _modalFocusReturnTargets[card] = focusedControl is not null &&
                                                   !IsInsideModalCard(focusedControl, card)
                ? focusedControl
                : null;
            if (!focusOnOpen)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_modalFocusVersions.GetValueOrDefault(card) == version &&
                    DataContext is MainWindowViewModel viewModel &&
                    ReferenceEquals(GetTopmostModalCard(viewModel), card))
                {
                    FocusFirstControl(card);
                }
            }, DispatcherPriority.Input);
            return;
        }

        _modalFocusReturnTargets.Remove(card, out var returnTarget);
        Dispatcher.UIThread.Post(() =>
        {
            if (_modalFocusVersions.GetValueOrDefault(card) != version ||
                DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            var activeCard = GetTopmostModalCard(viewModel);
            if (activeCard is not null)
            {
                if (returnTarget is not null &&
                    IsInsideModalCard(returnTarget, activeCard) &&
                    CanReceiveFocus(returnTarget))
                {
                    returnTarget.Focus();
                }
                else
                {
                    FocusFirstControl(activeCard);
                }
                return;
            }

            if (returnTarget is not null && CanReceiveFocus(returnTarget))
            {
                returnTarget.Focus();
            }
            else if (CanReceiveFocus(MessagesList))
            {
                MessagesList.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private Control? GetTopmostModalCard(MainWindowViewModel viewModel)
    {
        if (viewModel is { IsOnboardingOpen: true, IsMainWindowTutorial: true })
        {
            return OnboardingCard;
        }
        if (viewModel.IsModerationDialogOpen)
        {
            return ModerationDialogCard;
        }
        if (viewModel.IsDeleteLogConfirmationOpen)
        {
            return DeleteLogCard;
        }
        if (viewModel.IsRecentMessagesOpen)
        {
            return RecentMessagesCard;
        }
        if (viewModel.IsMomentEditorOpen)
        {
            return MomentEditorCard;
        }
        if (viewModel.IsClearChatConfirmationOpen)
        {
            return ClearChatConfirmationCard;
        }
        if (viewModel.IsSettingsOpen)
        {
            return SettingsCard;
        }
        if (viewModel.IsConnectPanelOpen)
        {
            return ConnectPanelCard;
        }
        if (viewModel.IsModerationPanelOpen)
        {
            return ModerationPanelCard;
        }
        if (viewModel.IsMomentsPanelOpen)
        {
            return MomentsPanelCard;
        }
        if (viewModel.IsProtectionPanelOpen)
        {
            return ProtectionPanelCard;
        }
        if (viewModel.IsStreamEventsOpen)
        {
            return StreamEventsCard;
        }
        if (viewModel.IsLogViewerOpen)
        {
            return LogViewerCard;
        }
        return viewModel.IsChannelEditorOpen ? ChannelEditorCard : null;
    }

    private static bool IsInsideModalCard(Control control, Control card) =>
        ReferenceEquals(control, card) ||
        control.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, card));

    private static bool CanReceiveFocus(Control control) =>
        control.Focusable && control.IsEffectivelyEnabled && control.IsEffectivelyVisible;

    private static void FocusFirstControl(Control card)
    {
        var focusTarget = card.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(CanReceiveFocus);
        focusTarget?.Focus();
    }

    private void MessageContextMenu_OnOpening(
        object? sender,
        System.ComponentModel.CancelEventArgs eventArgs)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        var messageItem = menu.DataContext as ChatMessageItemViewModel ??
                          menu.PlacementTarget?.DataContext as ChatMessageItemViewModel;
        if (messageItem is { } item &&
            DataContext is MainWindowViewModel viewModel)
        {
            var canModerate = viewModel.CanModerateTarget(item);
            var isPunished = viewModel.IsUserPunished(item);
            SetMenuItemVisible(menu, "MessageModerationSeparator", canModerate);
            SetMenuItemVisible(menu, "MessageDeleteMenuItem", canModerate);
            SetMenuItemVisible(menu, "MessageBanMenuItem", canModerate && !isPunished);
            SetMenuItemVisible(menu, "MessageTimeoutMenuItem", canModerate && !isPunished);
            SetMenuItemVisible(menu, "MessageCustomTimeoutMenuItem", canModerate && !isPunished);
            SetMenuItemVisible(menu, "MessageRemovePunishmentMenuItem", canModerate && isPunished);
            _ = viewModel.PrepareMessageUserProfileAsync(item);
        }

        _contextMenusCompletingClose.Remove(menu);
        var version = _contextMenuTransitionVersions.GetValueOrDefault(menu) + 1;
        _contextMenuTransitionVersions[menu] = version;
        menu.IsHitTestVisible = true;
        menu.Transitions = [];
        if (DataContext is MainWindowViewModel { ReduceMotion: true })
        {
            menu.Opacity = 1;
            menu.RenderTransform = TransformOperations.Identity;
            return;
        }

        menu.RenderTransformOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative);
        menu.Opacity = 0;
        menu.RenderTransform = TransformOperations.Parse("translate(0px, 5px) scale(0.992)");
        DispatcherTimer.RunOnce(() =>
        {
            if (_contextMenuTransitionVersions.GetValueOrDefault(menu) != version)
            {
                return;
            }

            var duration = TimeSpan.FromMilliseconds(220);
            menu.Transitions =
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = duration,
                    Easing = new CubicEaseOut()
                },
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = duration,
                    Easing = new CubicEaseOut()
                }
            ];
            menu.Opacity = 1;
            menu.RenderTransform = TransformOperations.Identity;
        }, TimeSpan.FromMilliseconds(16));
    }

    private void MessageRow_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(sender as Visual).Properties.PointerUpdateKind ==
                PointerUpdateKind.RightButtonPressed &&
            sender is Control { DataContext: ChatMessageItemViewModel item } &&
            DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.PrepareMessageUserProfileAsync(item);
        }
    }

    private static void SetMenuItemVisible(ContextMenu menu, string name, bool isVisible)
    {
        if (menu.FindControl<Control>(name) is { } control)
        {
            control.IsVisible = isVisible;
        }
    }

    private void MessagesList_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is not null)
        {
            listBox.SelectedItem = null;
        }
    }

    private void MessageContextMenu_OnClosing(
        object? sender,
        System.ComponentModel.CancelEventArgs eventArgs)
    {
        if (sender is not ContextMenu menu ||
            DataContext is MainWindowViewModel { ReduceMotion: true })
        {
            return;
        }

        if (_contextMenusCompletingClose.Remove(menu))
        {
            return;
        }

        eventArgs.Cancel = true;
        var version = _contextMenuTransitionVersions.GetValueOrDefault(menu) + 1;
        _contextMenuTransitionVersions[menu] = version;
        var duration = TimeSpan.FromMilliseconds(190);
        menu.IsHitTestVisible = false;
        menu.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = duration,
                Easing = new SineEaseInOut()
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = duration,
                Easing = new SineEaseInOut()
            }
        ];
        menu.Opacity = 0;
        menu.RenderTransform = TransformOperations.Parse("translate(0px, 4px) scale(0.995)");
        DispatcherTimer.RunOnce(() =>
        {
            if (_contextMenuTransitionVersions.GetValueOrDefault(menu) != version)
            {
                return;
            }

            _contextMenusCompletingClose.Add(menu);
            menu.Close();
        }, duration + TimeSpan.FromMilliseconds(16));
    }

    private void ChannelTextBox_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter && DataContext is MainWindowViewModel viewModel && viewModel.ConnectCommand.CanExecute(null))
        {
            viewModel.ConnectCommand.Execute(null);
            eventArgs.Handled = true;
        }
    }

    private void ConnectPanelChannel_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter ||
            DataContext is not MainWindowViewModel viewModel ||
            !viewModel.CanWatchChannel)
        {
            return;
        }

        viewModel.WatchChannelFromConnectPanelCommand.Execute(null);
        eventArgs.Handled = true;
    }

    private void ConnectPanelScrim_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!ReferenceEquals(eventArgs.Source, sender) ||
            DataContext is not MainWindowViewModel { IsConnectPanelOpen: true } viewModel)
        {
            return;
        }

        viewModel.CloseConnectPanelCommand.Execute(null);
        eventArgs.Handled = true;
    }

    private void ComposerTextBox_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter || eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        if (DataContext is MainWindowViewModel viewModel && viewModel.SendMessageCommand.CanExecute(null))
        {
            viewModel.SendMessageCommand.Execute(null);
            eventArgs.Handled = true;
        }
    }

    private void ChannelEditorDismissLayer_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (DataContext is MainWindowViewModel { IsOnboardingOpen: true })
        {
            eventArgs.Handled = true;
            return;
        }

        if (DataContext is MainWindowViewModel { IsChannelEditorOpen: true } viewModel &&
            viewModel.ToggleChannelEditorCommand.CanExecute(null))
        {
            viewModel.ToggleChannelEditorCommand.Execute(null);
            eventArgs.Handled = true;
        }
    }

    private void OnboardingInputBlocker_OnPointerPressed(
        object? sender,
        PointerPressedEventArgs eventArgs)
    {
        OnboardingNextButton.Focus();
        eventArgs.Handled = true;
    }

    private static void OnboardingInputBlocker_OnPointerWheelChanged(
        object? sender,
        PointerWheelEventArgs eventArgs)
    {
        eventArgs.Handled = true;
    }

    private void OnWindowPreviewKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (_middleScrollActive && eventArgs.Key == Key.Escape)
        {
            StopMiddleScroll();
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Escape)
        {
            var dropdown = this.GetLogicalDescendants().OfType<ComboBox>()
                .FirstOrDefault(combo => combo.IsDropDownOpen && combo.IsEffectivelyVisible);
            if (dropdown is not null)
            {
                dropdown.IsDropDownOpen = false;
                dropdown.Focus();
                eventArgs.Handled = true;
                return;
            }
        }

        if (eventArgs.Key == Key.Escape &&
            DataContext is MainWindowViewModel viewModel &&
            TryCloseTopmostModal(viewModel))
        {
            eventArgs.Handled = true;
            return;
        }

        if (DataContext is not MainWindowViewModel
            {
                IsOnboardingOpen: true,
                IsMainWindowTutorial: true
            })
        {
            return;
        }

        if (eventArgs.Source is Visual source &&
            source.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, OnboardingCard)))
        {
            return;
        }

        OnboardingNextButton.Focus();
        eventArgs.Handled = true;
    }

    private static bool TryCloseTopmostModal(MainWindowViewModel viewModel)
    {
        if (viewModel is { IsOnboardingOpen: true, IsMainWindowTutorial: true })
        {
            viewModel.SkipOnboardingCommand.Execute(null);
            return true;
        }
        if (viewModel.IsModerationDialogOpen)
        {
            viewModel.CancelModerationCommand.Execute(null);
            return true;
        }
        if (viewModel.IsDeleteLogConfirmationOpen)
        {
            viewModel.CancelDeleteLogCommand.Execute(null);
            return true;
        }
        if (viewModel.IsRecentMessagesOpen)
        {
            viewModel.CloseRecentMessagesCommand.Execute(null);
            return true;
        }
        if (viewModel.IsMomentEditorOpen)
        {
            viewModel.CancelSaveMomentCommand.Execute(null);
            return true;
        }
        if (viewModel.IsClearChatConfirmationOpen)
        {
            viewModel.CancelClearChatCommand.Execute(null);
            return true;
        }
        if (viewModel.IsSettingsOpen)
        {
            if (viewModel.CancelSettingsCommand.CanExecute(null))
            {
                viewModel.CancelSettingsCommand.Execute(null);
            }
            return true;
        }
        if (viewModel.IsConnectPanelOpen)
        {
            viewModel.CloseConnectPanelCommand.Execute(null);
            return true;
        }
        if (viewModel.IsModerationPanelOpen)
        {
            viewModel.CloseModerationPanelCommand.Execute(null);
            return true;
        }
        if (viewModel.IsMomentsPanelOpen)
        {
            viewModel.CloseMomentsPanelCommand.Execute(null);
            return true;
        }
        if (viewModel.IsProtectionPanelOpen)
        {
            viewModel.CloseProtectionPanelCommand.Execute(null);
            return true;
        }
        if (viewModel.IsStreamEventsOpen)
        {
            viewModel.CloseStreamEventsCommand.Execute(null);
            return true;
        }
        if (viewModel.IsLogViewerOpen)
        {
            viewModel.CloseLogsCommand.Execute(null);
            return true;
        }
        if (viewModel.IsChannelEditorOpen)
        {
            viewModel.ToggleChannelEditorCommand.Execute(null);
            return true;
        }
        return false;
    }

    private void SettingsScrim_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!ReferenceEquals(eventArgs.Source, sender) ||
            DataContext is not MainWindowViewModel { IsSettingsOpen: true } viewModel ||
            !viewModel.CancelSettingsCommand.CanExecute(null))
        {
            return;
        }

        viewModel.CancelSettingsCommand.Execute(null);
        eventArgs.Handled = true;
    }

    private void LogViewerOverlay_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!ReferenceEquals(eventArgs.Source, sender) ||
            DataContext is not MainWindowViewModel { IsLogViewerOpen: true } viewModel ||
            !viewModel.CloseLogsCommand.CanExecute(null))
        {
            return;
        }

        viewModel.CloseLogsCommand.Execute(null);
        eventArgs.Handled = true;
    }

    private void RichChatTextBlock_OnLinkClicked(object? sender, ValueEventArgs<Uri> eventArgs)
    {
        if (DataContext is MainWindowViewModel viewModel &&
            viewModel.OpenChatLinkCommand.CanExecute(eventArgs.Value))
        {
            viewModel.OpenChatLinkCommand.Execute(eventArgs.Value);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "An external URI launch is an async UI boundary and must never terminate the application.")]
    private async void OnOpenUriRequested(object? sender, ValueEventArgs<Uri> eventArgs)
    {
        var uri = eventArgs.Value;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                using var process = Process.Start(CreateMacOpenUriStartInfo(uri));
                if (process is null && DataContext is MainWindowViewModel macViewModel)
                {
                    macViewModel.AuthStatus = macViewModel.Texts.BrowserOpenFailed();
                }
                return;
            }

            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null || !await launcher.LaunchUriAsync(uri))
            {
                if (DataContext is MainWindowViewModel viewModel)
                {
                    viewModel.AuthStatus = viewModel.Texts.BrowserOpenFailed();
                }
            }
        }
        catch (Exception exception)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.AuthStatus = viewModel.Texts.BrowserOpenFailed(
                    AppDiagnostics.GetUserMessage(exception, "Open external URI"));
            }
        }
    }

    internal static ProcessStartInfo CreateMacOpenUriStartInfo(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("An external URI must be absolute.", nameof(uri));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/open",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(uri.AbsoluteUri);
        return startInfo;
    }

    private async void OnOpenLogDirectoryRequested(object? sender, ValueEventArgs<string> eventArgs)
    {
        try
        {
            var path = Path.GetFullPath(eventArgs.Value);
            if (!Path.EndsInDirectorySeparator(path))
            {
                path += Path.DirectorySeparatorChar;
            }

            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null || !await launcher.LaunchUriAsync(new Uri(path)))
            {
                if (DataContext is MainWindowViewModel viewModel)
                {
                    viewModel.StatusDetail = viewModel.Texts.LogOpenFailed;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.StatusDetail = viewModel.Texts.LogOpenFailed;
            }
        }
    }

    private async void OnCopyTextRequested(object? sender, ValueEventArgs<string> eventArgs)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                throw new InvalidOperationException("Clipboard service is unavailable.");
            }
            await clipboard.SetTextAsync(eventArgs.Value);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ExternalException or COMException or
                OperationCanceledException or ObjectDisposedException)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.StatusDetail = viewModel.Texts.ClipboardUnavailable;
            }
        }
    }

    private async void BrowseChatLogsFolder_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = viewModel.Texts.ChatLogsFolder,
                AllowMultiple = false
            });
            var folder = folders.FirstOrDefault();
            if (folder is not null)
            {
                using (folder)
                {
                    var path = folder.TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        viewModel.ChatLogsFolder = path;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            viewModel.StatusDetail = viewModel.Texts.SettingsSaveFailed(
                AppDiagnostics.GetUserMessage(exception, "Browse chat log folder"));
        }
    }

    private async void ExportLogTxt_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await ExportSelectedLogAsync(jsonLines: false);

    private async void ExportLogJsonl_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await ExportSelectedLogAsync(jsonLines: true);

    private async void ExportMoments_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.HasStreamMoments)
        {
            return;
        }
        try
        {
            var fileType = new FilePickerFileType("JSON") { Patterns = ["*.json"] };
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = viewModel.Texts.ExportMoments,
                SuggestedFileName = "WitherChat-moments-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json",
                DefaultExtension = "json",
                FileTypeChoices = [fileType],
                SuggestedFileType = fileType,
                ShowOverwritePrompt = true
            });
            if (target is null)
            {
                return;
            }
            using (target)
            {
                await ExportMomentsToStreamAsync(viewModel.GetMomentSnapshot(),
                    viewModel.MomentStorageDirectory, target.TryGetLocalPath(), target.OpenWriteAsync,
                    viewModel.Texts.MomentsExportDifferentFileRequired);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          NotSupportedException or JsonException)
        {
            viewModel.MomentsStatus = viewModel.Texts.MomentsSaveFailed(
                AppDiagnostics.GetUserMessage(exception, "Export stream moments"));
            viewModel.StatusDetail = viewModel.MomentsStatus;
        }
    }

    internal static async Task ExportMomentsToStreamAsync(
        IReadOnlyList<StreamMoment> moments, string? storageDirectory,
        string? targetLocalPath, Func<Task<Stream>> openOutput,
        string collisionMessage = "Choose an export file outside the WitherChat data folder.")
    {
        ArgumentNullException.ThrowIfNull(moments);
        if (!string.IsNullOrWhiteSpace(storageDirectory) && !string.IsNullOrWhiteSpace(targetLocalPath))
        {
            var root = Path.GetFullPath(storageDirectory).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var target = Path.GetFullPath(targetLocalPath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(root, target, comparison) ||
                target.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new IOException(collisionMessage);
        }
        // Prepare the entire snapshot before touching an existing destination.
        var payload = JsonSerializer.SerializeToUtf8Bytes(moments,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await using var output = await openOutput();
        output.SetLength(0);
        if (output.CanSeek) output.Position = 0;
        await output.WriteAsync(payload);
        await output.FlushAsync();
    }

    private async Task ExportSelectedLogAsync(bool jsonLines)
    {
        if (DataContext is not MainWindowViewModel { SelectedLogFile: { } selected } viewModel)
        {
            return;
        }
        try
        {
            var root = Path.GetFullPath(viewModel.ChatLogDirectory) + Path.DirectorySeparatorChar;
            var sourcePath = Path.GetFullPath(selected.Path);
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!sourcePath.StartsWith(root, pathComparison) || !File.Exists(sourcePath))
            {
                return;
            }

            var extension = jsonLines ? "jsonl" : "txt";
            var fileType = new FilePickerFileType(jsonLines ? "JSON Lines" : "Text")
            {
                Patterns = ["*." + extension]
            };
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = jsonLines ? viewModel.Texts.ExportJsonl : viewModel.Texts.ExportTxt,
                SuggestedFileName = Path.GetFileNameWithoutExtension(sourcePath) + "." + extension,
                DefaultExtension = extension,
                FileTypeChoices = [fileType],
                SuggestedFileType = fileType,
                ShowOverwritePrompt = true
            });
            if (target is null)
            {
                return;
            }

            using (target)
            {
                await ExportLogToStreamAsync(sourcePath, target.TryGetLocalPath(),
                    target.OpenWriteAsync, jsonLines, viewModel.Texts.ExportDifferentFileRequired);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            viewModel.StatusDetail = viewModel.Texts.LogOpenFailed + " " +
                                     AppDiagnostics.GetUserMessage(exception, "Export chat log");
        }
    }

    internal static async Task ExportLogToStreamAsync(
        string sourcePath, string? targetLocalPath, Func<Task<Stream>> openOutput,
        bool jsonLines, string collisionMessage = "The export cannot overwrite an original chat log.")
    {
        sourcePath = Path.GetFullPath(sourcePath);
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(targetLocalPath))
        {
            var targetPath = Path.GetFullPath(targetLocalPath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var protectedPaths = new[] { sourcePath,
                Path.Combine(directory, "chat.jsonl"), Path.Combine(directory, "chat.txt") };
            if (protectedPaths.Any(path => string.Equals(path, targetPath, comparison)))
                throw new IOException(collisionMessage);
        }

        var companionPath = Path.Combine(directory, jsonLines ? "chat.jsonl" : "chat.txt");
        var directCopyPath = File.Exists(companionPath) ? companionPath :
            jsonLines && sourcePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
            !jsonLines && (sourcePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                           sourcePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                ? sourcePath : null;
        // Open the source first: a missing/unreadable source must not clear an
        // existing destination. Shared read access also permits live logging.
        await using var input = new FileStream(directCopyPath ?? sourcePath, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        await using var output = await openOutput();
        output.SetLength(0);
        if (output.CanSeek) output.Position = 0;
        if (directCopyPath is not null)
        {
            await input.CopyToAsync(output);
            return;
        }
        using var reader = new StreamReader(input, leaveOpen: true);
        await using var writer = new StreamWriter(output, leaveOpen: true);
        while (await reader.ReadLineAsync() is { } line)
        {
            var entry = ChatLogEntryViewModel.Parse(line);
            if (jsonLines)
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    time = entry.TimeText, user = entry.User,
                    text = entry.Text, raw = entry.RawText
                }));
            else
                await writer.WriteLineAsync($"[{entry.TimeText}] {entry.User}: {entry.Text}");
        }
        await writer.FlushAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (Avalonia.Application.Current is not App app ||
            app.IsExitRequested ||
            app.IsSystemShutdownRequested)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_isObsDockLayout) { Hide(); return; }
        if (_closeAnimationInProgress)
        {
            return;
        }

        _closeAnimationInProgress = true;
        IsEnabled = false;
        try
        {
            if (app.IsTrayAvailable && DataContext is MainWindowViewModel { CloseToTray: true })
            {
                var platformHandle = TryGetPlatformHandle();
                var usedSystemAnimation =
                    platformHandle is not null &&
                    string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.Ordinal) &&
                    WindowsWindowAnimation.TryMinimize(platformHandle.Handle);
                if (usedSystemAnimation)
                {
                    await Task.Delay(360);
                }
                else
                {
                    await AnimateWindowMinimizeVisualAsync();
                }
                Hide();
                _wasMinimized = false;
                WindowState = WindowState.Normal;
                if (!_trayNoticeShown && DataContext is MainWindowViewModel { ToastNotifications: true } viewModel)
                {
                    _trayNoticeShown = true;
                    WindowsTrayNotification.Show(
                        TryGetPlatformHandle()?.Handle ?? IntPtr.Zero,
                        viewModel.Texts.StillRunningTitle,
                        viewModel.Texts.StillRunningMessage);
                }
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                await AnimateWindowCloseVisualAsync(offsetY: 8, durationMilliseconds: 240);
            }

            await app.RequestExitAsync();
        }
        finally
        {
            if (!app.IsExitRequested)
            {
                ResetWindowVisual();
                IsEnabled = true;
                _closeAnimationInProgress = false;
            }
        }
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        var platformHandle = TryGetPlatformHandle();
        if (platformHandle is not null &&
            string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.Ordinal))
        {
            WindowsWindowAnimation.RemoveSystemCommandHook(platformHandle.Handle);
        }

        if (_messageScrollViewer is not null)
        {
            _messageScrollViewer.ScrollChanged -= OnMessageScrollChanged;
            _messageScrollViewer = null;
        }
        if (_twitchSplitScrollViewer is not null)
        {
            _twitchSplitScrollViewer.ScrollChanged -= OnTwitchSplitScrollChanged;
            _twitchSplitScrollViewer = null;
        }
        if (_youTubeSplitScrollViewer is not null)
        {
            _youTubeSplitScrollViewer.ScrollChanged -= OnYouTubeSplitScrollChanged;
            _youTubeSplitScrollViewer = null;
        }
        CancelFollowLatest();
        StopMiddleScroll();
        _scrollIdleTimer.Stop();
        _scrollIdleTimer.Tick -= OnScrollIdleTimerTick;
        _middleScrollTimer.Tick -= OnMiddleScrollTimerTick;
        _contextTutorialLayoutTimer.Stop();
        _contextTutorialLayoutTimer.Tick -= OnContextTutorialLayoutTimerTick;
        MessagesList.RemoveHandler(InputElement.PointerPressedEvent, MessagesList_OnPointerPressed);
        MessagesList.RemoveHandler(InputElement.PointerWheelChangedEvent, MessagesList_OnPointerWheelChanged);
        MessagesList.RemoveHandler(InputElement.PointerReleasedEvent, MessagesList_OnPointerReleased);
        MessagesList.RemoveHandler(InputElement.PointerMovedEvent, MessagesList_OnPointerMoved);
        MessagesList.RemoveHandler(InputElement.PointerCaptureLostEvent, MessagesList_OnPointerCaptureLost);
        MessagesList.RemoveHandler(InputElement.ScrollGestureEvent, MessagesList_OnScrollGesture);
        MessagesList.RemoveHandler(InputElement.KeyDownEvent, MessagesList_OnKeyDown);
        RemoveSplitMessageInputHandlers(TwitchMessagesList);
        RemoveSplitMessageInputHandlers(YouTubeMessagesList);

        UnsubscribeFromViewModel();
        DataContextChanged -= OnDataContextChanged;
        AttachedToVisualTree -= OnAttachedToVisualTree;
        Opened -= OnOpened;
        SizeChanged -= OnWindowSizeChanged;
        PropertyChanged -= OnWindowPropertyChanged;
        Closing -= OnClosing;
        Closed -= OnClosed;
    }

    private void RemoveSplitMessageInputHandlers(ListBox list)
    {
        list.RemoveHandler(InputElement.PointerPressedEvent, SplitMessagesList_OnPointerPressed);
        list.RemoveHandler(InputElement.PointerMovedEvent, SplitMessagesList_OnPointerMoved);
        list.RemoveHandler(InputElement.PointerCaptureLostEvent, SplitMessagesList_OnPointerCaptureLost);
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedViewModel is null)
        {
            return;
        }

        _subscribedViewModel.MessagesChanged -= OnMessagesChanged;
        _subscribedViewModel.ScrollToLatestRequested -= OnScrollToLatestRequested;
        _subscribedViewModel.OpenUriRequested -= OnOpenUriRequested;
        _subscribedViewModel.OpenLogDirectoryRequested -= OnOpenLogDirectoryRequested;
        _subscribedViewModel.CopyTextRequested -= OnCopyTextRequested;
        _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        CancelFollowLatest();
        _scrollIdleTimer.Stop();
        _isUserScrolling = false;
        _subscribedViewModel.SetUserScrolling(false);
        AnimatedEmoteImage.SetFastScrolling(false);
        _subscribedViewModel = null;
    }
}
