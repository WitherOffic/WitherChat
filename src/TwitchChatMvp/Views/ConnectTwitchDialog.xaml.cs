using System.Windows;
using TwitchChatMvp.Models;
using TwitchChatMvp.Services;

namespace TwitchChatMvp.Views;

public partial class ConnectTwitchDialog : Window
{
    private readonly string _language;

    public ConnectTwitchDialog(string language, string lastReadOnlyChannel)
    {
        _language = LocalizationService.NormalizeLanguage(language);
        LocalizationService.ApplyToResources(_language);
        InitializeComponent();
        ChannelLoginText.Text = lastReadOnlyChannel;
        ChannelLoginText.Focus();
        ChannelLoginText.SelectAll();
    }

    public ChatConnectionMode SelectedMode { get; private set; } = ChatConnectionMode.SignedIn;

    public string ChannelLogin { get; private set; } = string.Empty;

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = ChatConnectionMode.SignedIn;
        DialogResult = true;
    }

    private void WatchOnly_Click(object sender, RoutedEventArgs e)
    {
        var login = (ChannelLoginText.Text ?? string.Empty).Trim().TrimStart('@', '#');
        if (string.IsNullOrWhiteSpace(login))
        {
            ErrorText.Text = LocalizationService.Get(_language, "TwitchChannelNameRequired");
            ErrorText.Visibility = Visibility.Visible;
            ChannelLoginText.Focus();
            return;
        }

        SelectedMode = ChatConnectionMode.ReadOnly;
        ChannelLogin = login;
        DialogResult = true;
    }
}
