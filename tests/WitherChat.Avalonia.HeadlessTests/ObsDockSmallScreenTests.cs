using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using WitherChat.Core.Services;
using WitherChat.Core.Models;
using WitherChat.Desktop.ViewModels;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 280, "ru", false)] [InlineData(280, 280, "ru", true)]
    [InlineData(280, 280, "en", false)] [InlineData(280, 280, "en", true)]
    [InlineData(360, 320, "ru", false)] [InlineData(360, 320, "ru", true)]
    [InlineData(360, 320, "en", false)] [InlineData(360, 320, "en", true)]
    [InlineData(620, 320, "ru", false)] [InlineData(620, 320, "ru", true)]
    [InlineData(620, 320, "en", false)] [InlineData(620, 320, "en", true)]
    [InlineData(1100, 600, "ru", false)] [InlineData(1100, 600, "ru", true)]
    [InlineData(1100, 600, "en", false)] [InlineData(1100, 600, "en", true)]
    public async Task ObsSmallScreenKeepsOneMenuIdentityAndComposer(int width, int height, string language, bool light)
    {
        await using var fixture = new WindowFixture(new RecordingChatClient());
        var vm = fixture.ViewModel; var window = fixture.Window;
        vm.Language = language; vm.Theme = light ? "Light" : "Dark";
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        ApplyObsScreenTwitchFixture(vm);
        vm.Channel = "very_long_target_channel";
        vm.ComposerText = "Draft survives menu and resizing";
        window.ConfigureObsDockLayout(true);
        window.Width = width; window.Height = height; window.Show();
        await SettleAuxiliaryAuditAsync(window);
        var toolbar = window.FindControl<Border>("ObsDockToolbar")!;
        Assert.True(toolbar.IsVisible);
        Assert.Equal(36, toolbar.Bounds.Height);
        Assert.False(window.FindControl<Border>("HeaderPanel")!.IsVisible);
        Assert.False(vm.ShowHeaderToggle);
        Assert.False(vm.ShowComposerToggle);
        var menuButton = Assert.Single(toolbar.GetVisualDescendants().OfType<Button>());
        var account = window.FindControl<TextBlock>("ObsDockAccountText")!;
        var channel = window.FindControl<TextBlock>("ObsDockChannelText")!;
        Assert.Equal("Twitch: @audit_owner", account.Text);
        Assert.Contains("@very_long_target_channel", channel.Text);
        AssertObsDockInside(account, window, width, height);
        AssertObsDockInside(channel, window, width, height);
        Assert.True(account.TranslatePoint(default, toolbar)!.Value.X + account.Bounds.Width
            <= menuButton.TranslatePoint(default, toolbar)!.Value.X);
        var input = window.FindControl<TextBox>("ComposerTextBox")!;
        Assert.True(input.IsEffectivelyVisible && input.IsEnabled);
        Assert.True(input.Bounds.Height <= 44, $"Oversize dock composer: {input.Bounds}");
        AssertObsDockInside(input, window, width, height);
        var send = window.GetVisualDescendants().OfType<Button>().Single(
            b => AutomationProperties.GetAutomationId(b) == "SendMessageButton");
        Assert.Equal(32, send.Bounds.Width);
        AssertObsDockInside(send, window, width, height);
        using (var frame = window.CaptureRenderedFrame())
            SaveAuditFrame(frame!, $"r9-dock-{language}-{light}-{width}x{height}.png");

        ClickAtCenter(window, menuButton);
        await SettleAuxiliaryAuditAsync(window);
        var flyout = Assert.IsType<Flyout>(menuButton.Flyout);
        Assert.True(flyout.IsOpen);
        var menu = Assert.IsType<Border>(flyout.Content);
        Assert.True(menu.Bounds.Width <= width - 16 + .5);
        Assert.True(menu.Bounds.Height <= height - 48 + .5);
        var rows = menu.GetLogicalDescendants().OfType<Button>().ToArray();
        Assert.Equal(13, rows.Length);
        Assert.All(rows, row => Assert.NotNull(row.Command));
        var scroll = menu.GetLogicalDescendants().OfType<ScrollViewer>().Single();
        Assert.True(scroll.Viewport.Width > 0 && scroll.Viewport.Height > 0);
        using (var frame = RenderPanelForParity(menu))
            SaveAuditFrame(frame, $"r9-menu-top-{language}-{light}-{width}x{height}.png");
        if (height <= 320)
        {
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            await SettleAuxiliaryAuditAsync(window);
            Assert.True(scroll.Offset.Y > 0);
            using var bottom = RenderPanelForParity(menu);
            SaveAuditFrame(bottom, $"r9-menu-bottom-{language}-{light}-{width}x{height}.png");
        }
        // Invoke the actual Button click/command sequence, not just the Click event.
        var settings = rows.Single(b => AutomationProperties.GetAutomationId(b) == "ObsDockSettingsButton");
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, null);
        await SettleAuxiliaryAuditAsync(window);
        Assert.True(vm.IsSettingsOpen);
        Assert.False(flyout.IsOpen);
        Assert.Equal("Draft survives menu and resizing", vm.ComposerText);
        AssertObsDockInside(window.FindControl<Border>("SettingsCard")!, window, width, height);
        vm.IsSettingsOpen = false;
        window.ConfigureObsDockLayout(false);
        Assert.False(toolbar.IsVisible);
        Assert.Equal(10, window.FindControl<Grid>("ChatLayoutRoot")!.RowDefinitions[3].Height.Value);
        Assert.Equal(56, input.MinHeight);
        Assert.Equal(46, send.Width);
        Assert.True(vm.ShowHeaderPanel);
    }

    [AvaloniaTheory]
    [InlineData("ru")] [InlineData("en")]
    public async Task ObsSmallScreenIdentityReactsToBothAccountsChannelAndLanguage(string language)
    {
        await using var fixture = new WindowFixture(new RecordingChatClient());
        var vm = fixture.ViewModel; var window = fixture.Window;
        window.ConfigureObsDockLayout(true);
        window.Width = 280; window.Height = 280; window.Show();
        await SettleAuxiliaryAuditAsync(window);
        var account = window.FindControl<TextBlock>("ObsDockAccountText")!;
        var channel = window.FindControl<TextBlock>("ObsDockChannelText")!;
        Assert.Equal(vm.Texts.Guest, account.Text);
        vm.Channel = "watched";
        await SettleAuxiliaryAuditAsync(window);
        Assert.Contains("@watched", channel.Text);
        Assert.Equal(vm.Texts.Guest, account.Text); // watching is not authenticating
        ApplyObsScreenTwitchFixture(vm);
        await SettleAuxiliaryAuditAsync(window);
        Assert.Equal("Twitch: @audit_owner", account.Text);
        var youtube = new YouTubeAuthSession("fixture", "", "fixture-client", "fixture-channel",
            "YouTube Viewer With A Long Display Name", "@fixture", null, [],
            DateTimeOffset.UtcNow.AddHours(1));
        typeof(MainWindowViewModel).GetMethod("ApplyYouTubeSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [youtube]);
        vm.Language = language;
        vm.IsYouTubeLiveConnected = true;
        vm.YouTubeBroadcastTitle = "A long YouTube broadcast title";
        await SettleAuxiliaryAuditAsync(window);
        Assert.Contains("Twitch: @audit_owner", account.Text);
        Assert.Contains("YouTube Viewer", account.Text);
        Assert.Contains("A long YouTube broadcast title", channel.Text);
        vm.Channel = "changed";
        vm.Language = language == "ru" ? "en" : "ru";
        await SettleAuxiliaryAuditAsync(window);
        Assert.Contains("@changed", channel.Text);
        Assert.Contains(vm.Texts.CurrentChannel, channel.Text);
        AssertObsDockInside(account, window, 280, 280);
    }

    [AvaloniaTheory]
    [InlineData("ru", false)] [InlineData("ru", true)]
    [InlineData("en", false)] [InlineData("en", true)]
    public async Task ObsSmallScreenStandaloneReference(string language, bool light)
    {
        await using var fixture = new WindowFixture(new RecordingChatClient());
        var vm = fixture.ViewModel; var window = fixture.Window;
        vm.Language = language; vm.Theme = light ? "Light" : "Dark";
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        ApplyObsScreenTwitchFixture(vm);
        vm.Channel = "audit"; vm.ComposerText = "Standalone draft";
        window.Show();
        foreach (var (width, height, compact) in new[] { (1100, 760, false), (860, 560, false), (360, 400, true) })
        {
            vm.IsCompactMode = compact;
            window.MinWidth = compact ? 280 : 860;
            window.MinHeight = compact ? 340 : 560;
            window.Width = width; window.Height = height;
            await SettleAuxiliaryAuditAsync(window);
            Assert.False(window.FindControl<Border>("ObsDockToolbar")!.IsVisible);
            Assert.Equal(!compact, vm.ShowHeaderPanel);
            Assert.Equal(56, window.FindControl<TextBox>("ComposerTextBox")!.MinHeight);
            using (var frame = window.CaptureRenderedFrame())
                SaveAuditFrame(frame!, $"r9-standalone-{language}-{light}-{width}x{height}.png");
            vm.IsFiltersVisible = true;
            await SettleAuxiliaryAuditAsync(window);
            using (var filters = window.CaptureRenderedFrame())
                SaveAuditFrame(filters!, $"r9-standalone-filters-{language}-{light}-{width}x{height}.png");
            vm.IsFiltersVisible = false;
        }
    }

    [AvaloniaFact]
    public async Task ObsSmallScreenMenuResizesAndClosesWhenReturningToDesktop()
    {
        await using var fixture = new WindowFixture(new RecordingChatClient());
        var window = fixture.Window;
        window.ConfigureObsDockLayout(true);
        window.Width = 620; window.Height = 600; window.Show();
        await SettleAuxiliaryAuditAsync(window);
        var button = window.FindControl<Button>("ObsDockMoreButton")!;
        ClickAtCenter(window, button);
        await SettleAuxiliaryAuditAsync(window);
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        Assert.True(flyout.IsOpen);
        window.Width = 280; window.Height = 280;
        await SettleAuxiliaryAuditAsync(window);
        var menu = Assert.IsType<Border>(flyout.Content);
        Assert.True(menu.Bounds.Width <= 264.5);
        Assert.True(menu.Bounds.Height <= 232.5);
        window.ConfigureObsDockLayout(false);
        await SettleAuxiliaryAuditAsync(window);
        Assert.False(flyout.IsOpen);
        Assert.False(window.FindControl<Border>("ObsDockToolbar")!.IsVisible);
        Assert.True(window.FindControl<Border>("TitleBarPanel")!.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData("ru", false)] [InlineData("ru", true)]
    [InlineData("en", false)] [InlineData("en", true)]
    public async Task ObsSmallScreenGuestWelcomeFitsWithoutScrolling(string language, bool light)
    {
        await using var fixture = new WindowFixture(new RecordingChatClient());
        var window = fixture.Window; var vm = fixture.ViewModel;
        vm.Language = language; vm.Theme = light ? "Light" : "Dark";
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        window.ConfigureObsDockLayout(true);
        window.Width = 280; window.Height = 280; window.Show();
        await SettleAuxiliaryAuditAsync(window);
        var card = window.FindControl<Border>("WelcomeCard")!;
        Assert.True(card.IsEffectivelyVisible);
        Assert.Equal(new Thickness(14), card.Padding);
        AssertObsDockInside(card, window, 280, 280);
        var scroll = window.FindControl<ScrollViewer>("WelcomeContentScrollViewer")!;
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + .5,
            $"Guest welcome needs scrolling: {scroll.Extent}/{scroll.Viewport}");
        AssertObsDockInside(window.FindControl<Button>("WelcomeActionButton")!, window, 280, 280);
        using var frame = window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, $"r9-guest-{language}-{light}-280x280.png");
    }


    [AvaloniaTheory]
    [InlineData(280, 280, "ru", false)] [InlineData(280, 280, "ru", true)]
    [InlineData(280, 280, "en", false)] [InlineData(280, 280, "en", true)]
    [InlineData(620, 320, "ru", false)] [InlineData(620, 320, "ru", true)]
    [InlineData(620, 320, "en", false)] [InlineData(620, 320, "en", true)]
    public async Task ObsSmallScreenFiltersLeaveReadableChat(int width, int height, string language, bool light)
    {
        await using var fixture = new WindowFixture(new RecordingChatClient());
        var window = fixture.Window; var vm = fixture.ViewModel;
        vm.Language = language; vm.Theme = light ? "Light" : "Dark";
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        ApplyObsScreenTwitchFixture(vm);
        vm.Channel = "audit";
        window.ConfigureObsDockLayout(true);
        window.Width = width; window.Height = height; window.Show();
        await SettleAuxiliaryAuditAsync(window);
        var more = window.FindControl<Button>("ObsDockMoreButton")!;
        ClickAtCenter(window, more);
        await SettleAuxiliaryAuditAsync(window);
        var flyout = Assert.IsType<Flyout>(more.Flyout);
        var row = Assert.IsType<Border>(flyout.Content).GetLogicalDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetAutomationId(b) == "ObsDockFiltersButton");
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(row, null);
        await SettleAuxiliaryAuditAsync(window);
        Assert.True(vm.IsFiltersVisible);
        Assert.False(flyout.IsOpen);
        using (var frame = window.CaptureRenderedFrame())
            SaveAuditFrame(frame!, $"r9-filters-{language}-{light}-{width}x{height}.png");
        var viewport = window.FindControl<Border>("ChatViewportCard")!;
        Assert.True(viewport.Bounds.Height >= 80, $"Filters consumed chat: {viewport.Bounds}");
        AssertObsDockInside(viewport, window, width, height);
        var scroll = window.FindControl<ScrollViewer>("MainFiltersScrollViewer")!;
        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        Assert.True(window.FindControl<TextBox>("MainMessageSearchBox")!.Bounds.Width <= scroll.Viewport.Width);
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        await SettleAuxiliaryAuditAsync(window);
        if (width == 280) Assert.True(scroll.Offset.Y > 0);
        Assert.True(window.FindControl<ScrollViewer>("SmartChatFiltersPanel")!.Viewport.Width > 0);
        using (var bottom = window.CaptureRenderedFrame())
            SaveAuditFrame(bottom!, $"r9-filters-bottom-{language}-{light}-{width}x{height}.png");
        window.ConfigureObsDockLayout(false);
        Assert.Equal(double.PositiveInfinity, window.FindControl<Border>("MainFiltersPanel")!.MaxHeight);
        Assert.Equal(ScrollBarVisibility.Disabled, scroll.VerticalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Hidden, window.FindControl<ScrollViewer>("SmartChatFiltersPanel")!.HorizontalScrollBarVisibility);
        Assert.Equal(240, window.FindControl<TextBox>("MainMessageSearchBox")!.Width);
        Assert.Equal(170, window.FindControl<TextBox>("MainUserFilterBox")!.Width);
    }

    private static void ApplyObsScreenTwitchFixture(MainWindowViewModel vm)
    {
        var session = new TwitchAuthSession("fixture", "", TwitchApplication.ClientId,
            "fixture-owner", "audit_owner", TwitchApplication.RequiredScopes,
            DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);
        typeof(MainWindowViewModel).GetMethod("ApplyAuthenticatedSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [session]);
    }
}
