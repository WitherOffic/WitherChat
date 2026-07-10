using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TwitchChatMvp;
using TwitchChatMvp.Models;
using TwitchChatMvp.Services;
using TwitchChatMvp.Views;

namespace TwitchChatMvp.ViewModels;

public sealed class ChatViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SettingsService _settingsService = new();
    private readonly SecureTokenStore _tokenStore = new();
    private readonly FileLogger _logger = new();
    private readonly DialogService _dialogs = new();
    private readonly AuthService _authService;
    private readonly TwitchApiClient _apiClient;
    private readonly TwitchEventSubClient _eventSubClient;
    private readonly ReadOnlyChatClient _readOnlyChatClient;
    private readonly ModerationService _moderationService;
    private readonly EmoteCache _emoteCache;
    private readonly TwitchBadgeService _badgeService;
    private readonly ThirdPartyEmoteService _thirdPartyEmoteService;
    private readonly StreamStatusService _streamStatusService;
    private readonly OverlayServerService _overlayServer;
    private readonly ChatLogService _chatLogService;
    private readonly DispatcherTimer _filterTimer;
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenMessageOrder = new();
    private CancellationTokenSource? _streamStatusCts;
    private CancellationTokenSource? _channelAssetsCts;

    private AppSettings _settings;
    private TwitchUser? _currentUser;
    private TwitchUser? _broadcaster;
    private ChatConnectionMode _connectionMode = ChatConnectionMode.SignedIn;
    private string _statusText = "Готово";
    private string _accountText = "Twitch не подключён";
    private string _channelText = "Мой канал";
    private string _searchText = string.Empty;
    private string _userFilter = string.Empty;
    private string _outgoingMessage = string.Empty;
    private string _profileImageUrl = string.Empty;
    private string _displayNameCompact = string.Empty;
    private string _avatarInitial = "?";
    private string _connectionStateText = "не вошли";
    private string _chatConnectionStateText = "отключён";
    private string _chatEmptyTitle = "Чат не подключён";
    private string _chatEmptyText = "Войдите через Twitch, чтобы подключить чат.";
    private string _streamStatusText = "OFFLINE";
    private bool _isStreamLive;
    private string _streamViewerText = string.Empty;
    private Brush _connectionIndicatorBrush = CreateFrozenBrush("#FF6E7482");
    private Brush _chatIndicatorBrush = CreateFrozenBrush("#FF6E7482");
    private Brush _streamIndicatorBrush = CreateFrozenBrush("#FF6E7482");
    private bool _isBusy;
    private bool _autoScroll = true;
    private bool _isConnected;
    private bool _isChatConnected;
    private bool _isConnecting;
    private bool _filtersVisible;

    public ChatViewModel()
    {
        _settings = _settingsService.Load();
        _connectionMode = Settings.ConnectionMode;
        LocalizationService.ApplyToResources(Settings.Language);
        AnimationService.SetReduceMotion(Settings.ReduceMotion);
        _authService = new AuthService(() => AppTwitchDefaults.GetClientId(Settings), _tokenStore, _logger);
        _apiClient = new TwitchApiClient(() => AppTwitchDefaults.GetClientId(Settings), _authService, _logger);
        _eventSubClient = new TwitchEventSubClient(_apiClient, _logger);
        _readOnlyChatClient = new ReadOnlyChatClient(_logger);
        _moderationService = new ModerationService(_apiClient);
        _emoteCache = new EmoteCache(_logger);
        _badgeService = new TwitchBadgeService(_apiClient, _logger);
        _thirdPartyEmoteService = new ThirdPartyEmoteService(_logger);
        _streamStatusService = new StreamStatusService(_apiClient, _logger);
        _overlayServer = new OverlayServerService(_logger);
        _chatLogService = new ChatLogService(_logger);

        Messages = new ObservableCollection<ChatMessageModel>();
        FilteredMessages = CollectionViewSource.GetDefaultView(Messages);
        FilteredMessages.Filter = FilterMessage;

        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            FilteredMessages.Refresh();
        };

        _eventSubClient.MessageReceived += OnEventSubMessageReceived;
        _eventSubClient.StatusChanged += OnEventSubStatusChanged;
        _readOnlyChatClient.MessageReceived += OnReadOnlyMessageReceived;
        _readOnlyChatClient.StatusChanged += OnReadOnlyStatusChanged;
        _readOnlyChatClient.ChannelIdentityResolved += OnReadOnlyChannelIdentityResolved;
        _chatLogService.WriteFailed += OnChatLogWriteFailed;

        ConnectCommand = new AsyncRelayCommand(ConnectTwitchAsync, () => !IsBusy);
        SignInCommand = new AsyncRelayCommand(SignInWithTwitchAsync, () => !IsBusy);
        ReconnectCommand = new AsyncRelayCommand(ReconnectChatAsync, () => !IsBusy && IsConnected);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, () => !IsBusy);
        OpenChatLogsCommand = new AsyncRelayCommand(OpenChatLogsAsync, () => !IsBusy);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => !IsBusy && IsConnected);
        SendMessageCommand = new AsyncRelayCommand(SendMessageAsync, () => !IsBusy && CanSendMessages && !string.IsNullOrWhiteSpace(OutgoingMessage));
        ClearMessagesCommand = new RelayCommand(_ => ClearMessages());
        IncreaseFontCommand = new RelayCommand(_ => ChangeFontSize(1));
        DecreaseFontCommand = new RelayCommand(_ => ChangeFontSize(-1));
        ToggleFiltersCommand = new RelayCommand(_ => FiltersVisible = !FiltersVisible);
    }

    public ObservableCollection<ChatMessageModel> Messages { get; }
    public ICollectionView FilteredMessages { get; }

    public ICommand ConnectCommand { get; }
    public ICommand SignInCommand { get; }
    public ICommand ReconnectCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenChatLogsCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand SendMessageCommand { get; }
    public ICommand ClearMessagesCommand { get; }
    public ICommand IncreaseFontCommand { get; }
    public ICommand DecreaseFontCommand { get; }
    public ICommand ToggleFiltersCommand { get; }

    public AppSettings Settings => _settings;

    public ChatConnectionMode ConnectionMode
    {
        get => _connectionMode;
        private set
        {
            if (SetProperty(ref _connectionMode, value))
            {
                Settings.ConnectionMode = value;
                OnPropertyChanged(nameof(IsSignedIn));
                OnPropertyChanged(nameof(IsReadOnlyMode));
                OnPropertyChanged(nameof(CanSendMessages));
                OnPropertyChanged(nameof(CanModerate));
                OnPropertyChanged(nameof(CanUseCreatorControls));
                OnPropertyChanged(nameof(ShowMessageComposer));
                OnPropertyChanged(nameof(ShowReadOnlyComposerNotice));
                OnPropertyChanged(nameof(ReadOnlyComposerText));
                OnPropertyChanged(nameof(DisconnectButtonToolTip));
                OnPropertyChanged(nameof(HeaderSubtitle));
                RaiseCommandState();
            }
        }
    }

    public bool IsSignedIn => IsConnected && ConnectionMode == ChatConnectionMode.SignedIn;

    public bool IsReadOnlyMode => IsConnected && ConnectionMode == ChatConnectionMode.ReadOnly;

    public bool CanSendMessages => IsSignedIn && IsChatConnected;

    public bool CanModerate => IsSignedIn && _currentUser is not null && _broadcaster is not null;

    public bool CanUseCreatorControls => CanModerate;

    public bool ShowMessageComposer => CanSendMessages || ConnectionMode == ChatConnectionMode.SignedIn;

    public bool ShowReadOnlyComposerNotice => IsReadOnlyMode;

    public string ReadOnlyComposerText => L("WatchOnlySendingUnavailable");

    public string DisconnectButtonToolTip => IsReadOnlyMode ? L("DisconnectFromChannel") : L("Logout");

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string AccountText
    {
        get => _accountText;
        private set => SetProperty(ref _accountText, value);
    }

    public string ChannelText
    {
        get => _channelText;
        private set => SetProperty(ref _channelText, value);
    }

    public string HeaderTitle => IsConnected
        ? (string.IsNullOrWhiteSpace(DisplayNameCompact) ? AppInfo.Name : DisplayNameCompact)
        : AppInfo.Name;

    public string HeaderSubtitle => ConnectionMode == ChatConnectionMode.ReadOnly
        ? L("WatchOnlyMode")
        : IsConnected ? L("AccountSignedIn") : L("AccountSignedOut");

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ScheduleFilterRefresh();
            }
        }
    }

    public string UserFilter
    {
        get => _userFilter;
        set
        {
            if (SetProperty(ref _userFilter, value))
            {
                ScheduleFilterRefresh();
            }
        }
    }

    public string OutgoingMessage
    {
        get => _outgoingMessage;
        set
        {
            if (SetProperty(ref _outgoingMessage, value))
            {
                OnPropertyChanged(nameof(SendButtonToolTip));
                (SendMessageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string ProfileImageUrl
    {
        get => _profileImageUrl;
        private set
        {
            if (SetProperty(ref _profileImageUrl, value))
            {
                OnPropertyChanged(nameof(HasProfileImage));
                OnPropertyChanged(nameof(ShowAvatarPlaceholder));
            }
        }
    }

    public bool HasProfileImage => IsConnected && !string.IsNullOrWhiteSpace(ProfileImageUrl);

    public bool ShowAvatarPlaceholder => IsConnected && !HasProfileImage;

    public string DisplayNameCompact
    {
        get => _displayNameCompact;
        private set
        {
            if (SetProperty(ref _displayNameCompact, value))
            {
                OnPropertyChanged(nameof(HeaderTitle));
            }
        }
    }

    public string AvatarInitial
    {
        get => _avatarInitial;
        private set => SetProperty(ref _avatarInitial, value);
    }

    public string ConnectionStateText
    {
        get => _connectionStateText;
        private set
        {
            if (SetProperty(ref _connectionStateText, value))
            {
                OnPropertyChanged(nameof(HeaderSubtitle));
            }
        }
    }

    public Brush ConnectionIndicatorBrush
    {
        get => _connectionIndicatorBrush;
        private set => SetProperty(ref _connectionIndicatorBrush, value);
    }

    public string ChatConnectionStateText
    {
        get => _chatConnectionStateText;
        private set => SetProperty(ref _chatConnectionStateText, value);
    }

    public Brush ChatIndicatorBrush
    {
        get => _chatIndicatorBrush;
        private set => SetProperty(ref _chatIndicatorBrush, value);
    }

    public string ChatEmptyTitle
    {
        get => _chatEmptyTitle;
        private set => SetProperty(ref _chatEmptyTitle, value);
    }

    public string ChatEmptyText
    {
        get => _chatEmptyText;
        private set => SetProperty(ref _chatEmptyText, value);
    }

    public string SendButtonToolTip
    {
        get
        {
            if (!IsConnected)
            {
                return L("SignInFirst");
            }

            if (ConnectionMode == ChatConnectionMode.ReadOnly)
            {
                return L("WatchOnlySendingUnavailable");
            }

            if (!IsChatConnected)
            {
                return L("ChatNotConnected");
            }

            return string.IsNullOrWhiteSpace(OutgoingMessage)
                ? L("EnterSendTooltip")
                : L("Send");
        }
    }
    public string StreamStatusText
    {
        get => _streamStatusText;
        private set => SetProperty(ref _streamStatusText, value);
    }

    public string StreamViewerText
    {
        get => _streamViewerText;
        private set => SetProperty(ref _streamViewerText, value);
    }

    public Brush StreamIndicatorBrush
    {
        get => _streamIndicatorBrush;
        private set => SetProperty(ref _streamIndicatorBrush, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(HasProfileImage));
                OnPropertyChanged(nameof(ShowAvatarPlaceholder));
                OnPropertyChanged(nameof(ShowChatEmptyState));
                OnPropertyChanged(nameof(SendButtonToolTip));
                OnPropertyChanged(nameof(HeaderTitle));
                OnPropertyChanged(nameof(HeaderSubtitle));
                OnPropertyChanged(nameof(IsSignedIn));
                OnPropertyChanged(nameof(IsReadOnlyMode));
                OnPropertyChanged(nameof(CanSendMessages));
                OnPropertyChanged(nameof(CanModerate));
                OnPropertyChanged(nameof(CanUseCreatorControls));
                OnPropertyChanged(nameof(ShowMessageComposer));
                OnPropertyChanged(nameof(ShowReadOnlyComposerNotice));
                OnPropertyChanged(nameof(DisconnectButtonToolTip));
                RaiseCommandState();
            }
        }
    }

    public bool IsChatConnected
    {
        get => _isChatConnected;
        private set
        {
            if (SetProperty(ref _isChatConnected, value))
            {
                OnPropertyChanged(nameof(SendButtonToolTip));
                OnPropertyChanged(nameof(CanSendMessages));
                OnPropertyChanged(nameof(ShowMessageComposer));
                OnPropertyChanged(nameof(ShowReadOnlyComposerNotice));
                RaiseCommandState();
            }
        }
    }

    public bool HasMessages => Messages.Count > 0;

    public bool ShowChatEmptyState => IsConnected && !HasMessages;

    public bool IsConnecting
    {
        get => _isConnecting;
        private set => SetProperty(ref _isConnecting, value);
    }

    public bool FiltersVisible
    {
        get => _filtersVisible;
        set => SetProperty(ref _filtersVisible, value);
    }

    public double FontSize
    {
        get => Settings.FontSize;
        set
        {
            var clamped = Math.Clamp(value, 10, 32);
            if (Math.Abs(Settings.FontSize - clamped) > 0.01)
            {
                Settings.FontSize = clamped;
                _settingsService.Save(Settings);
                OnPropertyChanged();
            }
        }
    }

    public bool ShowTimestamps
    {
        get => Settings.ShowTimestamps;
        set
        {
            if (Settings.ShowTimestamps != value)
            {
                Settings.ShowTimestamps = value;
                _settingsService.Save(Settings);
                OnPropertyChanged();
            }
        }
    }

    public bool ShowBadges
    {
        get => Settings.EnableBadges;
        set
        {
            if (Settings.EnableBadges != value)
            {
                Settings.EnableBadges = value;
                _settingsService.Save(Settings);
                OnPropertyChanged();
            }
        }
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandState();
            }
        }
    }

    public async Task InitializeAsync()
    {
        ApplyTheme();
        await ConfigureOverlayAsync(showErrors: false).ConfigureAwait(true);
        UpdateAccountState("not signed in");
        UpdateChatState("disconnected");
        UpdateStreamStatus(new StreamStatusInfo(false, 0, string.Empty));
        if (!AppTwitchDefaults.IsClientIdConfigured(Settings))
        {
            StatusText = "Release Twitch Client ID is not configured";
            return;
        }

        IsBusy = true;
        try
        {
            if (Settings.ConnectionMode == ChatConnectionMode.ReadOnly &&
                !string.IsNullOrWhiteSpace(Settings.LastReadOnlyChannel))
            {
                await ConnectReadOnlyChannelAsync(Settings.LastReadOnlyChannel, saveSettings: false).ConfigureAwait(true);
                return;
            }

            var token = await _authService.TryRestoreSessionAsync().ConfigureAwait(true);
            if (token is null)
            {
                StatusText = "Sign in with Twitch to connect chat";
                return;
            }

            await LoadIdentityAndConnectAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("Initialization failed", ex);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ConnectTwitchAsync()
    {
        ConnectTwitchPanelResult? result = null;
        if (Application.Current.MainWindow is MainWindow mainWindow)
        {
            result = await mainWindow.OpenConnectTwitchPanelAsync(Settings.Language, Settings.LastReadOnlyChannel, _apiClient).ConfigureAwait(true);
        }
        else
        {
            var dialog = new ConnectTwitchDialog(Settings.Language, Settings.LastReadOnlyChannel);
            if (dialog.ShowDialog() == true)
            {
                result = new ConnectTwitchPanelResult
                {
                    Accepted = true,
                    SelectedMode = dialog.SelectedMode,
                    ChannelLogin = dialog.ChannelLogin
                };
            }
        }

        if (result?.Accepted != true)
        {
            return;
        }

        if (result.SelectedMode == ChatConnectionMode.ReadOnly)
        {
            await ConnectReadOnlyChannelAsync(result.ChannelLogin, saveSettings: true).ConfigureAwait(true);
            return;
        }

        await SignInWithTwitchAsync().ConfigureAwait(true);
    }

    public async Task SignInWithTwitchAsync()
    {
        if (!AppTwitchDefaults.IsClientIdConfigured(Settings))
        {
            _dialogs.ShowError(
                "Twitch Client ID",
                "This build does not include a Twitch Client ID yet. Release builders must set AppTwitchDefaults.ClientId. Developers can enable a custom Client ID in Advanced settings.");
            await OpenSettingsAsync().ConfigureAwait(true);
            return;
        }

        if (!ValidateOAuthAndOverlayPorts(Settings))
        {
            return;
        }

        IsBusy = true;
        try
        {
            UpdateAccountState("signing in");
            if (ConnectionMode != ChatConnectionMode.ReadOnly)
            {
                UpdateChatState("disconnected");
            }

            StatusText = "Opening Twitch login...";
            await _authService.SignInWithImplicitGrantAsync(
                Settings.RedirectUri,
                _dialogs.OpenUrl,
                status => Application.Current.Dispatcher.InvokeAsync(() => StatusText = status),
                forceVerify: false).ConfigureAwait(true);

            StatusText = "Twitch connected";
            await _readOnlyChatClient.StopAsync().ConfigureAwait(true);
            ConnectionMode = ChatConnectionMode.SignedIn;
            Settings.ConnectionMode = ChatConnectionMode.SignedIn;
            _settingsService.Save(Settings);
            await LoadIdentityAndConnectAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            if (!IsConnected)
            {
                UpdateAccountState("not signed in");
            }

            StatusText = "Twitch login canceled";
        }
        catch (OAuthPortUnavailableException ex)
        {
            _logger.Error("Twitch OAuth port unavailable", ex);
            var message = L("OAuthPortBusy") + Environment.NewLine + ex.RedirectUri;
            _dialogs.ShowError(L("TwitchOAuthPortBusyTitle"), message);
            StatusText = message;
            if (!IsConnected)
            {
                UpdateAccountState("not signed in");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Twitch connect failed", ex);
            _dialogs.ShowError("Twitch OAuth", ex.Message);
            StatusText = ex.Message;
            if (!IsConnected)
            {
                UpdateAccountState("not signed in");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConnectReadOnlyChannelAsync(string channelLogin, bool saveSettings)
    {
        var login = (channelLogin ?? string.Empty).Trim().TrimStart('@', '#').ToLowerInvariant();
        if (!Regex.IsMatch(login, "^[a-z0-9_]{1,25}$", RegexOptions.CultureInvariant))
        {
            _dialogs.ShowError(L("ConnectTwitch"), L("TwitchChannelNameRequired"));
            return;
        }

        IsBusy = true;
        try
        {
            await _eventSubClient.StopAsync().ConfigureAwait(true);
            StopStreamStatusPolling();
            CancelChannelAssetRefresh();
            _thirdPartyEmoteService.Clear();
            _authService.Logout();
            _currentUser = null;
            _broadcaster = new TwitchUser
            {
                Login = login,
                DisplayName = login
            };

            Settings.LastReadOnlyChannel = login;
            Settings.ConnectionMode = ChatConnectionMode.ReadOnly;
            ConnectionMode = ChatConnectionMode.ReadOnly;
            if (saveSettings)
            {
                _settingsService.Save(Settings);
            }

            ProfileImageUrl = string.Empty;
            DisplayNameCompact = login;
            AvatarInitial = CreateAvatarInitial(login, login);
            AccountText = L("WatchOnlyMode");
            ChannelText = $"Chat channel: @{login}";
            IsConnected = true;
            OutgoingMessage = string.Empty;
            FiltersVisible = false;
            UpdateAccountState("read only");
            UpdateStreamStatus(new StreamStatusInfo(false, 0, string.Empty));
            await StartChatLogSessionAsync(new StreamStatusInfo(false, 0, string.Empty)).ConfigureAwait(true);
            UpdateChatState("connecting", L("ChatConnecting"));
            StatusText = "Starting read-only chat connect...";

            await _readOnlyChatClient.StartAsync(login).ConfigureAwait(true);
            var connected = await _readOnlyChatClient.WaitForInitialConnectionAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(true);
            if (!connected && !IsChatConnected)
            {
                UpdateChatState("error", L("WatchOnlyConnectFailed"));
                StatusText = L("WatchOnlyConnectFailed");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Read-only chat connect failed", ex);
            UpdateChatState("error", L("WatchOnlyConnectFailed"));
            _dialogs.ShowError(L("ConnectTwitch"), L("WatchOnlyConnectFailed") + Environment.NewLine + ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ReconnectChatAsync()
    {
        _logger.Info("Reconnect requested.");
        try
        {
            if (ConnectionMode == ChatConnectionMode.ReadOnly)
            {
                var channel = !string.IsNullOrWhiteSpace(Settings.LastReadOnlyChannel)
                    ? Settings.LastReadOnlyChannel
                    : _broadcaster?.Login ?? string.Empty;
                await ConnectReadOnlyChannelAsync(channel, saveSettings: true).ConfigureAwait(true);
                return;
            }

            await _readOnlyChatClient.StopAsync().ConfigureAwait(true);
            if (_currentUser is null || _broadcaster is null)
            {
                if (_authService.CurrentToken is null)
                {
                    await _authService.TryRestoreSessionAsync().ConfigureAwait(true);
                }

                await LoadIdentityOnlyAsync().ConfigureAwait(true);
            }

            if (_currentUser is null || _broadcaster is null)
            {
                StatusText = "Sign in with Twitch first";
                UpdateChatState("disconnected", "Войдите через Twitch, чтобы подключить чат.");
                return;
            }

            UpdateChatState("connecting", "Подключаюсь к Twitch EventSub...");
            StatusText = "Starting chat connect...";
            await StartChatLogSessionAsync().ConfigureAwait(true);
            await _eventSubClient.StartAsync(_broadcaster.Id, _currentUser.Id).ConfigureAwait(true);
            _ = RefreshChannelAssetsInBackgroundAsync();

            var connected = await _eventSubClient.WaitForInitialConnectionAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(true);
            if (!connected && !IsChatConnected)
            {
                UpdateChatState("error", "Не удалось подключить чат. Нажмите reconnect или откройте logs.");
                StatusText = "Chat connect timed out";
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Chat connect failed", ex);
            UpdateChatState("error", "Не удалось подключить чат: " + ex.Message);
            StatusText = ex.Message;
        }
    }

    public async Task OpenSettingsAsync()
    {
        var previous = Settings.Clone();
        var previousClientId = AppTwitchDefaults.GetClientId(previous);
        var editable = Settings.Clone();
        SettingsPanelResult? result = null;
        if (Application.Current.MainWindow is MainWindow mainWindow)
        {
            result = await mainWindow.OpenSettingsPanelAsync(
                editable,
                TestOverlayFromSettingsAsync,
                isSignedIn: IsSignedIn,
                isReadOnlyMode: IsReadOnlyMode,
                accountDisplayName: _currentUser?.DisplayName ?? string.Empty,
                accountLogin: _currentUser?.Login ?? string.Empty,
                accountProfileImageUrl: _currentUser?.ProfileImageUrl ?? string.Empty,
                readOnlyChannel: _broadcaster?.Login ?? Settings.LastReadOnlyChannel).ConfigureAwait(true);
        }
        else
        {
            var window = new SettingsWindow(
                editable,
                TestOverlayFromSettingsAsync,
                isSignedIn: IsSignedIn,
                isReadOnlyMode: IsReadOnlyMode,
                accountDisplayName: _currentUser?.DisplayName ?? string.Empty,
                accountLogin: _currentUser?.Login ?? string.Empty,
                accountProfileImageUrl: _currentUser?.ProfileImageUrl ?? string.Empty,
                readOnlyChannel: _broadcaster?.Login ?? Settings.LastReadOnlyChannel);

            if (window.ShowDialog() == true)
            {
                result = new SettingsPanelResult
                {
                    Accepted = true,
                    LogoutRequested = window.LogoutRequested,
                    ReconnectRequested = window.ReconnectRequested,
                    SignInRequested = window.SignInRequested,
                    ChangeWatchChannelRequested = window.ChangeWatchChannelRequested
                };
            }
        }

        if (result?.Accepted != true)
        {
            LocalizationService.ApplyToResources(Settings.Language);
            RefreshLocalizedText();
            await ConfigureOverlayAsync(showErrors: false).ConfigureAwait(true);
            return;
        }

        editable.Normalize();
        if (!ValidateOAuthAndOverlayPorts(editable))
        {
            return;
        }

        CopySettings(editable, Settings);
        _settingsService.Save(Settings);
        LocalizationService.ApplyToResources(Settings.Language);
        AnimationService.SetReduceMotion(Settings.ReduceMotion);
        ApplyTheme();
        RefreshLocalizedText();
        TrimMessagesToLimit();
        OnPropertyChanged(nameof(FontSize));
        OnPropertyChanged(nameof(ShowTimestamps));
        OnPropertyChanged(nameof(ShowBadges));
        StatusText = L("SettingsSaved");
        await ConfigureOverlayAsync(showErrors: true).ConfigureAwait(true);
        if (HasChatLogSettingsChanged(previous, Settings) && IsConnected && _broadcaster is not null)
        {
            await StartChatLogSessionAsync().ConfigureAwait(true);
        }

        if (result.LogoutRequested)
        {
            await LogoutAsync().ConfigureAwait(true);
            return;
        }

        if (result.SignInRequested)
        {
            await SignInWithTwitchAsync().ConfigureAwait(true);
            return;
        }

        if (result.ChangeWatchChannelRequested)
        {
            await ConnectReadOnlyChannelAsync(Settings.LastReadOnlyChannel, saveSettings: true).ConfigureAwait(true);
            return;
        }

        var clientChanged = !string.Equals(previousClientId, AppTwitchDefaults.GetClientId(Settings), StringComparison.Ordinal);
        var channelChanged =
            previous.UseCustomChannel != Settings.UseCustomChannel ||
            !string.Equals(previous.ChannelLogin, Settings.ChannelLogin, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previous.BroadcasterId, Settings.BroadcasterId, StringComparison.Ordinal);
        var emoteSettingsChanged =
            previous.EnableTwitchEmotes != Settings.EnableTwitchEmotes ||
            previous.EnableBttvEmotes != Settings.EnableBttvEmotes ||
            previous.EnableSevenTvEmotes != Settings.EnableSevenTvEmotes;

        if (clientChanged)
        {
            await LogoutAsync().ConfigureAwait(true);
            StatusText = "Client ID changed. Sign in with Twitch again.";
            return;
        }

        if (result.ReconnectRequested || channelChanged || emoteSettingsChanged)
        {
            await LoadIdentityAndConnectAsync().ConfigureAwait(true);
        }
    }

    private async Task OpenChatLogsAsync()
    {
        if (Application.Current.MainWindow is MainWindow mainWindow)
        {
            await mainWindow.OpenChatLogsPanelAsync(Settings.Clone()).ConfigureAwait(true);
            return;
        }

        var window = new ChatLogsWindow(Settings.Clone());
        window.Show();
    }

    public async Task SendMessageAsync()
    {
        var text = (OutgoingMessage ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!CanSendMessages)
        {
            _dialogs.ShowError("Twitch", ConnectionMode == ChatConnectionMode.ReadOnly
                ? L("WatchOnlySendingUnavailable")
                : L("SignInFirst"));
            return;
        }

        if (text.Length > 500)
        {
            _dialogs.ShowError("Twitch", L("MessageTooLong"));
            return;
        }

        if (_currentUser is null || _broadcaster is null)
        {
            _dialogs.ShowError("Twitch", L("SignInFirst"));
            return;
        }

        if (!IsChatConnected)
        {
            _dialogs.ShowError("Twitch", L("ChatNotConnected"));
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _apiClient.SendChatMessageAsync(_broadcaster.Id, _currentUser.Id, text).ConfigureAwait(true);
            if (!result.IsSent)
            {
                _dialogs.ShowError("Twitch rejected the message", result.DropMessage ?? result.DropCode ?? "No reason was provided.");
                return;
            }

            var localMessage = new ChatMessageModel
            {
                Id = result.MessageId,
                Timestamp = DateTimeOffset.Now,
                UserId = _currentUser.Id,
                Login = _currentUser.Login,
                DisplayName = _currentUser.DisplayName,
                Text = text,
                Color = "#CFA8FF",
                IsLocalEcho = true
            };

            localMessage.Parts.Add(ChatMessagePartModel.TextPart(text));
            await PrepareMessageForDisplayAsync(localMessage).ConfigureAwait(true);
            AddMessage(localMessage);
            OutgoingMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Error("Send chat message failed", ex);
            _dialogs.ShowError("Twitch", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConfigureOverlayAsync(bool showErrors)
    {
        try
        {
            await _overlayServer.ConfigureAsync(Settings).ConfigureAwait(true);
            if (Settings.EnableObsOverlay)
            {
                StatusText = $"OBS overlay: {Settings.OverlayUrl}";
            }
        }
        catch (Exception ex)
        {
            _logger.Error("OBS overlay start failed", ex);
            StatusText = ex.Message;
            if (showErrors)
            {
                _dialogs.ShowError("OBS Overlay", ex.Message);
            }
        }
    }

    private async Task<string> TestOverlayFromSettingsAsync(AppSettings settings)
    {
        settings.Normalize();
        if (!settings.EnableObsOverlay)
        {
            return LocalizationService.Get(settings.Language, "OverlayDisabled");
        }

        await _overlayServer.ConfigureAsync(settings).ConfigureAwait(true);
        _overlayServer.PublishTestMessage();
        return string.Format(LocalizationService.Get(settings.Language, "OverlayTestSent"), settings.OverlayUrl);
    }

    public async Task BanUserAsync(ChatMessageModel? message)
    {
        if (!CanModerateMessage(message))
        {
            return;
        }

        if (!_dialogs.ConfirmPermanentBan(message!))
        {
            return;
        }

        var request = _dialogs.ShowBanReasonDialog(message!);
        if (request is null)
        {
            return;
        }

        await RunModerationAsync(message!, request, true).ConfigureAwait(true);
    }

    public async Task TimeoutUserAsync(ChatMessageModel? message, int durationSeconds)
    {
        if (!CanModerateMessage(message))
        {
            return;
        }

        var request = _dialogs.ShowTimeoutDialog(message!, durationSeconds);
        if (request is null)
        {
            return;
        }

        await RunModerationAsync(message!, request, false).ConfigureAwait(true);
    }

    public void CopyUsername(ChatMessageModel? message)
    {
        if (message is not null)
        {
            _dialogs.CopyText(message.Login);
            StatusText = "Username copied";
        }
    }

    public void CopyMessage(ChatMessageModel? message)
    {
        if (message is not null)
        {
            _dialogs.CopyText(message.Text);
            StatusText = "Message copied";
        }
    }

    public void OpenUserOnTwitch(ChatMessageModel? message)
    {
        if (message is not null && !string.IsNullOrWhiteSpace(message.Login))
        {
            _dialogs.OpenUrl("https://www.twitch.tv/" + Uri.EscapeDataString(message.Login));
        }
    }

    public async Task LogoutAsync()
    {
        await _eventSubClient.StopAsync().ConfigureAwait(true);
        await _readOnlyChatClient.StopAsync().ConfigureAwait(true);
        StopStreamStatusPolling();
        CancelChannelAssetRefresh();
        _thirdPartyEmoteService.Clear();
        await _chatLogService.StopSessionAsync().ConfigureAwait(true);
        if (ConnectionMode == ChatConnectionMode.SignedIn)
        {
            _authService.Logout();
        }

        _currentUser = null;
        _broadcaster = null;
        ConnectionMode = ChatConnectionMode.SignedIn;
        Settings.ConnectionMode = ChatConnectionMode.SignedIn;
        _settingsService.Save(Settings);
        IsConnected = false;
        IsChatConnected = false;
        IsConnecting = false;
        ProfileImageUrl = string.Empty;
        DisplayNameCompact = string.Empty;
        AvatarInitial = "?";
        FiltersVisible = false;
        AccountText = "Twitch не подключён";
        ChannelText = "Мой канал";
        StatusText = "Twitch disconnected";
        UpdateAccountState("not signed in");
        UpdateChatState("disconnected");
        UpdateStreamStatus(new StreamStatusInfo(false, 0, string.Empty));
    }

    public async ValueTask DisposeAsync()
    {
        StopStreamStatusPolling();
        _filterTimer.Stop();
        _eventSubClient.MessageReceived -= OnEventSubMessageReceived;
        _eventSubClient.StatusChanged -= OnEventSubStatusChanged;
        _readOnlyChatClient.MessageReceived -= OnReadOnlyMessageReceived;
        _readOnlyChatClient.StatusChanged -= OnReadOnlyStatusChanged;
        _readOnlyChatClient.ChannelIdentityResolved -= OnReadOnlyChannelIdentityResolved;
        _chatLogService.WriteFailed -= OnChatLogWriteFailed;
        CancelChannelAssetRefresh();
        await _eventSubClient.DisposeAsync().ConfigureAwait(false);
        await _readOnlyChatClient.DisposeAsync().ConfigureAwait(false);
        await _overlayServer.DisposeAsync().ConfigureAwait(false);
        await _chatLogService.DisposeAsync().ConfigureAwait(false);
    }

    private async Task LoadIdentityAndConnectAsync()
    {
        await LoadIdentityOnlyAsync().ConfigureAwait(true);
        if (_currentUser is not null && _broadcaster is not null)
        {
            await ReconnectChatAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadIdentityOnlyAsync()
    {
        ConnectionMode = ChatConnectionMode.SignedIn;
        Settings.ConnectionMode = ChatConnectionMode.SignedIn;
        _currentUser = await _apiClient.GetCurrentUserAsync().ConfigureAwait(true);
        _authService.SaveProfile(_currentUser);
        _logger.Info($"OAuth success: user id={_currentUser.Id}, login={_currentUser.Login}");
        IsConnected = true;
        UpdateAccountState("signed in");
        ProfileImageUrl = _currentUser.ProfileImageUrl;
        DisplayNameCompact = string.IsNullOrWhiteSpace(_currentUser.DisplayName) ? _currentUser.Login : _currentUser.DisplayName;
        AvatarInitial = CreateAvatarInitial(DisplayNameCompact, _currentUser.Login);
        AccountText = $"Signed in as {_currentUser.DisplayName} (@{_currentUser.Login})";

        TwitchUser? broadcaster = null;
        if (Settings.UseCustomChannel && !string.IsNullOrWhiteSpace(Settings.ChannelLogin))
        {
            broadcaster = await _apiClient.GetUserByLoginAsync(Settings.ChannelLogin).ConfigureAwait(true);
        }
        else if (Settings.UseCustomChannel && !string.IsNullOrWhiteSpace(Settings.BroadcasterId))
        {
            broadcaster = await _apiClient.GetUserByIdAsync(Settings.BroadcasterId).ConfigureAwait(true);
        }

        broadcaster ??= _currentUser;
        _broadcaster = broadcaster;
        if (Settings.UseCustomChannel)
        {
            Settings.ChannelLogin = broadcaster.Login;
            Settings.BroadcasterId = broadcaster.Id;
        }

        _settingsService.Save(Settings);
        ChannelText = $"Chat channel: {broadcaster.DisplayName} (@{broadcaster.Login})";
        OnPropertyChanged(nameof(CanModerate));
        OnPropertyChanged(nameof(CanUseCreatorControls));
    }

    private async Task StartChatLogSessionAsync(StreamStatusInfo? knownStatus = null)
    {
        if (_broadcaster is null)
        {
            return;
        }

        var status = knownStatus ?? new StreamStatusInfo(false, 0, string.Empty);
        if (knownStatus is null && !string.IsNullOrWhiteSpace(_broadcaster.Id) && ConnectionMode == ChatConnectionMode.SignedIn)
        {
            status = await _streamStatusService.GetStatusAsync(_broadcaster.Id).ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(() => UpdateStreamStatus(status));
        }

        await _chatLogService.StartSessionAsync(Settings, _broadcaster, status).ConfigureAwait(false);
    }

    private async Task RefreshChannelAssetsAsync(CancellationToken cancellationToken = default)
    {
        if (_broadcaster is null)
        {
            return;
        }

        var broadcasterId = _broadcaster.Id;
        await Task.WhenAll(
            RunAssetStepAsync("Twitch badges", () => _badgeService.RefreshAsync(broadcasterId, cancellationToken)),
            RunAssetStepAsync("Third-party emotes", () => _thirdPartyEmoteService.RefreshAsync(
                broadcasterId,
                Settings.EnableBttvEmotes,
                Settings.EnableSevenTvEmotes,
                cancellationToken)),
            RunAssetStepAsync("Stream status", () => RefreshStreamStatusAsync(cancellationToken))).ConfigureAwait(false);

        StartStreamStatusPolling();
    }

    private async Task RefreshChannelAssetsInBackgroundAsync()
    {
        var cancellation = BeginChannelAssetRefresh();
        try
        {
            await RefreshChannelAssetsAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Channel assets refresh skipped: {ex.GetType().Name}");
        }
    }

    private async Task RefreshReadOnlyChannelAssetsAsync(string channelLogin, string broadcasterId)
    {
        var cancellation = BeginChannelAssetRefresh();
        try
        {
            await _thirdPartyEmoteService.RefreshAsync(
                broadcasterId,
                Settings.EnableBttvEmotes,
                Settings.EnableSevenTvEmotes,
                cancellation.Token).ConfigureAwait(false);

            var currentBroadcaster = _broadcaster;
            if (ConnectionMode == ChatConnectionMode.ReadOnly &&
                currentBroadcaster is not null &&
                string.Equals(currentBroadcaster.Login, channelLogin, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(currentBroadcaster.Id, broadcasterId, StringComparison.Ordinal))
            {
                _logger.Info($"Read-only channel emotes ready: broadcaster_id={broadcasterId}, cached={_thirdPartyEmoteService.Count}");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Read-only channel emotes refresh failed: {ex.GetType().Name}");
        }
    }

    private CancellationTokenSource BeginChannelAssetRefresh()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _channelAssetsCts, next);
        previous?.Cancel();
        previous?.Dispose();
        return next;
    }

    private void CancelChannelAssetRefresh()
    {
        var cancellation = Interlocked.Exchange(ref _channelAssetsCts, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task RunAssetStepAsync(string name, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"{name} refresh failed: {ex.GetType().Name}");
        }
    }

    private async Task PrepareMessageForDisplayAsync(ChatMessageModel message, CancellationToken cancellationToken = default)
    {
        PrepareMessageForDisplay(message);
        await LoadMessageImagesAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private void PrepareMessageForDisplay(ChatMessageModel message)
    {
        _badgeService.ApplyBadgeImages(message.Badges);

        var sourceParts = message.Parts.Count > 0
            ? message.Parts.ToList()
            : new List<ChatMessagePartModel> { ChatMessagePartModel.TextPart(message.Text) };

        message.Parts.Clear();
        foreach (var part in sourceParts)
        {
            if (part.Kind == ChatMessagePartKind.TwitchEmote)
            {
                if (Settings.EnableTwitchEmotes)
                {
                    message.Parts.Add(part);
                }
                else if (!string.IsNullOrEmpty(part.Text))
                {
                    message.Parts.Add(ChatMessagePartModel.TextPart(part.Text));
                }

                continue;
            }

            if (part.Kind == ChatMessagePartKind.Text && (Settings.EnableBttvEmotes || Settings.EnableSevenTvEmotes))
            {
                foreach (var renderedPart in RenderThirdPartyTextPart(part.Text))
                {
                    message.Parts.Add(renderedPart);
                }
            }
            else
            {
                message.Parts.Add(part);
            }
        }

        ApplyZeroWidthLayout(message.Parts);

    }

    private async Task LoadMessageImagesAsync(ChatMessageModel message, CancellationToken cancellationToken = default)
    {
        var imageLoadTasks = message.Parts
            .Where(part => !string.IsNullOrWhiteSpace(part.ImageUrl))
            .Select(async part =>
            {
                var media = await _emoteCache.GetMediaAsync(
                    part.CacheKey,
                    part.ImageUrl,
                    part.FallbackImageUrl,
                    cancellationToken).ConfigureAwait(false);
                return (Action)(() => part.Media = media);
            })
            .Concat(message.Badges
                .Where(badge => !string.IsNullOrWhiteSpace(badge.ImageUrl))
                .Select(async badge =>
                {
                    var image = await _emoteCache.GetImageAsync(badge.ImageUrl, cancellationToken).ConfigureAwait(false);
                    return (Action)(() => badge.ImageSource = image);
                }))
            .ToArray();

        var updates = await Task.WhenAll(imageLoadTasks).ConfigureAwait(false);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var update in updates)
            {
                update();
            }
        }, DispatcherPriority.Background);
    }

    private IEnumerable<ChatMessagePartModel> RenderThirdPartyTextPart(string text)
    {
        return ThirdPartyEmoteTokenizer.Tokenize(
            text,
            code => _thirdPartyEmoteService.TryGetEmote(code, out var emote) ? emote : null);
    }

    private static void ApplyZeroWidthLayout(IEnumerable<ChatMessagePartModel> parts)
    {
        ChatMessagePartModel? previousVisible = null;
        foreach (var part in parts)
        {
            part.OverlayPrevious = part.IsZeroWidth &&
                                   previousVisible is not null &&
                                   previousVisible.Kind != ChatMessagePartKind.Text;

            if (part.Kind != ChatMessagePartKind.Text || !string.IsNullOrWhiteSpace(part.Text))
            {
                previousVisible = part;
            }
        }
    }

    private async Task RunModerationAsync(ChatMessageModel message, ModerationRequest request, bool permanentBan)
    {
        if (_currentUser is null || _broadcaster is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (permanentBan)
            {
                await _moderationService.BanUserAsync(_broadcaster.Id, _currentUser.Id, message.UserId, request.Reason).ConfigureAwait(true);
                StatusText = $"Banned {message.UserLabel}";
                _dialogs.ShowInfo("Moderation", $"{message.UserLabel} was banned.");
            }
            else
            {
                var duration = request.DurationSeconds ?? 600;
                await _moderationService.TimeoutUserAsync(_broadcaster.Id, _currentUser.Id, message.UserId, duration, request.Reason).ConfigureAwait(true);
                StatusText = $"Timed out {message.UserLabel} for {duration / 60} minutes";
                _dialogs.ShowInfo("Moderation", $"{message.UserLabel} received a timeout.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Moderation action failed", ex);
            _dialogs.ShowError("Moderation", ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanModerateMessage(ChatMessageModel? message)
    {
        if (message is null)
        {
            return false;
        }

        if (ConnectionMode == ChatConnectionMode.ReadOnly)
        {
            _dialogs.ShowError("Moderation", L("WatchOnlyMode"));
            return false;
        }

        if (_currentUser is null || _broadcaster is null)
        {
            _dialogs.ShowError("Moderation", L("SignInFirst"));
            return false;
        }

        if (string.Equals(message.UserId, _currentUser.Id, StringComparison.Ordinal))
        {
            _dialogs.ShowError("Moderation", L("CannotModerateSelf"));
            return false;
        }

        return true;
    }

    private void OnEventSubMessageReceived(object? sender, ChatMessageModel message)
    {
        _ = HandleIncomingMessageAsync(message);
    }

    private void OnReadOnlyMessageReceived(object? sender, ChatMessageModel message)
    {
        _ = HandleIncomingMessageAsync(message);
    }

    private void OnReadOnlyChannelIdentityResolved(string channelLogin, string broadcasterId)
    {
        if (ConnectionMode != ChatConnectionMode.ReadOnly ||
            _broadcaster is null ||
            !string.Equals(_broadcaster.Login, channelLogin, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _broadcaster = new TwitchUser
        {
            Id = broadcasterId,
            Login = _broadcaster.Login,
            DisplayName = _broadcaster.DisplayName,
            ProfileImageUrl = _broadcaster.ProfileImageUrl
        };
        _logger.Info($"Read-only Twitch room resolved: channel={channelLogin}, broadcaster_id={broadcasterId}");
        _ = RefreshReadOnlyChannelAssetsAsync(channelLogin, broadcasterId);
    }

    private void OnEventSubStatusChanged(object? sender, string status)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusText = status;
            UpdateConnectionStateFromStatus(status);
        });
    }

    private void OnReadOnlyStatusChanged(object? sender, string status)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusText = status;
            UpdateConnectionStateFromStatus(status);
        });
    }

    private void OnChatLogWriteFailed(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(() => StatusText = L("ChatLogWriteFailed"));
    }

    private async Task HandleIncomingMessageAsync(ChatMessageModel message)
    {
        try
        {
            PrepareMessageForDisplay(message);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Incoming message render failed: {ex.GetType().Name}");
        }

        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() => AddMessage(message), DispatcherPriority.DataBind);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Incoming message UI update failed: {ex.GetType().Name}");
            return;
        }

        try
        {
            await LoadMessageImagesAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Incoming message image load failed: {ex.GetType().Name}");
        }
    }

    private void AddMessage(ChatMessageModel message)
    {
        if (!string.IsNullOrWhiteSpace(message.Id))
        {
            if (!_seenMessageIds.Add(message.Id))
            {
                return;
            }

            _seenMessageOrder.Enqueue(message.Id);
            TrimSeenIds();
        }

        Messages.Add(message);
        _chatLogService.Enqueue(message);
        _overlayServer.PublishMessage(message);
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(ShowChatEmptyState));
        TrimMessagesToLimit();
    }

    private void TrimMessagesToLimit()
    {
        var limit = Math.Clamp(Settings.MessageLimit, 100, 5000);
        while (Messages.Count > limit)
        {
            Messages.RemoveAt(0);
        }
    }

    private void TrimSeenIds()
    {
        var max = Math.Max(1000, Settings.MessageLimit * 3);
        while (_seenMessageOrder.Count > max)
        {
            var id = _seenMessageOrder.Dequeue();
            _seenMessageIds.Remove(id);
        }
    }

    private void ClearMessages()
    {
        Messages.Clear();
        _seenMessageIds.Clear();
        _seenMessageOrder.Clear();
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(ShowChatEmptyState));
        StatusText = "Local chat list cleared";
    }

    private void ChangeFontSize(double delta)
    {
        FontSize += delta;
    }

    private bool FilterMessage(object item)
    {
        if (item is not ChatMessageModel message)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !message.Text.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !message.UserLabel.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(UserFilter))
        {
            var filter = UserFilter.Trim().TrimStart('@');
            if (!message.Login.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !message.UserLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void ScheduleFilterRefresh()
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private async Task RefreshStreamStatusAsync(CancellationToken cancellationToken = default)
    {
        var broadcasterId = _broadcaster?.Id;
        if (string.IsNullOrWhiteSpace(broadcasterId))
        {
            UpdateStreamStatus(new StreamStatusInfo(false, 0, string.Empty));
            return;
        }

        var status = await _streamStatusService.GetStatusAsync(broadcasterId, cancellationToken).ConfigureAwait(false);
        await _chatLogService.UpdateStreamInfoAsync(status, cancellationToken).ConfigureAwait(false);
        await Application.Current.Dispatcher.InvokeAsync(() => UpdateStreamStatus(status));
    }

    private void StartStreamStatusPolling()
    {
        StopStreamStatusPolling();
        if (_broadcaster is null)
        {
            return;
        }

        _streamStatusCts = new CancellationTokenSource();
        _ = Task.Run(() => PollStreamStatusAsync(_streamStatusCts.Token));
    }

    private async Task PollStreamStatusAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                await RefreshStreamStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Stream status polling failed: {ex.GetType().Name}");
            }
        }
    }

    private void StopStreamStatusPolling()
    {
        var cts = _streamStatusCts;
        _streamStatusCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    private void UpdateConnectionStateFromStatus(string status)
    {
        if (status.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("ошибка", StringComparison.OrdinalIgnoreCase))
        {
            UpdateChatState("error", status);
        }
        else if (status.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("lost", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("отключ", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("потер", StringComparison.OrdinalIgnoreCase))
        {
            UpdateChatState("disconnected", status);
        }
        else if (status.Contains("connected", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("чат подключ", StringComparison.OrdinalIgnoreCase))
        {
            UpdateChatState("connected", L("ChatReady"));
        }
        else if (status.Contains("connecting", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("reconnecting", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("подключ", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("переподключ", StringComparison.OrdinalIgnoreCase))
        {
            UpdateChatState("connecting", status);
        }
    }

    private void UpdateAccountState(string state)
    {
        IsConnecting = string.Equals(state, "signing in", StringComparison.OrdinalIgnoreCase);
        ConnectionStateText = state switch
        {
            "signed in" => L("ApiConnected"),
            "signing in" => L("ApiConnecting"),
            "read only" => L("WatchOnlyMode"),
            _ => L("ApiDisconnected")
        };
        ConnectionIndicatorBrush = state switch
        {
            "signed in" => CreateFrozenBrush("#FF5BE7A9"),
            "signing in" => CreateFrozenBrush("#FF73C7FF"),
            "read only" => CreateFrozenBrush("#FF73C7FF"),
            _ => CreateFrozenBrush("#FF6E7482")
        };
    }

    private void UpdateChatState(string state, string? detail = null)
    {
        IsChatConnected = string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase);
        ChatConnectionStateText = state switch
        {
            "connected" => L("ChatConnected"),
            "connecting" => L("ChatConnecting"),
            "error" => L("ChatError"),
            _ => L("ChatDisconnected")
        };
        ChatIndicatorBrush = state switch
        {
            "connected" => CreateFrozenBrush("#FF5BE7A9"),
            "connecting" => CreateFrozenBrush("#FF73C7FF"),
            "error" => CreateFrozenBrush("#FFFF6B7A"),
            _ => CreateFrozenBrush("#FF6E7482")
        };

        ChatEmptyTitle = state switch
        {
            "connected" => L("EmptyConnectedTitle"),
            "connecting" => detail ?? L("ChatConnecting"),
            "error" => detail ?? L("ChatError"),
            _ => L("EmptyDisconnectedTitle")
        };

        ChatEmptyText = state == "connected"
            ? $"{AppInfo.Name} · {L("VersionLine")}"
            : string.Empty;

        OnPropertyChanged(nameof(SendButtonToolTip));
        OnPropertyChanged(nameof(HeaderSubtitle));
    }
    private void UpdateStreamStatus(StreamStatusInfo status)
    {
        _isStreamLive = status.IsLive;
        StreamStatusText = status.IsLive ? L("StreamLive") : L("StreamOffline");
        StreamViewerText = status.IsLive && status.ViewerCount > 0
            ? $"{status.ViewerCount:N0} viewers"
            : string.Empty;
        StreamIndicatorBrush = status.IsLive
            ? CreateFrozenBrush("#FFFF4D5E")
            : CreateFrozenBrush("#FF6E7482");
    }

    private bool ValidateOAuthAndOverlayPorts(AppSettings settings)
    {
        settings.Normalize();
        if (!Uri.TryCreate(settings.RedirectUri, UriKind.Absolute, out var redirectUri))
        {
            _dialogs.ShowError(L("OAuthRedirectUri"), "Invalid OAuth Redirect URI.");
            return false;
        }

        if (redirectUri.Port == settings.OverlayPort)
        {
            _dialogs.ShowError(L("OAuthRedirectUri"), L("OAuthOverlayPortConflict"));
            StatusText = L("OAuthOverlayPortConflict");
            return false;
        }

        return true;
    }

    private string L(string key) => LocalizationService.Get(Settings.Language, key);

    private void RefreshLocalizedText()
    {
        UpdateAccountState(IsConnected
            ? (ConnectionMode == ChatConnectionMode.ReadOnly ? "read only" : "signed in")
            : "not signed in");
        UpdateChatState(IsChatConnected ? "connected" : "disconnected");
        StreamStatusText = _isStreamLive ? L("StreamLive") : L("StreamOffline");
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(SendButtonToolTip));
        OnPropertyChanged(nameof(ReadOnlyComposerText));
        OnPropertyChanged(nameof(DisconnectButtonToolTip));
    }

    private static string CreateAvatarInitial(string displayName, string login)
    {
        var value = string.IsNullOrWhiteSpace(displayName) ? login : displayName;
        return string.IsNullOrWhiteSpace(value)
            ? "?"
            : value.Trim()[0].ToString().ToUpperInvariant();
    }

    private void ApplyTheme()
    {
        var dark = !string.Equals(Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        SetBrush("AppBackground", dark ? "#FF07090F" : "#FFE9EDF4");
        SetBrush("PanelBackground", dark ? "#D0101420" : "#F4F7FBFF");
        SetBrush("PanelAltBackground", dark ? "#B8131824" : "#E8EEF6FF");
        SetBrush("GlassRowBrush", dark ? "#331D2433" : "#DDE8EFF8");
        SetBrush("GlassRowHoverBrush", dark ? "#5A2A3347" : "#F6FAFDFF");
        SetBrush("BorderBrushSoft", dark ? "#2BFFFFFF" : "#6697A1B3");
        SetBrush("BorderBrushBright", dark ? "#55FFFFFF" : "#AA6D7890");
        SetBrush("PrimaryText", dark ? "#F7F8FF" : "#151A25");
        SetBrush("SecondaryText", dark ? "#AEB4C4" : "#4F5A6C");
        SetBrush("MutedText", dark ? "#7F879A" : "#768294");
        SetBrush("ButtonBackgroundBrush", dark ? "#2AFFFFFF" : "#FFE8EEF7");
        SetBrush("ButtonHoverBrush", dark ? "#42FFFFFF" : "#FFDCE6F5");
        SetBrush("ButtonPressedBrush", dark ? "#24FFFFFF" : "#FFC9D7EA");
        SetBrush("ButtonDisabledBrush", dark ? "#12FFFFFF" : "#FFE3E8F0");
        SetBrush("TextBoxBackgroundBrush", dark ? "#22FFFFFF" : "#FFF8FAFD");
        SetBrush("TextBoxFocusedBrush", dark ? "#33FFFFFF" : "#FFF1F5FA");
        SetBrush("DangerBrush", dark ? "#FF6B7A" : "#C92E44");
        SetBrush("SuccessBrush", dark ? "#6FE7B4" : "#16865D");
        SetBrush("AccentBrush", dark ? "#9F8CFF" : "#2F5AE8");
        SetBrush("AccentBlueBrush", dark ? "#69B7FF" : "#2454D6");
        SetBrush("PrimaryButtonTextBrush", dark ? "#0B0D12" : "#FFFFFFFF");
        SetBrush("PopupBackgroundBrush", dark ? "#F00C101A" : "#FFFFFFFF");
        SetBrush("PopupBorderBrush", dark ? "#FF2A2A3A" : "#FFB7C1D2");
        SetBrush("ComboItemHoverBrush", dark ? "#332F245E" : "#FFE8EFFB");
        SetBrush("ComboItemSelectedBrush", dark ? "#4A7A5CFF" : "#FF2F5AE8");
        SetBrush("TabStripBackgroundBrush", dark ? "#16FFFFFF" : "#DDE7EEF8");
        SetBrush("TabSelectedTextBrush", dark ? "#0B0D12" : "#FFFFFFFF");
        SetBrush("NumericBackgroundBrush", dark ? "#1CFFFFFF" : "#FFFFFFFF");
        SetBrush("NumericBorderBrush", dark ? "#332A2A3A" : "#FFB7C1D2");
        SetAccentBrush(dark);
    }

    private static Brush CreateFrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static void SetBrush(string key, string color)
    {
        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static void SetAccentBrush(bool dark)
    {
        if (!dark)
        {
            Application.Current.Resources["AccentGradientBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF2F5AE8"));
            return;
        }

        Application.Current.Resources["AccentGradientBrush"] = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop((Color)ColorConverter.ConvertFromString("#FFA88CFF"), 0),
                new GradientStop((Color)ColorConverter.ConvertFromString("#FF6CB6FF"), 0.58),
                new GradientStop((Color)ColorConverter.ConvertFromString("#FF7AF0D2"), 1)
            }
        };
    }

    private static void CopySettings(AppSettings source, AppSettings target)
    {
        source.Normalize();
        target.UseCustomClientId = source.UseCustomClientId;
        target.ClientId = source.ClientId;
        target.RedirectUri = source.RedirectUri;
        target.UseCustomChannel = source.UseCustomChannel;
        target.ChannelLogin = source.ChannelLogin;
        target.BroadcasterId = source.BroadcasterId;
        target.ConnectionMode = source.ConnectionMode;
        target.LastReadOnlyChannel = source.LastReadOnlyChannel;
        target.FontSize = source.FontSize;
        target.MessageLimit = source.MessageLimit;
        target.ShowTimestamps = source.ShowTimestamps;
        target.EnableTwitchEmotes = source.EnableTwitchEmotes;
        target.EnableBttvEmotes = source.EnableBttvEmotes;
        target.EnableSevenTvEmotes = source.EnableSevenTvEmotes;
        target.EnableBadges = source.EnableBadges;
        target.Theme = source.Theme;
        target.Language = source.Language;
        target.ToastNotifications = source.ToastNotifications;
        target.ReduceMotion = source.ReduceMotion;
        target.EnableObsOverlay = source.EnableObsOverlay;
        target.OverlayPort = source.OverlayPort;
        target.OverlayMaxMessages = source.OverlayMaxMessages;
        target.OverlayFontSize = source.OverlayFontSize;
        target.OverlayShowTimestamps = source.OverlayShowTimestamps;
        target.OverlayShowBadges = source.OverlayShowBadges;
        target.OverlayShowEmotes = source.OverlayShowEmotes;
        target.OverlayFadeOutSeconds = source.OverlayFadeOutSeconds;
        target.OverlayTextShadow = source.OverlayTextShadow;
        target.OverlayBackgroundOpacity = source.OverlayBackgroundOpacity;
        target.OverlayAlign = source.OverlayAlign;
        target.EnableChatLogging = source.EnableChatLogging;
        target.ChatLogsFolder = source.ChatLogsFolder;
        target.SaveChatLogJsonl = source.SaveChatLogJsonl;
        target.SaveChatLogTxt = source.SaveChatLogTxt;
        target.LogChatBadges = source.LogChatBadges;
        target.LogDeletedMessages = source.LogDeletedMessages;
        target.MaxLogViewerMessages = source.MaxLogViewerMessages;
        target.AutoSplitLogsByStream = source.AutoSplitLogsByStream;
    }

    private static bool HasChatLogSettingsChanged(AppSettings previous, AppSettings current)
    {
        return previous.EnableChatLogging != current.EnableChatLogging ||
               !string.Equals(previous.ChatLogsFolder, current.ChatLogsFolder, StringComparison.OrdinalIgnoreCase) ||
               previous.SaveChatLogJsonl != current.SaveChatLogJsonl ||
               previous.SaveChatLogTxt != current.SaveChatLogTxt ||
               previous.LogChatBadges != current.LogChatBadges ||
               previous.LogDeletedMessages != current.LogDeletedMessages ||
               previous.MaxLogViewerMessages != current.MaxLogViewerMessages ||
               previous.AutoSplitLogsByStream != current.AutoSplitLogsByStream;
    }

    private void RaiseCommandState()
    {
        (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SignInCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReconnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenChatLogsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (LogoutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SendMessageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}


