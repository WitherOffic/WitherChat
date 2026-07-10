using System.ComponentModel;
using System.Windows;
using TwitchChatMvp.Services;

namespace TwitchChatMvp.Views;

public partial class SilentDialog : Window
{
    private static Func<string, string, bool, bool>? _host;
    private bool _allowClose;
    private bool _closingWithAnimation;

    private SilentDialog(string title, string message, bool confirm)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        var language = new SettingsService().Load().Language;
        OkButton.Content = confirm
            ? (LocalizationService.NormalizeLanguage(language) == LocalizationService.English ? "Yes" : "Да")
            : "OK";
        Loaded += (_, _) => AnimationService.AnimateDialogIn(this);
        Closing += SilentDialog_Closing;
    }

    public static void RegisterHost(Func<string, string, bool, bool> host)
    {
        _host = host;
    }

    public static void ClearHost(Func<string, string, bool, bool> host)
    {
        if (_host == host)
        {
            _host = null;
        }
    }

    public static void ShowMessage(string title, string message)
    {
        if (_host is not null)
        {
            _host(title, message, false);
            return;
        }

        var dialog = Create(title, message, confirm: false);
        dialog.ShowDialog();
    }

    public static bool Confirm(string title, string message)
    {
        if (_host is not null)
        {
            return _host(title, message, true);
        }

        var dialog = Create(title, message, confirm: true);
        return dialog.ShowDialog() == true;
    }

    private static SilentDialog Create(string title, string message, bool confirm)
    {
        var dialog = new SilentDialog(title, message, confirm);
        var owner = Application.Current?.MainWindow;
        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        return dialog;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        await CompleteDialogAsync(true).ConfigureAwait(true);
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        await CompleteDialogAsync(false).ConfigureAwait(true);
    }

    private async Task CompleteDialogAsync(bool? result)
    {
        await AnimationService.AnimateWindowCloseAsync(this, offsetY: 12).ConfigureAwait(true);
        _allowClose = true;
        DialogResult = result;
    }

    private async void SilentDialog_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _closingWithAnimation)
        {
            return;
        }

        e.Cancel = true;
        _closingWithAnimation = true;
        await AnimationService.AnimateWindowCloseAsync(this, offsetY: 12).ConfigureAwait(true);
        _allowClose = true;
        Close();
    }
}
