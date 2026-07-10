using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TwitchChatMvp.Models;
using TwitchChatMvp.Services;

namespace TwitchChatMvp.Views;

public sealed class SettingsPanelResult
{
    public bool Accepted { get; init; }
    public bool LogoutRequested { get; init; }
    public bool ReconnectRequested { get; init; }
    public bool SignInRequested { get; init; }
    public bool ChangeWatchChannelRequested { get; init; }
}

public partial class SettingsPanel : UserControl
{
    private readonly Func<AppSettings, Task<string>>? _testOverlay;
    private readonly string _initialSettingsJson;

    public SettingsPanel(
        AppSettings settings,
        Func<AppSettings, Task<string>>? testOverlay = null,
        bool isSignedIn = false,
        bool isReadOnlyMode = false,
        string accountDisplayName = "",
        string accountLogin = "",
        string accountProfileImageUrl = "",
        string readOnlyChannel = "")
    {
        Settings = settings;
        _testOverlay = testOverlay;
        IsSignedIn = isSignedIn;
        IsReadOnlyMode = isReadOnlyMode;
        IsTwitchAccountConnected = isSignedIn;
        AccountDisplayName = string.IsNullOrWhiteSpace(accountDisplayName) ? accountLogin : accountDisplayName;
        AccountLogin = accountLogin;
        AccountProfileImageUrl = accountProfileImageUrl;
        ReadOnlyChannel = string.IsNullOrWhiteSpace(readOnlyChannel) ? Settings.LastReadOnlyChannel : readOnlyChannel;
        AccountAvatarInitial = CreateInitial(AccountDisplayName, AccountLogin);
        _initialSettingsJson = JsonSerializer.Serialize(Settings);
        LocalizationService.ApplyToResources(Settings.Language);
        InitializeComponent();
        DataContext = Settings;
        ChatLogService.CleanupEmptySessions(Settings);
        FontSizeText.Text = ((int)Math.Round(Settings.FontSize)).ToString();
        MessageLimitText.Text = Settings.MessageLimit.ToString();
        RefreshModeText();
        RefreshAccountActionText();
        RefreshChatLogsFolderHint();
        DataObject.AddPastingHandler(FontSizeText, NumberOnly_Pasting);
        DataObject.AddPastingHandler(MessageLimitText, NumberOnly_Pasting);
    }

    public AppSettings Settings { get; }
    public bool IsSignedIn { get; }
    public bool IsReadOnlyMode { get; }
    public bool IsTwitchAccountConnected { get; }
    public bool IsNotSignedIn => !IsSignedIn;
    public bool ShowAccountProfileImage => IsSignedIn && !string.IsNullOrWhiteSpace(AccountProfileImageUrl);
    public bool ShowAccountAvatarInitial => IsSignedIn && !ShowAccountProfileImage;
    public string AccountDisplayName { get; }
    public string AccountLogin { get; }
    public string AccountProfileImageUrl { get; }
    public string AccountAvatarInitial { get; }
    public string ReadOnlyChannel { get; }
    public string ReadOnlyChannelLabel => string.IsNullOrWhiteSpace(ReadOnlyChannel) ? string.Empty : "@" + ReadOnlyChannel.Trim().TrimStart('@');
    public bool LogoutRequested { get; private set; }
    public bool ReconnectRequested { get; private set; }
    public bool SignInRequested { get; private set; }
    public bool ChangeWatchChannelRequested { get; private set; }
    public bool HasUnsavedChanges
    {
        get
        {
            if (!string.Equals(_initialSettingsJson, JsonSerializer.Serialize(Settings), StringComparison.Ordinal))
            {
                return true;
            }

            if (!IsLoaded)
            {
                return false;
            }

            return !string.Equals(FontSizeText.Text.Trim(), ((int)Math.Round(Settings.FontSize)).ToString(), StringComparison.Ordinal) ||
                   !string.Equals(MessageLimitText.Text.Trim(), Settings.MessageLimit.ToString(), StringComparison.Ordinal);
        }
    }

