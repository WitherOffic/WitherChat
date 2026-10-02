using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 340, "ru", false)]
    [InlineData(280, 340, "ru", true)]
    [InlineData(280, 340, "en", false)]
    [InlineData(280, 340, "en", true)]
    [InlineData(360, 400, "ru", false)]
    [InlineData(360, 400, "ru", true)]
    [InlineData(360, 400, "en", false)]
    [InlineData(360, 400, "en", true)]
    [InlineData(1100, 760, "ru", false)]
    [InlineData(1100, 760, "ru", true)]
    [InlineData(1100, 760, "en", false)]
    [InlineData(1100, 760, "en", true)]
    public async Task WelcomeCardFitsCompactChatAndKeepsConnectionActionReachable(
        int width, int height, string language, bool light)
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        window.MinWidth = 280;
        window.MinHeight = 340;
        window.Width = width;
        window.Height = height;
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        viewModel.ReduceMotion = true;
        viewModel.Language = language;
        viewModel.IsCompactMode = width < 860;
        Assert.True(viewModel.ShowWelcomeState);
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        using var initial = window.CaptureRenderedFrame();
        using var settled = window.CaptureRenderedFrame();

        var card = window.FindControl<Border>("WelcomeCard")!;
        var viewport = window.FindControl<Border>("ChatViewportCard")!;
        var button = window.FindControl<Button>("WelcomeActionButton")!;
        button.BringIntoView();
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        using var final = window.CaptureRenderedFrame();
        var cardOrigin = card.TranslatePoint(default, viewport)!.Value;
        Assert.True(cardOrigin.X >= 0 && cardOrigin.Y >= 0);
        Assert.True(cardOrigin.X + card.Bounds.Width <= viewport.Bounds.Width + 0.5 &&
                    cardOrigin.Y + card.Bounds.Height <= viewport.Bounds.Height + 0.5,
            $"Welcome card is clipped: origin={cardOrigin}, card={card.Bounds}, viewport={viewport.Bounds}");
        Assert.True(button.Bounds.Width > 100 && button.Bounds.Height >= 30);
        var buttonOrigin = button.TranslatePoint(default, window)!.Value;
        Assert.True(buttonOrigin.X >= 0 && buttonOrigin.Y >= 42);
        Assert.True(buttonOrigin.X + button.Bounds.Width <= width + 0.5 &&
                    buttonOrigin.Y + button.Bounds.Height <= height + 0.5);
        if (width >= 860)
        {
            Assert.Equal(560, card.Bounds.Width, precision: 1);
            Assert.Equal(210, button.Bounds.Width, precision: 1);
        }

        SaveAuditFrame(final!, $"welcome-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
        ClickAtCenter(window, button);
        await Task.Delay(20);
        Assert.True(viewModel.IsConnectPanelOpen,
            "The welcome connection action must receive a real pointer click in compact mode.");

        var connect = window.FindControl<Border>("ConnectPanelCard")!;
        var channelInput = window.FindControl<TextBox>("ConnectPanelChannelInput")!;
        var watch = window.FindControl<Button>("ConnectPanelWatchButton")!;
        var connectBack = window.FindControl<Button>("ConnectPanelBackButton")!;
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        using var connectedPanel = window.CaptureRenderedFrame();
        var connectOrigin = connect.TranslatePoint(default, window)!.Value;
        Assert.True(connectOrigin.X >= 0 && connectOrigin.Y >= 0);
        Assert.True(connectOrigin.X + connect.Bounds.Width <= width + 0.5 &&
                    connectOrigin.Y + connect.Bounds.Height <= height + 0.5,
            $"Connection panel is clipped: origin={connectOrigin}, card={connect.Bounds}");

        async Task EnsureConnectControlVisibleAsync(Control control)
        {
            control.BringIntoView();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            using var controlFrame = window.CaptureRenderedFrame();
            var origin = control.TranslatePoint(default, window)!.Value;
            Assert.True(origin.X >= 0 && origin.Y >= 42);
            Assert.True(origin.X + control.Bounds.Width <= width + 0.5 &&
                        origin.Y + control.Bounds.Height <= height + 0.5,
                $"Connection control is clipped: origin={origin}, bounds={control.Bounds}");
        }

        await EnsureConnectControlVisibleAsync(channelInput);
        Assert.True(channelInput.Bounds.Width >= 120, "The complete channel name needs a usable input width.");
        var watchClicked = false;
        // Do not authenticate or connect to a real IRC server in an offline
        // layout regression: retain the production control and pointer routing.
        watch.Command = new RelayCommand(() => watchClicked = true);
        watch.IsEnabled = true;
        await EnsureConnectControlVisibleAsync(watch);
        ClickAtCenter(window, watch);
        Assert.True(watchClicked);
        using var connectFrame = window.CaptureRenderedFrame();
        SaveAuditFrame(connectFrame!, $"connect-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
        await EnsureConnectControlVisibleAsync(connectBack);
        ClickAtCenter(window, connectBack);
        Assert.False(viewModel.IsConnectPanelOpen);
    }
}
