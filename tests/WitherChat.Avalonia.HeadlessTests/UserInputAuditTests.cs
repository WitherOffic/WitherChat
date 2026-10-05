using System.Globalization;
using Avalonia.Headless.XUnit;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData("++12", "ru-RU")]
    [InlineData("--12", "ru-RU")]
    [InlineData("+-12", "ru-RU")]
    [InlineData("-+12", "ru-RU")]
    [InlineData("１２", "ru-RU")]
    [InlineData("١٢", "ru-RU")]
    [InlineData("++12", "en-US")]
    [InlineData("--12", "en-US")]
    [InlineData("+-12", "en-US")]
    [InlineData("-+12", "en-US")]
    [InlineData("１２", "en-US")]
    [InlineData("١٢", "en-US")]
    public async Task UserInputAuditMalformedIntegersDoNotChangeSettings(string text, string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await using var fixture = new WindowFixture();
            var vm = fixture.ViewModel;
            var expected = (vm.MessageLimit, vm.ViewerCountRefreshIntervalSeconds, vm.OverlayPort,
                vm.OverlayMaxMessages, vm.OverlayFadeOutSeconds);
            vm.MessageLimitText = text;
            vm.ViewerCountRefreshIntervalText = text;
            vm.OverlayPortText = text;
            vm.OverlayMaxMessagesText = text;
            vm.OverlayFadeOutSecondsText = text;
            Assert.Equal(expected, (vm.MessageLimit, vm.ViewerCountRefreshIntervalSeconds, vm.OverlayPort,
                vm.OverlayMaxMessages, vm.OverlayFadeOutSeconds));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("++12")]
    [InlineData("--12")]
    [InlineData("not a number")]
    public async Task UserInputAuditMalformedFloatingPointInputsKeepFiniteSettings(string text)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var expected = (vm.ChatFontSize, vm.OverlayFontSize, vm.OverlayBackgroundOpacity);
        vm.ChatFontSizeText = text;
        vm.OverlayFontSizeText = text;
        vm.OverlayBackgroundOpacityText = text;
        Assert.Equal(expected, (vm.ChatFontSize, vm.OverlayFontSize, vm.OverlayBackgroundOpacity));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserInputAuditValidOverflowIsClampedWithoutThrowing(bool negative)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var text = (negative ? "-" : "+") + new string('9', 200);
        vm.MessageLimitText = text;
        vm.OverlayPortText = text;
        vm.ChatFontSizeText = text;
        Assert.Equal(negative ? 250 : 10000, vm.MessageLimit);
        Assert.Equal(negative ? 1024 : 65535, vm.OverlayPort);
        Assert.Equal(negative ? 11 : 28, vm.ChatFontSize);
    }
}
