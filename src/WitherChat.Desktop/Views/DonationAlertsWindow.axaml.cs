using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using System.Globalization;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Platforms;
using WitherChat.Desktop.ViewModels;

namespace WitherChat.Desktop.Views;

public partial class DonationAlertsWindow : Window
{
    private const double ResizeHitThickness = 7;
    private const double MiddleScrollDeadZone = 12;
    private static readonly Cursor HorizontalResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor VerticalResizeCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor NorthWestResizeCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor NorthEastResizeCursor = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor SouthWestResizeCursor = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor SouthEastResizeCursor = new(StandardCursorType.BottomRightCorner);
    private readonly DispatcherTimer _middleScrollTimer;
    private IPointer? _middleScrollPointer;
    private Point _middleScrollAnchor;
    private Point _middleScrollCurrent;
    private bool _middleScrollActive;
    private bool _allowClose;
    private MainWindowViewModel? _viewModel;

    public DonationAlertsWindow()
    {
        InitializeComponent();
        _middleScrollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            OnMiddleScrollTimerTick);
        Closing += OnClosing;
        Closed += OnClosed;
        Opened += OnOpened;
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => PositionDonationTutorial();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        WindowRoot.AddHandler(
            PointerMovedEvent,
            WindowRoot_OnPointerMoved,
            RoutingStrategies.Tunnel);
        WindowRoot.AddHandler(
            PointerPressedEvent,
            WindowRoot_OnPointerPressed,
            RoutingStrategies.Tunnel);
        WindowRoot.PointerExited += WindowRoot_OnPointerExited;
        DonationHistoryScrollViewer.AddHandler(
            PointerPressedEvent,
            DonationHistory_OnPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        DonationHistoryScrollViewer.AddHandler(
            PointerMovedEvent,
            DonationHistory_OnPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        DonationHistoryScrollViewer.PointerCaptureLost += DonationHistory_OnPointerCaptureLost;
        Deactivated += (_, _) => StopMiddleScroll();
    }

    public void CloseForApplicationExit()
    {
        _allowClose = true;
        Close();
    }

    private void OnOpened(object? sender, EventArgs eventArgs)
    {
        UpdateDonationTutorialFocus();
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

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        var platformHandle = TryGetPlatformHandle();
        if (platformHandle is not null &&
            string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.Ordinal))
        {
            WindowsWindowAnimation.RemoveSystemCommandHook(platformHandle.Handle);
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        CloseDonationTutorial();
        if (_allowClose ||
            Avalonia.Application.Current is App app &&
            (app.IsExitRequested || app.IsSystemShutdownRequested))
        {
            StopMiddleScroll();
            return;
        }
        StopMiddleScroll();
        eventArgs.Cancel = true;
        Hide();
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(eventArgs);
        }
    }

