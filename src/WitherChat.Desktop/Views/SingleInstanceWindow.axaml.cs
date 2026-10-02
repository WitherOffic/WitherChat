using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.Views;

public partial class SingleInstanceWindow : Window
{
    private bool _closing;
    private readonly UiText _texts;

    public SingleInstanceWindow()
        : this("ru")
    {
    }

    public SingleInstanceWindow(string language)
    {
        InitializeComponent();
        _texts = new UiText();
        _texts.SetLanguage(language);
        MessageText.Text = _texts.AlreadyRunning;
        DismissButton.Content = _texts.Close;
        ShowChatButton.Content = _texts.ShowChatWindow;
        AutomationProperties.SetName(TrafficCloseButton, _texts.Close);
        AutomationProperties.SetName(CaptionCloseButton, _texts.Close);
        ToolTip.SetTip(TrafficCloseButton, _texts.Close);
        ToolTip.SetTip(CaptionCloseButton, _texts.Close);
        Opened += OnOpened;
        KeyDown += OnKeyDown;
    }

    private void OnOpened(object? sender, EventArgs eventArgs)
    {
        WindowSurface.Opacity = 0;
        WindowSurface.RenderTransform = TransformOperations.Parse("translate(0px, 10px) scale(0.985)");
        Dispatcher.UIThread.Post(() =>
        {
            WindowSurface.Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new CubicEaseOut()
                },
                new TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(240),
                    Easing = new CubicEaseOut()
                }
            ];
            WindowSurface.Opacity = 1;
            WindowSurface.RenderTransform = TransformOperations.Identity;
            Activate();
            ShowChatButton.Focus();
        }, DispatcherPriority.Render);
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(eventArgs);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            _ = ShowExistingChatAsync();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            CloseWithAnimation();
            eventArgs.Handled = true;
        }
    }

    private async void ShowChatButton_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await ShowExistingChatAsync();

    private void CloseButton_OnClick(object? sender, RoutedEventArgs eventArgs) => CloseWithAnimation();

    private async Task ShowExistingChatAsync()
    {
        if (_closing || !ShowChatButton.IsEnabled)
        {
            return;
        }

        ShowChatButton.IsEnabled = false;
        var activated = await SingleInstanceActivationService.RequestShowWindowAsync();
        if (activated)
        {
            CloseWithAnimation();
            return;
        }

        MessageText.Text = _texts.ShowChatWindowFailed;
        ShowChatButton.IsEnabled = true;
        ShowChatButton.Focus();
    }

    private void CloseWithAnimation()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        DismissButton.IsEnabled = false;
        ShowChatButton.IsEnabled = false;
        WindowSurface.Opacity = 0;
        WindowSurface.RenderTransform = TransformOperations.Parse("translate(0px, 7px) scale(0.99)");
        DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(180));
    }
}
