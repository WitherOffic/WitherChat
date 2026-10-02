using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WitherChat.Desktop;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Platforms;
using WitherChat.Desktop.Platforms.macOS;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.Views;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class TrayMenuTests
{
    [AvaloniaFact]
    public void MultipleChannelsExposeSwitchRestartAndExitCommands()
    {
        var texts = new UiText();
        var first = new ChannelSessionViewModel("first_channel");
        var second = new ChannelSessionViewModel("second_channel")
        {
            IsActive = true
        };
        var opened = false;
        var restarted = false;
        var exited = false;
        var switchedChannel = string.Empty;

        var menu = TrayMenuFactory.Create(
            texts,
            [first, second],
            () => opened = true,
            login => switchedChannel = login,
            () => restarted = true,
            () => exited = true);

        Assert.Equal(5, menu.Items.Count);
        var openItem = Assert.IsType<NativeMenuItem>(menu.Items[0]);
        Assert.Equal("Открыть WitherChat", openItem.Header);
        openItem.Command!.Execute(null);
        Assert.True(opened);

        var channelsItem = Assert.IsType<NativeMenuItem>(menu.Items[1]);
        Assert.Equal("Переключить чат", channelsItem.Header);
        Assert.NotNull(channelsItem.Menu);
        Assert.Equal(2, channelsItem.Menu.Items.Count);
        Assert.Equal(
            "@first_channel",
            Assert.IsType<NativeMenuItem>(channelsItem.Menu.Items[0]).Header);
        var activeChannelItem = Assert.IsType<NativeMenuItem>(channelsItem.Menu.Items[1]);
        Assert.Equal("✓ @second_channel", activeChannelItem.Header);
        activeChannelItem.Command!.Execute(null);
        Assert.Equal("second_channel", switchedChannel);

        Assert.IsType<NativeMenuItemSeparator>(menu.Items[2]);
        var restartItem = Assert.IsType<NativeMenuItem>(menu.Items[3]);
        Assert.Equal("Перезапустить WitherChat", restartItem.Header);
        restartItem.Command!.Execute(null);
        Assert.True(restarted);

        var exitItem = Assert.IsType<NativeMenuItem>(menu.Items[4]);
        Assert.Equal("Завершить", exitItem.Header);
        exitItem.Command!.Execute(null);
        Assert.True(exited);
    }

    [AvaloniaFact]
    public void SingleChannelKeepsTrayMenuCompact()
    {
        var menu = TrayMenuFactory.Create(
            new UiText(),
            [new ChannelSessionViewModel("only_channel")],
            () => { },
            _ => { },
            () => { },
            () => { });

        Assert.Equal(4, menu.Items.Count);
        Assert.Equal(
            "Перезапустить WitherChat",
            Assert.IsType<NativeMenuItem>(menu.Items[2]).Header);
    }

    [Fact]
    public void RestartPreservesArgumentsAndWaitsForTheCurrentProcess()
    {
        var startInfo = App.CreateRestartStartInfo(
            @"C:\Apps\WitherChat.exe",
            ["--compact", "--channel=t2x2"],
            1234);

        Assert.Equal(@"C:\Apps\WitherChat.exe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(["--compact", "--channel=t2x2"], startInfo.ArgumentList);
        Assert.Equal("1234", startInfo.Environment["WITHERCHAT_RESTART_PARENT_PID"]);
    }

    [Fact]
    public void WindowsSessionEndMessagesCannotBeCanceledByCloseToTray()
    {
        var queryCount = 0;
        bool? isEnding = null;

        Assert.True(WindowsWindowAnimation.ProcessSessionEndMessage(
            0x0011,
            IntPtr.Zero,
            () => queryCount++,
            value => isEnding = value,
            out var queryResult));
        Assert.Equal(1, queryCount);
        Assert.Equal(new IntPtr(1), queryResult);

        Assert.False(WindowsWindowAnimation.ProcessSessionEndMessage(
            0x0016,
            IntPtr.Zero,
            () => queryCount++,
            value => isEnding = value,
            out _));
        Assert.False(isEnding);

        Assert.False(WindowsWindowAnimation.ProcessSessionEndMessage(
            0x0016,
            new IntPtr(1),
            () => queryCount++,
            value => isEnding = value,
            out _));
        Assert.True(isEnding);
    }

    [Fact]
    public void SingleInstanceGuardRejectsASecondProcessOwner()
    {
        var mutexName = (OperatingSystem.IsWindows() ? "Local\\" : string.Empty) +
                        "WitherChat-tests-" + Guid.NewGuid().ToString("N");

        using (var first = SingleInstanceGuard.Acquire(mutexName))
        {
            Assert.True(first.IsFirstInstance);
            using var second = SingleInstanceGuard.Acquire(mutexName);
            Assert.False(second.IsFirstInstance);
        }

        using var afterExit = SingleInstanceGuard.Acquire(mutexName);
        Assert.True(afterExit.IsFirstInstance);
    }

    [AvaloniaFact]
    public void SingleInstanceNotificationOffersToShowTheRunningChat()
    {
        var window = new SingleInstanceWindow("en");

        Assert.Equal(460, window.Width);
        Assert.Equal(260, window.Height);
        Assert.Equal("None", window.WindowDecorations.ToString());
        Assert.True(window.Topmost);
        Assert.Contains("wither-window", window.Classes);
        Assert.Equal(
            "WitherChat is already running. You can show its window now.",
            window.FindControl<TextBlock>("MessageText")!.Text);
        Assert.Equal("Close", window.FindControl<Button>("DismissButton")!.Content);
        Assert.Equal("Show chat window", window.FindControl<Button>("ShowChatButton")!.Content);
    }

    [Fact]
    public async Task SecondInstanceCanRequestTheRunningWindow()
    {
        var pipeName = "WitherChat-tests-activate-" + Guid.NewGuid().ToString("N");
        var activationReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new SingleInstanceActivationService(
            () => activationReceived.TrySetResult(),
            pipeName);

        Assert.True(await SingleInstanceActivationService.RequestShowWindowAsync(
            pipeName,
            TestContext.Current.CancellationToken));
        await activationReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void MacOsUsesSystemOpenWithoutShellInterpolation()
    {
        var uri = new Uri("https://id.twitch.tv/oauth2/authorize?state=a%20b");
        var startInfo = MainWindow.CreateMacOpenUriStartInfo(uri);

        Assert.Equal("/usr/bin/open", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Single(startInfo.ArgumentList);
        Assert.Equal(uri.AbsoluteUri, startInfo.ArgumentList[0]);
    }

    [Fact]
    public void MacOsUsesDockInsteadOfMutableSystemTrayMenu()
    {
        var platform = new MacOsPlatformDescriptor();

        Assert.False(platform.SupportsSystemTray);
    }
}
