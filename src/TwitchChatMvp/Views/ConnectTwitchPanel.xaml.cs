using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TwitchChatMvp.Models;
using TwitchChatMvp.Services;

namespace TwitchChatMvp.Views;

public sealed class ConnectTwitchPanelResult
{
    public bool Accepted { get; init; }
    public ChatConnectionMode SelectedMode { get; init; } = ChatConnectionMode.SignedIn;
    public string ChannelLogin { get; init; } = string.Empty;
}

public partial class ConnectTwitchPanel : UserControl
{
    private readonly string _language;
    private readonly string _lastReadOnlyChannel;
    private readonly IChannelSearchService? _channelSearchService;
    private CancellationTokenSource? _searchCts;
    private bool _selectingSuggestion;

    public ConnectTwitchPanel(
        string language,
        string lastReadOnlyChannel,
        IChannelSearchService? channelSearchService = null)
    {
        _language = LocalizationService.NormalizeLanguage(language);
        _lastReadOnlyChannel = NormalizeLogin(lastReadOnlyChannel);
        _channelSearchService = channelSearchService;
        LocalizationService.ApplyToResources(_language);
        InitializeComponent();
        ChannelLoginText.Text = _lastReadOnlyChannel;
        Loaded += (_, _) =>
        {
            ChannelLoginText.Focus();
            ChannelLoginText.SelectAll();
        };
        Unloaded += (_, _) => CancelSearch();
    }

    public event EventHandler<ConnectTwitchPanelResult>? Completed;

    public void CancelFromHost()
    {
        Completed?.Invoke(this, new ConnectTwitchPanelResult { Accepted = false });
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        Completed?.Invoke(this, new ConnectTwitchPanelResult
        {
            Accepted = true,
            SelectedMode = ChatConnectionMode.SignedIn
        });
    }

    private void WatchOnly_Click(object sender, RoutedEventArgs e)
    {
        CancelSearch();
        ChannelSuggestionsPopup.IsOpen = false;
        var login = (ChannelLoginText.Text ?? string.Empty).Trim().TrimStart('@', '#');
        if (string.IsNullOrWhiteSpace(login))
        {
            ErrorText.Text = LocalizationService.Get(_language, "TwitchChannelNameRequired");
            ErrorText.Visibility = Visibility.Visible;
            ChannelLoginText.Focus();
            return;
        }

        Completed?.Invoke(this, new ConnectTwitchPanelResult
        {
            Accepted = true,
            SelectedMode = ChatConnectionMode.ReadOnly,
            ChannelLogin = login
        });
    }

    private async void ChannelLoginText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_selectingSuggestion)
        {
            return;
        }

        CancelSearch();
        var query = NormalizeLogin(ChannelLoginText.Text);
        if (query.Length < 2)
        {
            ChannelSuggestionsPopup.IsOpen = false;
            ChannelSearchProgress.Visibility = Visibility.Collapsed;
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        ChannelSearchProgress.Visibility = Visibility.Visible;

        try
        {
            await Task.Delay(350, cts.Token);
            var results = new List<ChannelSearchResult>();
            var onlineUnavailable = _channelSearchService?.IsOnlineSearchAvailable != true;

            if (!onlineUnavailable)
            {
                try
                {
                    results.AddRange(await _channelSearchService!.SearchChannelsAsync(query, cts.Token));
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Network errors must not block manual channel entry.
                }
            }

            if (!string.IsNullOrWhiteSpace(_lastReadOnlyChannel) &&
                _lastReadOnlyChannel.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                results.All(item => !string.Equals(item.BroadcasterLogin, _lastReadOnlyChannel, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new ChannelSearchResult
                {
                    BroadcasterLogin = _lastReadOnlyChannel,
                    DisplayName = _lastReadOnlyChannel
                });
            }

            var suggestions = results
                .GroupBy(item => item.BroadcasterLogin, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => !string.Equals(item.BroadcasterLogin, query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => !item.BroadcasterLogin.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .Take(6)
                .ToArray();

            cts.Token.ThrowIfCancellationRequested();
            ChannelSuggestionsList.ItemsSource = suggestions;
            ChannelSuggestionsList.SelectedIndex = suggestions.Length > 0 ? 0 : -1;

            ChannelSearchStatus.Text = onlineUnavailable
                ? LocalizationService.Get(_language, "ChannelSearchUnavailableWithoutAuth")
                : suggestions.Length == 0
                    ? LocalizationService.Get(_language, "ChannelSearchNoResults")
                    : string.Empty;
            ChannelSearchStatus.Visibility = string.IsNullOrEmpty(ChannelSearchStatus.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            ChannelSuggestionsPopup.IsOpen = suggestions.Length > 0 || ChannelSearchStatus.Visibility == Visibility.Visible;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                ChannelSearchProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ChannelLoginText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ChannelSuggestionsPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (ChannelSuggestionsList.Items.Count == 0)
        {
            return;
        }

        if (e.Key == Key.Down)
        {
            ChannelSuggestionsPopup.IsOpen = true;
            ChannelSuggestionsList.SelectedIndex = Math.Min(ChannelSuggestionsList.Items.Count - 1, ChannelSuggestionsList.SelectedIndex + 1);
            ChannelSuggestionsList.ScrollIntoView(ChannelSuggestionsList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            ChannelSuggestionsPopup.IsOpen = true;
            ChannelSuggestionsList.SelectedIndex = Math.Max(0, ChannelSuggestionsList.SelectedIndex - 1);
            ChannelSuggestionsList.ScrollIntoView(ChannelSuggestionsList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ChannelSuggestionsPopup.IsOpen && ChannelSuggestionsList.SelectedItem is ChannelSearchResult selected)
        {
            SelectSuggestion(selected);
            e.Handled = true;
        }
    }

    private void ChannelSuggestionsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var item = ItemsControl.ContainerFromElement(ChannelSuggestionsList, source) as ListBoxItem;
        if (item?.DataContext is ChannelSearchResult selected)
        {
            SelectSuggestion(selected);
        }
    }

    private void SelectSuggestion(ChannelSearchResult selected)
    {
        CancelSearch();
        _selectingSuggestion = true;
        ChannelLoginText.Text = selected.BroadcasterLogin;
        ChannelLoginText.CaretIndex = ChannelLoginText.Text.Length;
        _selectingSuggestion = false;
        ChannelSuggestionsPopup.IsOpen = false;
        ChannelLoginText.Focus();
    }

    private void CancelSearch()
    {
        var cts = _searchCts;
        _searchCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private static string NormalizeLogin(string? value) =>
        (value ?? string.Empty).Trim().TrimStart('@', '#');
}
