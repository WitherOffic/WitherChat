using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using WitherChat.Desktop.Views;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(false, false, 280, 340)]
    [InlineData(false, true, 280, 340)]
    [InlineData(false, false, 1100, 760)]
    [InlineData(false, true, 1100, 760)]
    [InlineData(true, false, 280, 340)]
    [InlineData(true, true, 280, 340)]
    [InlineData(true, false, 520, 620)]
    [InlineData(true, true, 520, 620)]
    public async Task TutorialCardsDoNotShowUnderlyingTextThroughTheirGradient(
        bool donationWindow, bool light, int width, int height)
    {
        await using var fixture = new WindowFixture();
        ConfigureAuxiliaryAuditWindow(fixture, width, height, "ru", light);
        var vm = fixture.ViewModel;
        Window window = fixture.Window;
        if (donationWindow)
        {
            ApplyDonationAlertsSessionForTesting(vm);
            window = new DonationAlertsWindow
            {
                DataContext = vm, MinWidth = 280, MinHeight = 340, Width = width, Height = height,
                RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark
            };
        }
        try
        {
            window.Show();
            vm.StartContextTutorialCommand.Execute(donationWindow ? "Donations" : "Moderation");
            await SettleAuxiliaryAuditAsync(window);
            var card = AssertControl<Border>(window,
                donationWindow ? "DonationTutorialCard" : "OnboardingCard");
            Assert.True(card.IsEffectivelyVisible);
            AssertOpaqueTutorialGradient(card, light);
            Assert.Equal(new CornerRadius(donationWindow ? 22 : 26), card.CornerRadius);
            using (var frame = window.CaptureRenderedFrame())
                SaveAuditFrame(frame, $"opaque-tutorial-{(donationWindow ? "donations" : "main")}-{(light ? "light" : "dark")}-{width}x{height}.png");

            window.RequestedThemeVariant = light ? ThemeVariant.Dark : ThemeVariant.Light;
            await SettleAuxiliaryAuditAsync(window);
            AssertOpaqueTutorialGradient(card, !light);
            vm.SkipOnboardingCommand.Execute(null);
            Assert.False(vm.IsOnboardingOpen);
        }
        finally
        {
            if (window is DonationAlertsWindow donations)
                donations.CloseForApplicationExit();
        }
    }

    private static void AssertOpaqueTutorialGradient(Border card, bool light)
    {
        var brush = Assert.IsAssignableFrom<IGradientBrush>(card.Background);
        Assert.Equal(1, brush.Opacity);
        Assert.Equal(3, brush.GradientStops.Count);
        Assert.All(brush.GradientStops, stop => Assert.Equal((byte)255, stop.Color.A));
        Assert.Equal(new[] { 0d, .52d, 1d }, brush.GradientStops.Select(stop => stop.Offset));
        Assert.Equal(
            (light ? new[] { "#FFFFFF", "#F6F8FF", "#F5FBFF" }
                   : new[] { "#151929", "#121524", "#101725" }).Select(Color.Parse),
            brush.GradientStops.Select(stop => stop.Color));
    }
}
