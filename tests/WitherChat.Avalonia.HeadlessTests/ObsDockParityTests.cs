using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using WitherChat.Core.Models;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.Views;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData("ru", false, false)]
    [InlineData("ru", true, false)]
    [InlineData("en", false, false)]
    [InlineData("en", true, false)]
    [InlineData("ru", false, true)]
    [InlineData("ru", true, true)]
    [InlineData("en", false, true)]
    [InlineData("en", true, true)]
    public async Task ObsParityDonationsKeepOriginalCommandsAndTutorial(string language, bool light, bool connected)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        vm.Language = language;
        vm.ReduceMotion = true;
        if (connected) ApplyDonationAlertsSessionForTesting(vm);
        var window = new DonationAlertsWindow { DataContext = vm,
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        var backRequests = 0;
        window.ObsDockBackToChatRequested += (_, _) => backRequests++;
        try
        {
            window.ConfigureObsDockLayout(true);
            window.Show();
            foreach (var (width, height) in new[] { (280, 280), (360, 400), (520, 620), (900, 760) })
            {
                window.Width = width; window.Height = height;
                await SettleAuxiliaryAuditAsync(window);
                window.UpdateObsDockLayout();
                await SettleAuxiliaryAuditAsync(window);
                Assert.False(window.ShowInTaskbar);
                Assert.False(window.Topmost);
                Assert.False(window.CanResize);
                var help = AssertControl<Button>(window, "DonationHelpButton");
                AssertObsParityInside(help, window, width, height);
                Assert.False(AssertControl<Button>(window, "DonationWindowCloseButton").IsVisible);
                Assert.False(AssertControl<Button>(window, "DonationWindowMinimizeButton").IsVisible);
                Assert.Same(vm.SkipDonationCommand, AssertControl<Button>(window, "SkipDonationButton").Command);
                using (var frame = window.CaptureRenderedFrame())
                    SaveAuditFrame(frame!, $"obs-r2-donations-{language}-{light}-{connected}-{width}x{height}.png");
                ClickAtCenter(window, help);
                await SettleAuxiliaryAuditAsync(window);
                Assert.True(vm.ShowDonationTutorial);
                AssertObsParityInside(window.FindControl<Border>("DonationTutorialCard")!, window, width, height);
                var next = AssertControl<Button>(window, "DonationTutorialNextButton");
                next.BringIntoView();
                await SettleAuxiliaryAuditAsync(window);
                AssertObsParityInside(next, window, width, height);
                Assert.True(next.IsFocused);
                using (var frame = window.CaptureRenderedFrame())
                    SaveAuditFrame(frame!, $"obs-r2-donation-guide-{language}-{light}-{connected}-{width}x{height}.png");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(vm.ShowDonationTutorial);
                Assert.True(window.IsVisible);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.True(window.IsVisible);
            }
            Assert.Equal(4, backRequests);
            window.ConfigureObsDockLayout(false);
            Assert.Equal(420, window.MinWidth);
            Assert.Equal(420, window.MinHeight);
            Assert.True(window.ShowInTaskbar);
            Assert.True(window.CanResize);
            Assert.True(window.Topmost);
        }
        finally { vm.SkipOnboardingCommand.Execute(null); window.DataContext = null; window.CloseForApplicationExit(); }
    }

    [AvaloniaTheory]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public async Task ObsParityAllAuxiliaryPanelsStayInsideDock(string language, bool light)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var window = fixture.Window;
        vm.Language = language;
        vm.ReduceMotion = true;
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        window.ConfigureObsDockLayout(true);
        window.Show();
        var panels = new Dictionary<string, string>
        {
            ["IsSettingsOpen"] = "SettingsCard", ["IsConnectPanelOpen"] = "ConnectPanelCard",
            ["IsChannelEditorOpen"] = "ChannelEditorCard", ["IsLogViewerOpen"] = "LogViewerCard",
            ["IsStreamEventsOpen"] = "StreamEventsCard", ["IsProtectionPanelOpen"] = "ProtectionPanelCard",
            ["IsMomentsPanelOpen"] = "MomentsPanelCard", ["IsModerationPanelOpen"] = "ModerationPanelCard",
            ["IsRecentMessagesOpen"] = "RecentMessagesCard", ["IsModerationDialogOpen"] = "ModerationDialogCard"
        };
        foreach (var (width, height) in new[] { (280, 280), (360, 400), (600, 600), (1100, 760) })
        {
            window.Width = width; window.Height = height;
            await SettleAuxiliaryAuditAsync(window);
            window.UpdateObsDockLayout();
            foreach (var (property, name) in panels)
            {
                var flag = vm.GetType().GetProperty(property)!;
                flag.SetValue(vm, true);
                await SettleAuxiliaryAuditAsync(window);
                var card = window.FindControl<Border>(name)!;
                Assert.True(card.IsVisible);
                AssertObsParityInside(card, window, width, height);
                using var frame = window.CaptureRenderedFrame();
                SaveAuditFrame(frame!, $"obs-r2-{name}-{language}-{light}-{width}x{height}.png");
                flag.SetValue(vm, false);
                await SettleAuxiliaryAuditAsync(window);
            }
        }
        window.ConfigureObsDockLayout(false);
        Assert.Same(vm, window.DataContext);
    }

    [AvaloniaTheory]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public async Task ObsParityCriticalActionsRemainReachableInSmallDock(string language, bool light)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel; var window = fixture.Window;
        vm.Language = language; vm.ReduceMotion = true;
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        window.ConfigureObsDockLayout(true); window.Show();
        foreach (var (width, height) in new[] { (280, 280), (360, 400), (600, 600) })
        {
            window.Width = width; window.Height = height;
            await SettleAuxiliaryAuditAsync(window);
            vm.IsConnectPanelOpen = true;
            await SettleAuxiliaryAuditAsync(window);
            var connectScroll = window.FindControl<ScrollViewer>("ConnectBodyScrollViewer")!;
            Assert.True(connectScroll.Viewport.Height >= 50, $"Connection body inaccessible: {connectScroll.Viewport}");
            var signIn = AssertControl<Button>(window, "ConnectPanelSignIn");
            signIn.BringIntoView(); await SettleAuxiliaryAuditAsync(window);
            Assert.True(signIn.Bounds.Width >= 100);
            Assert.Same(vm.SignInFromConnectPanelCommand, signIn.Command);
            vm.IsConnectPanelOpen = false; vm.IsLogViewerOpen = true;
            await SettleAuxiliaryAuditAsync(window);
            AssertObsParityInside(AssertControl<Button>(window, "CloseLogsButton"), window, width, height);
            AssertObsParityInside(AssertControl<Button>(window, "OpenLogFolderButton"), window, width, height);
            if (width < 600)
            {
                var content = window.FindControl<Border>("LogViewerContent")!;
                content.BringIntoView(); await SettleAuxiliaryAuditAsync(window);
                Assert.True(content.Bounds.Width > 150 && content.Bounds.Width < width);
                foreach (var input in window.FindControl<Grid>("LogViewerFilters")!.Children)
                    Assert.True(input.Bounds.Width > 100 && input.Bounds.Width < width);
            }
            vm.IsLogViewerOpen = false; vm.IsProtectionPanelOpen = true;
            await SettleAuxiliaryAuditAsync(window);
            var protectionScroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(
                control => AutomationProperties.GetAutomationId(control) == "ProtectionPanelScrollViewer");
            Assert.True(protectionScroll.Viewport.Height >= 50);
            var protectionCard = window.FindControl<Border>("ProtectionTwitchSettingsCard")!;
            Assert.True(protectionCard.Bounds.Width >= 150);
            if (width < 600) Assert.True(protectionCard.Bounds.Width < width);
            vm.IsProtectionPanelOpen = false; vm.IsStreamEventsOpen = true;
            await SettleAuxiliaryAuditAsync(window);
            foreach (var filter in window.FindControl<WrapPanel>("StreamEventFiltersPanel")!.Children)
                AssertObsParityInside(filter, window, width, height);
            vm.IsStreamEventsOpen = false;
        }
        window.ConfigureObsDockLayout(false);
        Assert.Equal(6, window.FindControl<Grid>("LogViewerHeader")!.ColumnDefinitions.Count);
        Assert.Equal(3, window.FindControl<Grid>("LogViewerBodyGrid")!.ColumnDefinitions.Count);
    }

    private static void AssertObsParityInside(Control control, Window window, int width, int height)
    {
        var point = control.TranslatePoint(default, window)!.Value;
        Assert.True(point.X >= 0 && point.Y >= 0 &&
            point.X + control.Bounds.Width <= width + .5 && point.Y + control.Bounds.Height <= height + .5,
            $"Dock overflow: {control.Name} {point} {control.Bounds} in {width}x{height}");
    }
}

public sealed class ObsDockDonationProtocolTests
{
    [Theory]
    [InlineData("ATTACH_DONATIONS 123 456", true)]
    [InlineData("ATTACH_DONATIONS 0 456", false)]
    [InlineData("ATTACH_DONATIONS 123 0", false)]
    [InlineData("ATTACH_DONATIONS 123 456 extra", false)]
    public void DonationDockProtocolIsBoundedAndRoundTrips(string command, bool valid)
    {
        Assert.Equal(valid, ObsDockRequest.TryParse(command, out var request));
        if (valid) { Assert.True(request.Donations); Assert.True(request.Attach); Assert.Equal(command, request.ToString()); }
    }
}
