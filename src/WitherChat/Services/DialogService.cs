using System.Diagnostics;
using System.Globalization;
using System.Windows;
using WitherChat.Models;
using WitherChat.Views;

namespace WitherChat.Services;

public sealed class DialogService
{
    private readonly SettingsService _settings = new();

    public bool ConfirmPermanentBan(ChatMessageModel message)
    {
        var language = _settings.Load().Language;
        return SilentDialog.Confirm(
            LocalizationService.Get(language, "ConfirmBanTitle"),
            string.Format(CultureInfo.CurrentCulture, LocalizationService.Get(language, "ConfirmBan"), message.UserLabel));
    }

    public ModerationRequest? ShowBanReasonDialog(ChatMessageModel message)
    {
        var language = _settings.Load().Language;
        var dialog = new ModerationDialog(
            string.Format(CultureInfo.CurrentCulture, LocalizationService.Get(language, "BanUserTitleFormat"), message.UserLabel),
            false,
            null)
        {
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Request : null;
    }

    public ModerationRequest? ShowCustomTimeoutDialog(ChatMessageModel message, string channelName)
    {
        var dialog = new CustomTimeoutDialog(message, channelName)
        {
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Request : null;
    }

    public UnbanRequestResolution? ShowUnbanRequestResolutionDialog(UnbanRequestEntry request, bool approve)
    {
        var dialog = new UnbanRequestResolutionDialog(request, approve)
        {
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Resolution : null;
    }

    public void ShowError(string title, string message)
    {
        SilentDialog.ShowMessage(title, message);
    }

    public bool CopyText(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
                return true;
            }
            catch
            {
                if (attempt < 2)
                {
                    Thread.Sleep(40 * (attempt + 1));
                }
            }
        }

        return false;
    }

    public bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (!uri.Host.Equals("id.twitch.tv", StringComparison.OrdinalIgnoreCase) &&
             !uri.Host.Equals("www.twitch.tv", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        try
        {
            return Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }) is not null;
        }
        catch
        {
            return false;
        }
    }
}
