using System.Diagnostics;
using System.Windows;
using TwitchChatMvp.Models;
using TwitchChatMvp.Views;

namespace TwitchChatMvp.Services;

public sealed class DialogService
{
    private readonly SettingsService _settings = new();

    public bool ConfirmPermanentBan(ChatMessageModel message)
    {
        var language = _settings.Load().Language;
        return SilentDialog.Confirm(
            LocalizationService.Get(language, "ConfirmBanTitle"),
            string.Format(LocalizationService.Get(language, "ConfirmBan"), message.UserLabel));
    }

    public ModerationRequest? ShowBanReasonDialog(ChatMessageModel message)
    {
        var dialog = new ModerationDialog($"Ban user: {message.UserLabel}", false, null)
        {
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Request : null;
    }

    public ModerationRequest? ShowTimeoutDialog(ChatMessageModel message, int initialSeconds)
    {
        var dialog = new ModerationDialog($"Timeout user: {message.UserLabel}", true, initialSeconds)
        {
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Request : null;
    }

    public void ShowInfo(string title, string message)
    {
        SilentDialog.ShowMessage(title, message);
    }

    public void ShowError(string title, string message)
    {
        SilentDialog.ShowMessage(title, message);
    }

    public void CopyText(string text)
    {
        Clipboard.SetText(text ?? string.Empty);
    }

    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (!uri.Host.Equals("id.twitch.tv", StringComparison.OrdinalIgnoreCase) &&
             !uri.Host.Equals("www.twitch.tv", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Only trusted Twitch HTTPS links can be opened.");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
