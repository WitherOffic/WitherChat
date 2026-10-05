using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
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
    public async Task ContextTutorialRemainsReadableAndOperableInCompactWindows(
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
        window.Show();
        viewModel.IsModerationPanelOpen = true;
        viewModel.StartContextTutorialCommand.Execute("Moderation");

        async Task SettleAsync()
        {
            await Task.Delay(160);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            using var settled = window.CaptureRenderedFrame();
        }

        var card = window.FindControl<Border>("OnboardingCard")!;
        var scroll = window.FindControl<ScrollViewer>("OnboardingContentScrollViewer")!;
        var next = window.FindControl<Button>("OnboardingNextButton")!;
        var back = window.FindControl<Button>("OnboardingBackButton")!;
        var close = window.FindControl<Button>("OnboardingSkipButton")!;

        void AssertWithinWindow(Control control)
        {
            var origin = control.TranslatePoint(default, window)!.Value;
            Assert.True(origin.X >= -0.5 && origin.Y >= 42,
                $"Control starts outside content area: {origin}");
            Assert.True(origin.X + control.Bounds.Width <= width + 0.5 &&
                        origin.Y + control.Bounds.Height <= height + 0.5,
                $"Control is clipped: origin={origin}, bounds={control.Bounds}, viewport={width}x{height}");
        }

        async Task ClickReachableAsync(Button button)
        {
            button.BringIntoView();
            await SettleAsync();
            AssertWithinWindow(button);
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2,
                button.Bounds.Height / 2), window)!.Value;
            var footer = window.FindControl<StackPanel>("OnboardingFixedFooter")!;
            var footerOrigin = footer.TranslatePoint(default, window)!.Value;
            Assert.True(point.Y >= footerOrigin.Y && point.Y <= footerOrigin.Y + footer.Bounds.Height);
            ClickAtCenter(window, button);
            await SettleAsync();
        }

        await SettleAsync();
        AssertWithinWindow(card);
        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        if (width < 860)
        {
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                "Short tutorial windows must expose all content through scrolling.");
        }

        await ClickReachableAsync(next);
        Assert.Equal(1, viewModel.OnboardingStep);
        AssertWithinWindow(card);
        await ClickReachableAsync(back);
        Assert.Equal(0, viewModel.OnboardingStep);
        AssertWithinWindow(card);

        // Verify every later, differently sized translated step, not only the
        // first page. Real pointer clicks also detect input-blocker regressions.
        while (viewModel.OnboardingStep < viewModel.OnboardingTotalSteps - 1)
        {
            var previousStep = viewModel.OnboardingStep;
            await ClickReachableAsync(next);
            Assert.Equal(previousStep + 1, viewModel.OnboardingStep);
            AssertWithinWindow(card);
        }

        close.BringIntoView();
        await SettleAsync();
        using (var frame = window.CaptureRenderedFrame())
        {
            SaveAuditFrame(frame!, $"context-moderation-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
        }
        await ClickReachableAsync(close);
        Assert.False(viewModel.IsOnboardingOpen);
        Assert.True(viewModel.IsModerationPanelOpen);
    }
}