    public event EventHandler<SettingsPanelResult>? Completed;

    public void CancelFromHost() => Complete(false);

    public void ShowUnsavedHint()
    {
        SettingsStatusText.Text = LocalizationService.Get(Settings.Language, "UnsavedSettingsHint");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitNumericValues();
        Settings.Normalize();
        Complete(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Complete(false);
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        CommitNumericValues();
        Settings.Normalize();
        LogoutRequested = true;
        Complete(true);
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        CommitNumericValues();
        Settings.Normalize();
        ReconnectRequested = true;
        Complete(true);
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        CommitNumericValues();
        Settings.Normalize();
        SignInRequested = true;
        Complete(true);
    }

    private void ChangeWatchChannel_Click(object sender, RoutedEventArgs e)
    {
        CommitNumericValues();
        Settings.Normalize();
        ChangeWatchChannelRequested = true;
        Complete(true);
    }

    private void Complete(bool accepted)
    {
        Completed?.Invoke(this, new SettingsPanelResult
        {
            Accepted = accepted,
            LogoutRequested = LogoutRequested,
            ReconnectRequested = ReconnectRequested,
            SignInRequested = SignInRequested,
            ChangeWatchChannelRequested = ChangeWatchChannelRequested
        });
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (LanguageCombo.SelectedValue is string selectedLanguage)
        {
            Settings.Language = LocalizationService.NormalizeLanguage(selectedLanguage);
        }
        else
        {
            Settings.Language = LocalizationService.NormalizeLanguage(Settings.Language);
        }

        LocalizationService.ApplyToResources(Settings.Language);
        RefreshModeText();
        RefreshAccountActionText();
        RefreshChatLogsFolderHint();
        SettingsStatusText.Text = string.Empty;
        OverlayStatusText.Text = string.Empty;
    }

    private void RefreshModeText()
    {
        CurrentModeText.Text = IsReadOnlyMode
            ? LocalizationService.Get(Settings.Language, "WatchOnlyMode")
            : IsSignedIn
                ? LocalizationService.Get(Settings.Language, "FullAccessMode")
                : LocalizationService.Get(Settings.Language, "NotSignedIn");
    }

    private void RefreshAccountActionText()
    {
        WatchChannelButton.Content = IsReadOnlyMode
            ? LocalizationService.Get(Settings.Language, "ChangeWatchChannel")
            : LocalizationService.Get(Settings.Language, "WatchChannelWithoutSignIn");
    }

    private void RefreshChatLogsFolderHint()
    {
        ChatLogsFolderHint.Text = ChatLogService.GetRootFolder(Settings);
    }

    private static string CreateInitial(string displayName, string login)
    {
        var value = string.IsNullOrWhiteSpace(displayName) ? login : displayName;
        return string.IsNullOrWhiteSpace(value)
            ? "?"
            : value.Trim()[0].ToString().ToUpperInvariant();
    }

    private void CopyOverlayUrl_Click(object sender, RoutedEventArgs e)
    {
        CommitNumericValues();
        Settings.Normalize();
        Clipboard.SetText(Settings.OverlayUrl);
        SettingsStatusText.Text = LocalizationService.Get(Settings.Language, "OverlayCopied");
        OverlayStatusText.Text = Settings.OverlayUrl;
    }

    private void SupportLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DonationService.Open((sender as Button)?.Tag as string ?? string.Empty);
            SettingsStatusText.Text = string.Empty;
        }
        catch
        {
            SettingsStatusText.Text = LocalizationService.Get(Settings.Language, "SupportActionFailed");
        }
    }

    private void CopyCryptoAddress_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var network = (sender as Button)?.Tag as string ?? string.Empty;
            Clipboard.SetText(DonationService.GetAddress(network));
            SettingsStatusText.Text = LocalizationService.Get(
                Settings.Language,
                network == "USDT_TRC20" ? "UsdtTrc20Copied" : "UsdtTonCopied");
        }
        catch
        {
            SettingsStatusText.Text = LocalizationService.Get(Settings.Language, "SupportActionFailed");
        }
    }

    private async void TestOverlay_Click(object sender, RoutedEventArgs e)
    {
        CommitNumericValues();
        Settings.Normalize();
        if (_testOverlay is null)
        {
            OverlayStatusText.Text = LocalizationService.Get(Settings.Language, "OverlayUnavailable");
            return;
        }

        try
        {
            OverlayStatusText.Text = LocalizationService.Get(Settings.Language, "OverlaySending");
            OverlayStatusText.Text = await _testOverlay(Settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            OverlayStatusText.Text = ex.Message;
        }
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        CommitNumericValues();
        Settings.Normalize();
        RefreshChatLogsFolderHint();
        try
        {
            ChatLogService.CleanupEmptySessions(Settings);
            ChatLogService.OpenFolder(ChatLogService.GetRootFolder(Settings));
            SettingsStatusText.Text = LocalizationService.Get(Settings.Language, "OpenLogsFolder");
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = ex.Message;
        }
    }

    private void FontSizeMinus_Click(object sender, RoutedEventArgs e) => AdjustFontSize(-1);
    private void FontSizePlus_Click(object sender, RoutedEventArgs e) => AdjustFontSize(1);
    private void MessageLimitMinus_Click(object sender, RoutedEventArgs e) => AdjustMessageLimit(-100);
    private void MessageLimitPlus_Click(object sender, RoutedEventArgs e) => AdjustMessageLimit(100);

    private void FontSizeText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        HandleNumericKey(e, () => AdjustFontSize(1), () => AdjustFontSize(-1));
    }

    private void MessageLimitText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        HandleNumericKey(e, () => AdjustMessageLimit(100), () => AdjustMessageLimit(-100));
    }

    private void FontSizeText_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        AdjustFontSize(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void MessageLimitText_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        AdjustMessageLimit(e.Delta > 0 ? 100 : -100);
        e.Handled = true;
    }

    private void FontSizeText_LostFocus(object sender, RoutedEventArgs e) => CommitFontSize();
    private void MessageLimitText_LostFocus(object sender, RoutedEventArgs e) => CommitMessageLimit();

    private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private static void NumberOnly_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text) ||
            e.DataObject.GetData(DataFormats.Text) is not string text ||
            !text.All(char.IsDigit))
        {
            e.CancelCommand();
        }
    }

    private static bool HandleNumericKey(KeyEventArgs e, Action increase, Action decrease)
    {
        switch (e.Key)
        {
            case Key.Up:
                increase();
                e.Handled = true;
                return true;
            case Key.Down:
                decrease();
                e.Handled = true;
                return true;
            case Key.Enter:
                e.Handled = true;
                return true;
            default:
                return false;
        }
    }

    private void AdjustFontSize(int delta)
    {
        CommitFontSize();
        Settings.FontSize = Math.Clamp(Settings.FontSize + delta, 10, 32);
        FontSizeText.Text = ((int)Math.Round(Settings.FontSize)).ToString();
    }

    private void AdjustMessageLimit(int delta)
    {
        CommitMessageLimit();
        Settings.MessageLimit = Math.Clamp(Settings.MessageLimit + delta, 100, 5000);
        MessageLimitText.Text = Settings.MessageLimit.ToString();
    }

    private void CommitNumericValues()
    {
        CommitFontSize();
        CommitMessageLimit();
    }

    private void CommitFontSize()
    {
        if (!int.TryParse(FontSizeText.Text.Trim(), out var value))
        {
            value = (int)Math.Round(Settings.FontSize);
        }

        value = Math.Clamp(value, 10, 32);
        Settings.FontSize = value;
        FontSizeText.Text = value.ToString();
    }

    private void CommitMessageLimit()
    {
        if (!int.TryParse(MessageLimitText.Text.Trim(), out var value))
        {
            value = Settings.MessageLimit;
        }

        value = Math.Clamp((int)Math.Round(value / 100.0) * 100, 100, 5000);
        Settings.MessageLimit = value;
        MessageLimitText.Text = value.ToString();
    }
}
