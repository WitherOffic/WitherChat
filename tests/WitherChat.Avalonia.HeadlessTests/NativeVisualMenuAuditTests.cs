using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeVisualAuditMoreToolsMenuDoesNotShowChatThroughItsBackground(bool light, bool reduceMotion)
    {
        await using var fixture = new WindowFixture();
        ConfigureAuxiliaryAuditWindow(fixture, 1100, 760, "ru", light);
        var vm = fixture.ViewModel;
        vm.Channel = "audit";
        vm.ReduceMotion = reduceMotion;
        fixture.Window.Show();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        var button = AssertControl<Button>(fixture.Window, "HeaderMoreToolsButton");
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(220);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        var card = Assert.IsType<Border>(flyout.Content);
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(card.Background);
        Assert.Equal((byte)255, brush.Color.A);
        Assert.Equal(1, brush.Opacity);
        Assert.Equal(Color.Parse(light ? "#F7F8FC" : "#131824"), brush.Color);
        Assert.Equal(1, card.Opacity);
        Assert.Equal(new CornerRadius(16), card.CornerRadius);
        Assert.Equal(new Thickness(1), card.BorderThickness);
        if (reduceMotion)
            Assert.Empty(card.Transitions!);
        else
            Assert.Equal(2, card.Transitions!.Count);
        if (TopLevel.GetTopLevel(card) is { } popup)
        {
            using var frame = popup.CaptureRenderedFrame();
            SaveAuditFrame(frame, $"more-tools-{(light ? "light" : "dark")}-motion-{!reduceMotion}.png");
        }
        fixture.Window.RequestedThemeVariant = light ? ThemeVariant.Dark : ThemeVariant.Light;
        vm.Theme = light ? "Dark" : "Light";
        Dispatcher.UIThread.RunJobs();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        var changedBrush = Assert.IsAssignableFrom<ISolidColorBrush>(card.Background);
        Assert.Equal(Color.Parse(light ? "#131824" : "#F7F8FC"), changedBrush.Color);
        Assert.Equal(1, changedBrush.Opacity);
        flyout.Hide();
    }
}
