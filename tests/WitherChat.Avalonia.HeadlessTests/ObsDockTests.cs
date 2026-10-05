using System.IO.Pipes;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using WitherChat.Core.Models;
using WitherChat.Desktop.Controls;
using WitherChat.Desktop.Platforms;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public async Task ObsDockResizesKeepMessagesComposerAndCommands(string language, bool light)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var window = fixture.Window;
        vm.Language = language;
        vm.Theme = light ? "Light" : "Dark";
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        Assert.Equal(light, vm.UseLightMessageTheme);
        vm.Channel = "audit";
        vm.ComposerText = "Draft stays while resizing";
        for (var index = 0; index < 4; index++)
        {
            var item = new ChatMessageItemViewModel(new ChatMessage
            {
                Id = $"dock-{index}", Channel = "audit", UserLogin = "viewer", DisplayName = "Viewer",
                Text = "A readable message with a long line of text for the narrow OBS dock.",
                Timestamp = DateTimeOffset.UtcNow,
                Parts = [ChatMessagePart.PlainText("A readable message with a long line of text for the narrow OBS dock.")]
            }, fixture.ImageCache, vm.Texts, owner: vm);
            vm.Messages.Add(item);
            vm.VisibleMessages.Add(item);
        }
        window.ConfigureObsDockLayout(true);
        window.Show();
        foreach (var (width, height) in new[] { (280, 340), (360, 600), (600, 600), (1100, 760), (360, 600) })
        {
            window.Width = width;
            window.Height = height;
            await SettleAuxiliaryAuditAsync(window);
            window.UpdateObsDockLayout();
            await SettleAuxiliaryAuditAsync(window);
            Assert.True(vm.IsObsDockMode);
            Assert.Equal(width < 860, vm.IsCompactMode);
            Assert.False(window.FindControl<Border>("TitleBarPanel")!.IsVisible);
            var toolbar = window.FindControl<Border>("ObsDockToolbar")!;
            Assert.True(toolbar.IsVisible);
            if (toolbar.IsVisible)
            {
                AssertObsDockInside(toolbar, window, width, height);
                foreach (var button in toolbar.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.IsEffectivelyVisible))
                    AssertObsDockInside(button, window, width, height);
            }
            Assert.True(window.FindControl<Grid>("ComposerPanel")!.IsVisible);
            Assert.Equal("Draft stays while resizing", vm.ComposerText);
            Assert.Equal(4, vm.VisibleMessages.Count);
            Assert.Contains(window.GetVisualDescendants().OfType<RichChatTextBlock>(),
                text => text.IsEffectivelyVisible && text.Bounds.Width > 100 && text.TextLayout.Width > 100);
            using var frame = window.CaptureRenderedFrame();
            SaveAuditFrame(frame!, $"obs-dock-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
        }
        var moreButton = window.GetVisualDescendants().OfType<Button>().Single(
            button => AutomationProperties.GetAutomationId(button) == "ObsDockMoreButton");
        ClickAtCenter(window, moreButton);
        await SettleAuxiliaryAuditAsync(window);
        var moreFlyout = Assert.IsType<Flyout>(moreButton.Flyout);
        var menu = Assert.IsType<Border>(moreFlyout.Content);
        var menuButtons = menu.GetLogicalDescendants().OfType<Button>().ToArray();
        Assert.Equal(13, menuButtons.Length);
        Assert.All(menuButtons, button => Assert.NotNull(button.Command));
        Assert.True(Assert.Single(menuButtons, button => AutomationProperties.GetAutomationId(button) == "ObsDockDonationButton").IsVisible);
        Assert.True(Assert.Single(menuButtons, button => AutomationProperties.GetAutomationId(button) == "ObsDockClipButton").IsVisible);
        var settings = menuButtons.Single(
            button => AutomationProperties.GetAutomationId(button) == "ObsDockSettingsButton");
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(settings, null);
        await SettleAuxiliaryAuditAsync(window);
        Assert.True(vm.IsSettingsOpen);
        AssertAuxiliaryAuditInside(window.FindControl<Border>("SettingsCard")!, window, 360, 600);
        using var settingsFrame = window.CaptureRenderedFrame();
        SaveAuditFrame(settingsFrame!, $"obs-dock-settings-{language}-{light}.png");
        vm.IsSettingsOpen = false;
        window.ConfigureObsDockLayout(false);
        Assert.False(vm.IsObsDockMode);
        Assert.False(vm.IsCompactMode);
        Assert.True(window.FindControl<Border>("TitleBarPanel")!.IsVisible);
        Assert.True(window.ShowInTaskbar);
        Assert.True(window.CanResize);
        Assert.Equal(860, window.MinWidth);
        Assert.Equal(4, vm.VisibleMessages.Count);
        Assert.Equal("Draft stays while resizing", vm.ComposerText);
    }

    private static void AssertObsDockInside(Control control, Window window, int width, int height)
    {
        var point = control.TranslatePoint(default, window)!.Value;
        Assert.True(point.X >= 0 && point.Y >= 0 &&
            point.X + control.Bounds.Width <= width + 0.5 &&
            point.Y + control.Bounds.Height <= height + 0.5, $"Outside dock: {point}, {control.Bounds}");
    }

    [AvaloniaFact]
    public async Task ObsDockRestoresPreviousCompactAndPanelPreferences()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        vm.IsCompactMode = true;
        vm.IsHeaderExpanded = false;
        vm.IsComposerExpanded = false;
        fixture.Window.ConfigureObsDockLayout(true);
        Assert.True(vm.IsComposerExpanded);
        fixture.Window.ConfigureObsDockLayout(false);
        Assert.True(vm.IsCompactMode);
        Assert.False(vm.IsHeaderExpanded);
        Assert.False(vm.IsComposerExpanded);
        Assert.Equal(280, fixture.Window.MinWidth);
        Assert.Equal(340, fixture.Window.MinHeight);
    }
}

