using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WitherChat.Models;
using WitherChat.Services;

namespace WitherChat.Views;

public partial class ChatLogsPanel : UserControl
{
    private static readonly TimeSpan FilterDebounceDelay = TimeSpan.FromMilliseconds(250);
    private readonly AppSettings _settings;
    private readonly ChatLogService _chatLogService;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _filterDebounceCts;
    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _deleteCts;
    private Task _loadTask = Task.CompletedTask;
    private Task _filterDebounceTask = Task.CompletedTask;
    private Task _exportTask = Task.CompletedTask;
    private Task _deleteTask = Task.CompletedTask;
    private bool _hostCancellationRequested;

    public event EventHandler? CloseRequested;

    public ChatLogsPanel(AppSettings settings, ChatLogService chatLogService)
    {
        _settings = settings;
        _chatLogService = chatLogService;
        _settings.Normalize();
        LocalizationService.ApplyToResources(_settings.Language);
        InitializeComponent();
        RootFolderText.Text = ChatLogService.GetRootFolder(_settings);
        Unloaded += ChatLogsPanel_Unloaded;
        LoadChannels();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ChatLogsPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelFromHost();
    }

    internal void CancelFromHost()
    {
        _hostCancellationRequested = true;
        Interlocked.Exchange(ref _filterDebounceCts, null)?.Cancel();
        Interlocked.Exchange(ref _loadCts, null)?.Cancel();
        Interlocked.Exchange(ref _exportCts, null)?.Cancel();
        Interlocked.Exchange(ref _deleteCts, null)?.Cancel();
    }

    internal async Task CancelFromHostAsync()
    {
        CancelFromHost();
        try
        {
            await Task.WhenAll(_filterDebounceTask, _loadTask, _exportTask, _deleteTask).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
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
        CancelFilterDebounce();
        UpdateSessionInfo();
        StartReloadMessages();
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        ScheduleFilterReload();
    }

    private void RoleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ScheduleFilterReload();
    }

    private void StartReloadMessages()
    {
        _loadTask = ReloadMessagesAsync();
    }

    private void ScheduleFilterReload()
    {
        if (!IsLoaded)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _filterDebounceCts, cts)?.Cancel();
        _filterDebounceTask = ReloadAfterDebounceAsync(cts);
    }

    private async Task ReloadAfterDebounceAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(FilterDebounceDelay, cts.Token).ConfigureAwait(true);
            StartReloadMessages();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _filterDebounceCts, null, cts);
            cts.Dispose();
        }
    }

    private void CancelFilterDebounce()
    {
        Interlocked.Exchange(ref _filterDebounceCts, null)?.Cancel();
    }

    private async Task ReloadMessagesAsync()
    {
        var session = SelectedSession;
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCts, cts);
        previous?.Cancel();
        var token = cts.Token;

        try
        {
            if (session is null)
            {
                MessagesList.ItemsSource = null;
                return;
            }

            StatusText.Text = LocalizationService.Get(_settings.Language, "LoadingLogs");
            await _chatLogService.FlushAsync().WaitAsync(token).ConfigureAwait(true);
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
        catch (Exception)
        {
            StatusText.Text = LocalizationService.Get(_settings.Language, "ChatLogLoadFailed");
        }
        finally
        {
            Interlocked.CompareExchange(ref _loadCts, null, cts);
            cts.Dispose();
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

        var channelLogin = string.IsNullOrWhiteSpace(session.Metadata.ChannelLogin)
            ? string.Empty
            : "@" + session.Metadata.ChannelLogin.TrimStart('@');
        SessionTitleText.Text = string.IsNullOrWhiteSpace(channelLogin)
            ? session.DisplayTitle
            : $"{channelLogin} · {session.DisplayTitle}";
        SessionMetaText.Text =
            $"{LocalizationService.Get(_settings.Language, "LogDate")}: {session.DisplayDate}   " +
            $"{LocalizationService.Get(_settings.Language, "Messages")}: {session.MessageCountText}";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedSession?.DirectoryPath ?? ChatLogService.GetRootFolder(_settings);
        try
        {
            ChatLogService.OpenFolder(path);
        }
        catch (Exception)
        {
            SilentDialog.ShowMessage(
                LocalizationService.Get(_settings.Language, "Error"),
                LocalizationService.Get(_settings.Language, "ChatLogOpenFolderFailed"));
        }
    }

    private async void ExportTxt_Click(object sender, RoutedEventArgs e)
    {
        _exportTask = ExportAsync(
            "chat.txt",
            $"{LocalizationService.Get(_settings.Language, "TextLogFileFilter")}|*.txt",
            ".txt");
        await _exportTask;
    }

    private async void ExportJsonl_Click(object sender, RoutedEventArgs e)
    {
        _exportTask = ExportAsync(
            "chat.jsonl",
            $"{LocalizationService.Get(_settings.Language, "JsonLinesFileFilter")}|*.jsonl",
            ".jsonl");
        await _exportTask;
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

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _exportCts, cts)?.Cancel();
        var token = cts.Token;
        IsEnabled = false;
        try
        {
            await _chatLogService.FlushAsync().WaitAsync(token).ConfigureAwait(true);
            await ChatLogService.ExportAsync(session, fileName, dialog.FileName, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested && IsLoaded)
            {
                StatusText.Text = dialog.FileName;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_hostCancellationRequested && IsLoaded)
            {
                SilentDialog.ShowMessage(
                    LocalizationService.Get(_settings.Language, "Error"),
                    LocalizationService.Get(_settings.Language, "ChatLogExportFailed"));
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _exportCts, null, cts);
            cts.Dispose();
            if (!_hostCancellationRequested && IsLoaded)
            {
                IsEnabled = true;
            }
        }
    }

    private async void DeleteLog_Click(object sender, RoutedEventArgs e)
    {
        var deletion = DeleteSelectedLogAsync();
        _deleteTask = deletion;
        await deletion.ConfigureAwait(true);
    }

    private async Task DeleteSelectedLogAsync()
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

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _deleteCts, cts)?.Cancel();
        var token = cts.Token;
        IsEnabled = false;
        try
        {
            await CancelAndAwaitLoadingAsync().ConfigureAwait(true);
            await _chatLogService.DeleteSessionAsync(_settings, session, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            LoadChannels();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException ex)
        {
            SilentDialog.ShowMessage(LocalizationService.Get(_settings.Language, "Error"), ex.Message);
        }
        catch (Exception)
        {
            SilentDialog.ShowMessage(
                LocalizationService.Get(_settings.Language, "Error"),
                LocalizationService.Get(_settings.Language, "ChatLogDeleteFailed"));
        }
        finally
        {
            Interlocked.CompareExchange(ref _deleteCts, null, cts);
            cts.Dispose();
            if (!_hostCancellationRequested && IsLoaded)
            {
                IsEnabled = true;
            }
        }
    }

    private async Task CancelAndAwaitLoadingAsync()
    {
        CancelFilterDebounce();
        try
        {
            await _filterDebounceTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }

        Volatile.Read(ref _loadCts)?.Cancel();
        try
        {
            await _loadTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
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
}
