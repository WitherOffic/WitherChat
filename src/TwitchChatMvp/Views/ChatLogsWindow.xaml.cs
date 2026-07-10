using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TwitchChatMvp.Models;
using TwitchChatMvp.Services;

namespace TwitchChatMvp.Views;

public partial class ChatLogsWindow : Window
{
    private readonly AppSettings _settings;
    private CancellationTokenSource? _loadCts;
    private bool _allowClose;
    private bool _closingWithAnimation;

    public ChatLogsWindow(AppSettings settings)
    {
        _settings = settings;
        _settings.Normalize();
        LocalizationService.ApplyToResources(_settings.Language);
        InitializeComponent();
        RootFolderText.Text = ChatLogService.GetRootFolder(_settings);
        LoadChannels();
        Loaded += (_, _) => AnimationService.AnimateWindowIn(this, offsetX: 24, offsetY: 0);
        Closing += ChatLogsWindow_Closing;
        Closed += (_, _) => _loadCts?.Dispose();
    }

    private ChatLogSessionSummary? SelectedSession => SessionsList.SelectedItem as ChatLogSessionSummary;

    private void LoadChannels()
    {
        var channels = ChatLogService.GetChannels(_settings);
        ChannelsList.ItemsSource = channels;
        if (channels.Count > 0)
        {
            ChannelsList.SelectedIndex = 0;
            StatusText.Text = string.Empty;
        }
        else
        {
            SessionsList.ItemsSource = null;
            MessagesList.ItemsSource = null;
            SessionTitleText.Text = LocalizationService.Get(_settings.Language, "NoChatLogs");
            SessionMetaText.Text = ChatLogService.GetRootFolder(_settings);
            StatusText.Text = LocalizationService.Get(_settings.Language, "NoChatLogs");
        }
    }

    private void ChannelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelsList.SelectedItem is not ChatLogChannelSummary channel)
        {
            SessionsList.ItemsSource = null;
            MessagesList.ItemsSource = null;
            return;
        }

        var sessions = ChatLogService.GetSessions(channel);
        SessionsList.ItemsSource = sessions;
        SessionsList.SelectedIndex = sessions.Count > 0 ? 0 : -1;
        if (sessions.Count == 0)
        {
            MessagesList.ItemsSource = null;
            SessionTitleText.Text = LocalizationService.Get(_settings.Language, "NoChatLogs");
            SessionMetaText.Text = string.Empty;
        }
    }

    private void SessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSessionInfo();
        _ = ReloadMessagesAsync();
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = ReloadMessagesAsync();
    }

    private void RoleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _ = ReloadMessagesAsync();
    }

    private async Task ReloadMessagesAsync()
    {
        var session = SelectedSession;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        if (session is null)
        {
            MessagesList.ItemsSource = null;
            return;
        }

        try
        {
            StatusText.Text = LocalizationService.Get(_settings.Language, "LoadingLogs");
            var messages = await ChatLogService.LoadMessagesAsync(
                session,
                SearchTextBox.Text,
                UserTextBox.Text,
                GetSelectedRole(),
                _settings.MaxLogViewerMessages,
                token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            MessagesList.ItemsSource = messages;
            StatusText.Text = $"{LocalizationService.Get(_settings.Language, "Messages")}: {messages.Count:N0}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void UpdateSessionInfo()
    {
        var session = SelectedSession;
        if (session is null)
        {
            SessionTitleText.Text = LocalizationService.Get(_settings.Language, "NoChatLogs");
            SessionMetaText.Text = string.Empty;
            return;
        }

        SessionTitleText.Text = session.DisplayTitle;
        SessionMetaText.Text =
            $"{LocalizationService.Get(_settings.Language, "StreamDate")}: {session.DisplayDate}   " +
            $"{LocalizationService.Get(_settings.Language, "Messages")}: {session.MessageCountText}";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedSession?.DirectoryPath ?? ChatLogService.GetRootFolder(_settings);
        try
        {
            ChatLogService.OpenFolder(path);
        }
        catch (Exception ex)
        {
            SilentDialog.ShowMessage(LocalizationService.Get(_settings.Language, "Error"), ex.Message);
        }
    }

    private async void ExportTxt_Click(object sender, RoutedEventArgs e)
    {
        await ExportAsync("chat.txt", "Text log|*.txt", ".txt");
    }

    private async void ExportJsonl_Click(object sender, RoutedEventArgs e)
    {
        await ExportAsync("chat.jsonl", "JSON Lines log|*.jsonl", ".jsonl");
    }

    private async Task ExportAsync(string fileName, string filter, string extension)
    {
        var session = SelectedSession;
        if (session is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = $"{SanitizeFileName(session.DisplayTitle)}{extension}",
            Filter = filter,
            DefaultExt = extension
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await ChatLogService.ExportAsync(session, fileName, dialog.FileName).ConfigureAwait(true);
            StatusText.Text = dialog.FileName;
        }
        catch (Exception ex)
        {
            SilentDialog.ShowMessage(LocalizationService.Get(_settings.Language, "Error"), ex.Message);
        }
    }

    private void DeleteLog_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession;
        if (session is null)
        {
            return;
        }

        if (!SilentDialog.Confirm(
                LocalizationService.Get(_settings.Language, "DeleteLog"),
                LocalizationService.Get(_settings.Language, "DeleteLogConfirm")))
        {
            return;
        }

        try
        {
            ChatLogService.DeleteSession(_settings, session);
            LoadChannels();
        }
        catch (Exception ex)
        {
            SilentDialog.ShowMessage(LocalizationService.Get(_settings.Language, "Error"), ex.Message);
        }
    }

    private string GetSelectedRole()
    {
        return RoleCombo.SelectedItem is ComboBoxItem item && item.Tag is string role
            ? role
            : string.Empty;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        var result = new string(chars).Trim(' ', '.', '-');
        return string.IsNullOrWhiteSpace(result) ? "chat-log" : result;
    }

    private async void ChatLogsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _closingWithAnimation)
        {
            return;
        }

        e.Cancel = true;
        _closingWithAnimation = true;
        await AnimationService.AnimateWindowCloseAsync(this, offsetX: 24, offsetY: 0).ConfigureAwait(true);
        _allowClose = true;
        Close();
    }
}
