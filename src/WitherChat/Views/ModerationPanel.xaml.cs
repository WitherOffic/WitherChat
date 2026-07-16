using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WitherChat.Models;
using WitherChat.Services;
using WitherChat.ViewModels;

namespace WitherChat.Views;

public partial class ModerationPanel : UserControl
{
    private readonly ChatViewModel _viewModel;
    private ChannelSessionViewModel? _observedSession;

    public ModerationPanel(ChatViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += ModerationPanel_Loaded;
        Unloaded += ModerationPanel_Unloaded;
    }

    public event EventHandler? CloseRequested;

    private async void ModerationPanel_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        ObserveActiveSession();
        RefreshStatus();
        await ObserveAsync(_viewModel.RefreshBannedUsersAsync()).ConfigureAwait(true);
        await ObserveAsync(_viewModel.RefreshUnbanRequestsAsync()).ConfigureAwait(true);
    }

    private void ModerationPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_observedSession is not null)
        {
            _observedSession.PendingAutoModMessages.CollectionChanged -= PendingAutoModMessages_CollectionChanged;
            _observedSession = null;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.ActiveChannel))
        {
            CloseRecentMessages();
            ObserveActiveSession();
            RefreshStatus();
            _ = ObserveAsync(_viewModel.RefreshBannedUsersAsync());
            _ = ObserveAsync(_viewModel.RefreshUnbanRequestsAsync());
        }
    }

    private void ObserveActiveSession()
    {
        if (_observedSession is not null)
        {
            _observedSession.PendingAutoModMessages.CollectionChanged -= PendingAutoModMessages_CollectionChanged;
        }
        _observedSession = _viewModel.ActiveChannel;
        if (_observedSession is not null)
        {
            _observedSession.PendingAutoModMessages.CollectionChanged += PendingAutoModMessages_CollectionChanged;
        }
        ApplyUnbanRequestFilter();
    }

    private void PendingAutoModMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshStatus();

    private void RefreshStatus()
    {
        AutoModStatusText.Text = _viewModel.ActiveChannel is { PendingAutoModMessages.Count: > 0 }
            ? string.Empty
            : _viewModel.ActiveChannel is not { CanModerate: true }
            ? LocalizationService.Get(LocalizationService.CurrentLanguage, "NoModeratorPermissions")
            : _viewModel.HasAutoModScope
                ? LocalizationService.Get(LocalizationService.CurrentLanguage, "NoAutoModMessages")
                : LocalizationService.Get(LocalizationService.CurrentLanguage, "AutoModSignInAgain");
    }

    private async void Allow_Click(object sender, RoutedEventArgs e) =>
        await ObserveAsync(_viewModel.ManageAutoModMessageAsync((sender as FrameworkElement)?.DataContext as HeldAutoModMessage, true)).ConfigureAwait(true);

    private async void Deny_Click(object sender, RoutedEventArgs e) =>
        await ObserveAsync(_viewModel.ManageAutoModMessageAsync((sender as FrameworkElement)?.DataContext as HeldAutoModMessage, false)).ConfigureAwait(true);

    private async void RefreshBanned_Click(object sender, RoutedEventArgs e) =>
        await ObserveAsync(_viewModel.RefreshBannedUsersAsync()).ConfigureAwait(true);

    private async void LoadMoreBanned_Click(object sender, RoutedEventArgs e) =>
        await ObserveAsync(_viewModel.RefreshBannedUsersAsync(loadMore: true)).ConfigureAwait(true);

    private async void RemovePunishment_Click(object sender, RoutedEventArgs e) =>
        await ObserveAsync(_viewModel.RemovePunishmentAsync((sender as FrameworkElement)?.DataContext as BannedUserEntry)).ConfigureAwait(true);

    private async void UnbanByLogin_Click(object sender, RoutedEventArgs e) =>
        await ObserveAsync(_viewModel.UnbanByLoginAsync(UserLoginText.Text)).ConfigureAwait(true);

    private void UserLoginText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (UserLoginPlaceholder is not null)
        {
            UserLoginPlaceholder.Visibility = string.IsNullOrWhiteSpace(UserLoginText.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ModerationTab_Checked(object sender, RoutedEventArgs e)
    {
        if (ModerationTabs is not null && sender is FrameworkElement { Tag: string tabIndex } &&
            int.TryParse(tabIndex, out var index))
        {
            ModerationTabs.SelectedIndex = index;
        }
    }

    private async void RefreshUnbanRequests_Click(object sender, RoutedEventArgs e) =>
        await ObserveAsync(_viewModel.RefreshUnbanRequestsAsync(status: _viewModel.ActiveChannel?.UnbanRequestFilter ?? UnbanRequestStatus.Pending)).ConfigureAwait(true);

    private async void PendingUnbanRequests_Click(object sender, RoutedEventArgs e) =>
        await RefreshUnbanRequestFilterAsync(UnbanRequestStatus.Pending).ConfigureAwait(true);

    private async void ApprovedUnbanRequests_Click(object sender, RoutedEventArgs e) =>
        await RefreshUnbanRequestFilterAsync(UnbanRequestStatus.Approved).ConfigureAwait(true);

    private async void DeniedUnbanRequests_Click(object sender, RoutedEventArgs e) =>
        await RefreshUnbanRequestFilterAsync(UnbanRequestStatus.Denied).ConfigureAwait(true);

    private async Task RefreshUnbanRequestFilterAsync(UnbanRequestStatus status)
    {
        if (_viewModel.ActiveChannel is { } session)
        {
            session.UnbanRequestFilter = status;
            ApplyUnbanRequestFilter();
        }
        await ObserveAsync(_viewModel.RefreshUnbanRequestsAsync(status: status)).ConfigureAwait(true);
        ApplyUnbanRequestFilter();
    }

    private void ApplyUnbanRequestFilter()
    {
        if (UnbanRequestsList.ItemsSource is null)
        {
            return;
        }
        var view = CollectionViewSource.GetDefaultView(UnbanRequestsList.ItemsSource);
        var status = _viewModel.ActiveChannel?.UnbanRequestFilter ?? UnbanRequestStatus.Pending;
        if (view is not null)
        {
            view.Filter = item => item is UnbanRequestEntry request && request.Status == status;
            view.Refresh();
        }
    }

    private async void LoadMoreUnbanRequests_Click(object sender, RoutedEventArgs e) =>
        await ObserveAsync(_viewModel.RefreshUnbanRequestsAsync(
            status: _viewModel.ActiveChannel?.UnbanRequestFilter ?? UnbanRequestStatus.Pending,
            loadMore: true)).ConfigureAwait(true);

    private async void ApproveUnbanRequest_Click(object sender, RoutedEventArgs e) =>
        await ObserveAsync(_viewModel.ResolveUnbanRequestAsync((sender as FrameworkElement)?.DataContext as UnbanRequestEntry, true)).ConfigureAwait(true);

    private async void DenyUnbanRequest_Click(object sender, RoutedEventArgs e) =>
        await ObserveAsync(_viewModel.ResolveUnbanRequestAsync((sender as FrameworkElement)?.DataContext as UnbanRequestEntry, false)).ConfigureAwait(true);

    private Task ObserveAsync(Task task) => _viewModel.ObserveInteractiveTaskAsync(task);

    private void RecentMessages_Click(object sender, RoutedEventArgs e)
    {
        var user = GetModerationUser((sender as FrameworkElement)?.DataContext);
        var session = _viewModel.ActiveChannel;
        if (user is null || session is null)
        {
            return;
        }

        var messages = session.Messages
            .Where(message => MatchesUser(message, user))
            .TakeLast(50)
            .ToList();
        RecentMessagesUserText.Text = string.IsNullOrWhiteSpace(user.Login)
            ? user.DisplayName
            : $"{user.DisplayName} (@{user.Login})";
        RecentMessagesList.ItemsSource = messages;
        RecentMessagesList.Visibility = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoRecentMessagesText.Visibility = messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentMessagesPanel.Visibility = Visibility.Visible;
        if (messages.Count > 0)
        {
            RecentMessagesList.ScrollIntoView(messages[^1]);
        }
    }

    private void OpenUserOnTwitch_Click(object sender, RoutedEventArgs e)
    {
        var user = GetModerationUser((sender as FrameworkElement)?.DataContext);
        if (user is not null)
        {
            _viewModel.OpenUserOnTwitch(user.Login);
        }
    }

    private void CloseRecentMessages_Click(object sender, RoutedEventArgs e) => CloseRecentMessages();

    private void CloseRecentMessages()
    {
        RecentMessagesPanel.Visibility = Visibility.Collapsed;
        RecentMessagesList.ItemsSource = null;
    }

    private static bool MatchesUser(ChatMessageModel message, ModerationUserContext user)
    {
        if (!string.IsNullOrWhiteSpace(user.UserId) && !string.IsNullOrWhiteSpace(message.UserId))
        {
            return string.Equals(message.UserId, user.UserId, StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(user.Login) &&
               string.Equals(message.Login, user.Login, StringComparison.OrdinalIgnoreCase);
    }

    private static ModerationUserContext? GetModerationUser(object? source) => source switch
    {
        BannedUserEntry user => new ModerationUserContext(user.UserId, user.UserLogin, user.UserLabel),
        HeldAutoModMessage user => new ModerationUserContext(user.UserId, user.UserLogin, user.UserLabel),
        UnbanRequestEntry user => new ModerationUserContext(user.UserId, user.UserLogin, user.UserLabel),
        _ => null
    };

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private sealed record ModerationUserContext(string UserId, string Login, string DisplayName);
}