public sealed class ObsDockProtocolTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ATTACH")]
    [InlineData("ATTACH 0 123")]
    [InlineData("ATTACH 123 0")]
    [InlineData("ATTACH -1 22")]
    [InlineData("ATTACH 1 -22")]
    [InlineData("ATTACH 1 9223372036854775808")]
    [InlineData("ATTACH 4294967296 22")]
    [InlineData("ATTACH 1 22 extra")]
    [InlineData("SHOW 1 22")]
    [InlineData("attach 1 22")]
    public void InvalidCommandsAreRejected(string? text) => Assert.False(ObsDockRequest.TryParse(text, out _));

    [Theory]
    [InlineData("ATTACH 123 456", true)]
    [InlineData("DETACH 123 456", false)]
    public void ValidCommandsRoundTrip(string text, bool attach)
    {
        Assert.True(ObsDockRequest.TryParse(text, out var request));
        Assert.Equal(attach, request.Attach);
        Assert.Equal(123u, request.ProcessId);
        Assert.Equal((nint)456, request.ParentWindow);
        Assert.Equal(text, request.ToString());
    }

    [Theory]
    [InlineData("ATTACH 1 2\n", "ATTACH 1 2")]
    [InlineData("ATTACH 1 2\r\n", "ATTACH 1 2")]
    [InlineData("ATTACH 1 2", null)]
    [InlineData("ATTACH 1\t2\n", null)]
    public async Task BoundedPipeReaderRejectsInvalidFrames(string input, string? expected)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(input));
        Assert.Equal(expected, await ObsDockIpcService.ReadLineAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task OversizePipeFrameIsRejectedWithoutReadingForever()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(new string('a', 1000) + "\n"));
        Assert.Null(await ObsDockIpcService.ReadLineAsync(stream, CancellationToken.None));
        Assert.True(stream.Position <= 129);
    }

    [Fact]
    public async Task PipeRejectsInvalidRequestThenAcceptsNextClient()
    {
        var pipe = "WitherChat-ObsDock-test-" + Guid.NewGuid().ToString("N");
        var calls = 0;
        await using var service = new ObsDockIpcService(request =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("OK 456");
        }, pipe);
        await using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(timeout.Token);
            await client.WriteAsync(Encoding.ASCII.GetBytes("ATTACH 0 0\n"), timeout.Token);
            Assert.Equal("ERROR invalid-command", await ObsDockIpcService.ReadLineAsync(client, timeout.Token));
        }
        Assert.Equal(0, calls);
        Assert.True(await ObsDockIpcService.RequestAsync(new ObsDockRequest(true, 123, 456), pipe, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PipeShutdownInterruptsClientThatNeverSendsNewline()
    {
        var pipe = "WitherChat-ObsDock-test-" + Guid.NewGuid().ToString("N");
        var service = new ObsDockIpcService(_ => Task.FromResult("OK 456"), pipe);
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            await client.ConnectAsync(timeout.Token);
            await client.WriteAsync(Encoding.ASCII.GetBytes("ATTACH"), timeout.Token);
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }
        finally { await service.DisposeAsync(); }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(4294967295L, 123)]
    public void InvalidNativeParentsAreNotAccepted(long pid, long hwnd) =>
        Assert.False(WindowsObsDockHost.IsValidParent(new ObsDockRequest(true, (uint)pid, (nint)hwnd)));
}
