using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 340, "ru", false)]
    [InlineData(280, 340, "ru", true)]
    [InlineData(280, 340, "en", false)]
    [InlineData(280, 340, "en", true)]
    [InlineData(1100, 760, "ru", false)]
    [InlineData(1100, 760, "ru", true)]
    [InlineData(1100, 760, "en", false)]
    [InlineData(1100, 760, "en", true)]
    public async Task DonationControlWarningWrapsInsideAccountSettings(
        int width, int height, string language, bool light)
    {
        await using var fixture = new WindowFixture();
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        fixture.ViewModel.IsSettingsOpen = true;
        fixture.ViewModel.SelectedSettingsSection = SettingsSection.Account;
        fixture.Window.Show();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        var card = fixture.Window.FindControl<Border>("AccountSettingsCard")!;
        var warning = Assert.Single(card.GetLogicalDescendants().OfType<TextBlock>(),
            block => block.Text == fixture.ViewModel.DonationAlertsObsControlStatus);
        Assert.True(warning.Bounds.Width > 0);
        Assert.True(warning.Bounds.Width <= card.Bounds.Width - card.Padding.Left - card.Padding.Right,
            $"Warning overflows settings: warning={warning.Bounds}, card={card.Bounds}");
        Assert.True(warning.Bounds.Height > warning.FontSize * 1.5,
            "The long status must wrap rather than disappear beyond the right edge.");
        using var frame = fixture.Window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, $"account-warning-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
    }
}