    private void CloseButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        StopMiddleScroll();
        CloseDonationTutorial();
        Hide();
    }

    private void MinimizeButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        WindowState = WindowState.Minimized;

    private void WindowRoot_OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (_middleScrollActive)
        {
            Cursor = Avalonia.Input.Cursor.Default;
            return;
        }
        Cursor = ResizeEdgeAt(eventArgs.GetPosition(this), Bounds.Size) switch
        {
            WindowEdge.West or WindowEdge.East => HorizontalResizeCursor,
            WindowEdge.North or WindowEdge.South => VerticalResizeCursor,
            WindowEdge.NorthWest => NorthWestResizeCursor,
            WindowEdge.NorthEast => NorthEastResizeCursor,
            WindowEdge.SouthWest => SouthWestResizeCursor,
            WindowEdge.SouthEast => SouthEastResizeCursor,
            _ => Avalonia.Input.Cursor.Default
        };
    }

    private void WindowRoot_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            ResizeEdgeAt(eventArgs.GetPosition(this), Bounds.Size) is not { } edge)
        {
            return;
        }

        BeginResizeDrag(edge, eventArgs);
        eventArgs.Handled = true;
    }

    private void WindowRoot_OnPointerExited(object? sender, PointerEventArgs eventArgs) =>
        Cursor = Avalonia.Input.Cursor.Default;

    private void DonationHistory_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        var point = eventArgs.GetCurrentPoint(DonationHistoryScrollViewer);
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
        {
            if (_middleScrollActive)
            {
                StopMiddleScroll();
            }
            else
            {
                StartMiddleScroll(eventArgs.Pointer, eventArgs.GetPosition(DonationContentRoot));
            }
            eventArgs.Handled = true;
            return;
        }

        if (_middleScrollActive &&
            point.Properties.PointerUpdateKind is PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.RightButtonPressed)
        {
            StopMiddleScroll();
        }
    }

    private void DonationHistory_OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_middleScrollActive || !ReferenceEquals(eventArgs.Pointer, _middleScrollPointer))
        {
            return;
        }
        _middleScrollCurrent = eventArgs.GetPosition(DonationContentRoot);
        eventArgs.Handled = true;
    }

    private void DonationHistory_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs)
    {
        if (_middleScrollActive && ReferenceEquals(eventArgs.Pointer, _middleScrollPointer))
        {
            StopMiddleScroll(releasePointer: false);
        }
    }

    private void StartMiddleScroll(IPointer pointer, Point position)
    {
        if (DonationHistoryScrollViewer.Extent.Height <= DonationHistoryScrollViewer.Viewport.Height)
        {
            return;
        }

        StopMiddleScroll();
        _middleScrollActive = true;
        _middleScrollPointer = pointer;
        _middleScrollAnchor = position;
        _middleScrollCurrent = position;
        DonationMiddleScrollIndicator.RenderTransform = new TranslateTransform(
            Math.Clamp(position.X - 16, 0, Math.Max(0, DonationContentRoot.Bounds.Width - 32)),
            Math.Clamp(position.Y - 16, 0, Math.Max(0, DonationContentRoot.Bounds.Height - 32)));
        DonationMiddleScrollIndicator.IsVisible = true;
        pointer.Capture(DonationHistoryScrollViewer);
        _middleScrollTimer.Start();
    }

    private void StopMiddleScroll(bool releasePointer = true)
    {
        var pointer = _middleScrollPointer;
        _middleScrollActive = false;
        _middleScrollPointer = null;
        _middleScrollTimer.Stop();
        DonationMiddleScrollIndicator.IsVisible = false;
        if (releasePointer)
        {
            pointer?.Capture(null);
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

        var maximumOffset = Math.Max(
            0,
            DonationHistoryScrollViewer.Extent.Height - DonationHistoryScrollViewer.Viewport.Height);
        if (maximumOffset <= 0)
        {
            return false;
        }

        var step = Math.CopySign(
            Math.Min(34, 1 + (magnitude * 0.18) + (magnitude * magnitude * 0.003)),
            distance);
        var targetOffset = Math.Clamp(
            DonationHistoryScrollViewer.Offset.Y + step,
            0,
            maximumOffset);
        DonationHistoryScrollViewer.Offset = new Vector(
            DonationHistoryScrollViewer.Offset.X,
            targetOffset);
        return true;
    }

    internal bool ScrollDonationHistoryWithMiddleButtonForTesting(double verticalDistance) =>
        ApplyMiddleScrollDistance(verticalDistance);

    internal static WindowEdge? ResizeEdgeAt(Point point, Size size)
    {
        var west = point.X >= 0 && point.X <= ResizeHitThickness;
        var east = point.X <= size.Width && point.X >= size.Width - ResizeHitThickness;
        var north = point.Y >= 0 && point.Y <= ResizeHitThickness;
        var south = point.Y <= size.Height && point.Y >= size.Height - ResizeHitThickness;

        if (north && west) return WindowEdge.NorthWest;
        if (north && east) return WindowEdge.NorthEast;
        if (south && west) return WindowEdge.SouthWest;
        if (south && east) return WindowEdge.SouthEast;
        if (west) return WindowEdge.West;
        if (east) return WindowEdge.East;
        if (north) return WindowEdge.North;
        if (south) return WindowEdge.South;
        return null;
    }

    private void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        PositionDonationTutorial();
        UpdateDonationTutorialFocus();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MainWindowViewModel.ShowDonationTutorial) or
            nameof(MainWindowViewModel.OnboardingStep) or
            nameof(MainWindowViewModel.ActiveTutorialTopic) or
            nameof(MainWindowViewModel.HasCurrentDonation) or
            nameof(MainWindowViewModel.ShowDonationConnectPanel) or
            nameof(MainWindowViewModel.ShowDonationHistoryPanel))
        {
            Dispatcher.UIThread.Post(PositionDonationTutorial, DispatcherPriority.Loaded);
        }
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.ShowDonationTutorial))
        {
            UpdateDonationTutorialFocus();
        }
    }

    private void UpdateDonationTutorialFocus()
    {
        if (_viewModel?.ShowDonationTutorial == true)
        {
            Dispatcher.UIThread.Post(
                () => DonationTutorialNextButton.Focus(),
                DispatcherPriority.Input);
            return;
        }

        if (IsVisible)
        {
            Dispatcher.UIThread.Post(() => DonationHelpButton.Focus(), DispatcherPriority.Input);
        }
    }

    private void PositionDonationTutorial()
    {
        if (_viewModel?.ShowDonationTutorial != true || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        Control? target = _viewModel.ActiveTutorialTopic == TutorialTopic.DonationsSetup
            ? DonationConnectPanel
            : _viewModel.OnboardingStep switch
            {
                0 => DonationControlStatusCard,
                1 => DonationHistoryHeader,
                2 => (Control?)FindDonationReplayButton() ?? DonationHistoryHeader,
                _ when CurrentDonationActionBar.IsEffectivelyVisible => CurrentDonationActionBar,
                _ when CurrentDonationPresentation.IsEffectivelyVisible => CurrentDonationPresentation,
                _ => DonationHistoryHeader
            };
        var targetRect = GetTutorialTargetRect(target, 5);
        SetDonationTutorialFocusGeometry(targetRect);

        var cardWidth = Math.Min(380, Math.Max(300, Bounds.Width - 32));
        var cardMaxHeight = Math.Max(280, Bounds.Height - 68);
        DonationTutorialCard.Width = cardWidth;
        DonationTutorialCard.MaxWidth = cardWidth;
        DonationTutorialCard.MaxHeight = cardMaxHeight;
        DonationTutorialCard.Measure(new Size(cardWidth, cardMaxHeight));
        var cardHeight = DonationTutorialCard.Bounds.Height > 120
            ? Math.Min(cardMaxHeight, DonationTutorialCard.Bounds.Height)
            : Math.Min(cardMaxHeight, 330);
        var left = Math.Max(16, (Bounds.Width - cardWidth) / 2);
        var top = Math.Max(52, (Bounds.Height - cardHeight) / 2);
        if (targetRect is { } rect)
        {
            const double gap = 14;
            const double edge = 16;
            if (rect.Bottom + gap + cardHeight <= Bounds.Height - edge)
            {
                top = rect.Bottom + gap;
            }
            else if (rect.Top - gap - cardHeight >= 52)
            {
                top = rect.Top - gap - cardHeight;
            }
        }
        DonationTutorialCard.HorizontalAlignment = HorizontalAlignment.Left;
        DonationTutorialCard.VerticalAlignment = VerticalAlignment.Top;
        DonationTutorialCard.Margin = new Thickness(left, top, 0, 0);
    }

    private Button? FindDonationReplayButton() => this.GetVisualDescendants()
        .OfType<Button>()
        .FirstOrDefault(button => string.Equals(
            AutomationProperties.GetAutomationId(button),
            "DonationHistoryReplayButton",
            StringComparison.Ordinal));

    private Rect? GetTutorialTargetRect(Control? target, double padding)
    {
        if (target is null || !target.IsEffectivelyVisible ||
            target.Bounds.Width < 1 || target.Bounds.Height < 1 ||
            target.TranslatePoint(new Point(), this) is not { } origin)
        {
            return null;
        }
        var rect = new Rect(
            origin.X - padding,
            origin.Y - padding,
            target.Bounds.Width + padding * 2,
            target.Bounds.Height + padding * 2);
        var visible = rect.Intersect(new Rect(0, 0, Bounds.Width, Bounds.Height));
        return visible.Width > 0.5 && visible.Height > 0.5 ? visible : null;
    }

    private void SetDonationTutorialFocusGeometry(Rect? targetRect)
    {
        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(1, Bounds.Height);
        var outerPath = $"F0 M0,0 H{FormatGeometryNumber(width)} V{FormatGeometryNumber(height)} H0 Z";
        DonationTutorialDimmingMask.Data = Geometry.Parse(targetRect is { } target
            ? outerPath + " " + RoundedFocusPath(target, 14)
            : outerPath);
        if (targetRect is { } visibleTarget)
        {
            Canvas.SetLeft(DonationTutorialTargetHighlight, visibleTarget.X);
            Canvas.SetTop(DonationTutorialTargetHighlight, visibleTarget.Y);
            DonationTutorialTargetHighlight.Width = visibleTarget.Width;
            DonationTutorialTargetHighlight.Height = visibleTarget.Height;
        }
        else
        {
            DonationTutorialTargetHighlight.Width = 0;
            DonationTutorialTargetHighlight.Height = 0;
        }
    }

    private static string RoundedFocusPath(Rect rect, double requestedRadius)
    {
        var radius = Math.Min(requestedRadius, Math.Min(rect.Width, rect.Height) / 2);
        return $"M{FormatGeometryNumber(rect.Left + radius)},{FormatGeometryNumber(rect.Top)} " +
               $"H{FormatGeometryNumber(rect.Right - radius)} Q{FormatGeometryNumber(rect.Right)},{FormatGeometryNumber(rect.Top)} " +
               $"{FormatGeometryNumber(rect.Right)},{FormatGeometryNumber(rect.Top + radius)} " +
               $"V{FormatGeometryNumber(rect.Bottom - radius)} Q{FormatGeometryNumber(rect.Right)},{FormatGeometryNumber(rect.Bottom)} " +
               $"{FormatGeometryNumber(rect.Right - radius)},{FormatGeometryNumber(rect.Bottom)} " +
               $"H{FormatGeometryNumber(rect.Left + radius)} Q{FormatGeometryNumber(rect.Left)},{FormatGeometryNumber(rect.Bottom)} " +
               $"{FormatGeometryNumber(rect.Left)},{FormatGeometryNumber(rect.Bottom - radius)} " +
               $"V{FormatGeometryNumber(rect.Top + radius)} Q{FormatGeometryNumber(rect.Left)},{FormatGeometryNumber(rect.Top)} " +
               $"{FormatGeometryNumber(rect.Left + radius)},{FormatGeometryNumber(rect.Top)} Z";
    }

    private static string FormatGeometryNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private void DonationTutorialBlocker_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        DonationTutorialNextButton.Focus();
        eventArgs.Handled = true;
    }

    private static void DonationTutorialBlocker_OnPointerWheelChanged(
        object? sender,
        PointerWheelEventArgs eventArgs) => eventArgs.Handled = true;

    private void OnPreviewKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (_viewModel?.ShowDonationTutorial != true)
        {
            if (eventArgs.Key == Key.Escape)
            {
                StopMiddleScroll();
                Hide();
                eventArgs.Handled = true;
            }
            return;
        }

        if (eventArgs.Key == Key.Escape)
        {
            CloseDonationTutorial();
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Source is Visual source &&
            (ReferenceEquals(source, DonationTutorialCard) ||
             source.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, DonationTutorialCard))))
        {
            return;
        }

        DonationTutorialNextButton.Focus();
        eventArgs.Handled = true;
    }

    private void CloseDonationTutorial()
    {
        if (_viewModel?.ShowDonationTutorial == true)
        {
            _viewModel.SkipOnboardingCommand.Execute(null);
        }
    }
}
