using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using WitherChat.Desktop.Models;

namespace WitherChat.Desktop.Services;

internal static class TrayMenuFactory
{
    public static NativeMenu Create(
        UiText texts,
        IReadOnlyList<ChannelSessionViewModel> channels,
        Action openApplication,
        Action<string> switchChannel,
        Action restartApplication,
        Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(openApplication);
        ArgumentNullException.ThrowIfNull(switchChannel);
        ArgumentNullException.ThrowIfNull(restartApplication);
        ArgumentNullException.ThrowIfNull(exitApplication);

        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem(texts.OpenWitherChat)
        {
            Command = new RelayCommand(openApplication)
        });

        if (channels.Count > 1)
        {
            var channelMenu = new NativeMenu();
            foreach (var channel in channels)
            {
                var login = channel.Login;
                channelMenu.Items.Add(new NativeMenuItem(
                    (channel.IsActive ? "✓ " : string.Empty) + "@" + login)
                {
                    Command = new RelayCommand(() => switchChannel(login))
                });
            }

            menu.Items.Add(new NativeMenuItem(texts.TrayChannels)
            {
                Menu = channelMenu
            });
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem(texts.RestartWitherChat)
        {
            Command = new RelayCommand(restartApplication)
        });
        menu.Items.Add(new NativeMenuItem(texts.Exit)
        {
            Command = new RelayCommand(exitApplication)
        });
        return menu;
    }
}
