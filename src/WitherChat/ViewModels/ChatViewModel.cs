using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WitherChat;
using WitherChat.Controls;
using WitherChat.Models;
using WitherChat.Services;
using WitherChat.Views;

namespace WitherChat.ViewModels;

public sealed class ChatViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxObservedBadgeLoadsWhileScrolling = 16;
    private const int MaxChannels = 3;
    private const int UserProfileCacheSoftLimit = 500;
    private const int UserProfileCacheHardLimit = 750;
    private const int RedemptionDedupLimit = 1250;
    private const int LiveMessageCorrelationLimit = 1500;
    private const int PendingMetadataLimit = 1250;
    private const int UserColorCacheLimit = 2000;
    private const int MaxPendingChatMessages = 20000;
    private const int MaxBannedUsersPerChannel = 1000;
    private const int MaxUnbanRequestsPerChannel = 1000;
    private const int SourceIdentityCacheLimit = 256;
    private const double IncomingMessageDrainBudgetMs = 6;
    private static readonly TimeSpan UserProfileCacheTtl = TimeSpan.FromMinutes(75);
    private readonly SettingsService _settingsService = new();
    private readonly SecureTokenStore _tokenStore = new();
    private readonly FileLogger _logger = new();
    private readonly DialogService _dialogs = new();
    private readonly AuthService _authService;
    private readonly TwitchApiClient _apiClient;
    private readonly TwitchEventSubClient _eventSubClient;
    private readonly ReadOnlyChatClient _readOnlyChatClient;
    private readonly ModerationService _moderationService;
    private readonly ModerationCacheService _moderationCacheService;
    private readonly EmoteCache _emoteCache;
    private readonly TwitchBadgeService _badgeService;
    private readonly ThirdPartyEmoteService _thirdPartyEmoteService;
    private readonly StreamStatusService _streamStatusService;
    private readonly OverlayServerService _overlayServer;
    private readonly ChatLogService _chatLogService;
    private readonly DispatcherTimer _filterTimer;
    private readonly DispatcherTimer _messageBatchTimer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _punishmentRemovalInProgress = new(StringComparer.Ordinal);
    private readonly HashSet<string> _moderationOperationsInProgress = new(StringComparer.Ordinal);
    private readonly HashSet<string> _messageDeleteOperationsInProgress = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenModerationEventKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenModerationEventOrder = new();
    private readonly HashSet<string> _pendingDeletedMessageKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _pendingDeletedMessageOrder = new();
    private readonly Queue<string> _seenMessageOrder = new();
    private readonly HashSet<string> _seenRedemptionIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenRedemptionOrder = new();
    private readonly HashSet<string> _reconnectingChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ChatMessageModel> _emptyMessages = [];
    private readonly ConcurrentQueue<PendingChatMessage> _pendingChatMessages = new();
    private readonly object _pendingChatMessagesGate = new();
    private readonly ConcurrentDictionary<Task, byte> _backgroundTasks = new();
    private readonly object _backgroundTaskGate = new();
    private readonly ConcurrentDictionary<ChatMessageModel, byte> _messageImageLoads =
        new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<string, byte> _badgeImageLoads =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ChatMessageModel, byte> _visibleMessages =
        new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _messageHydrationGate = new(4, 4);
    private readonly object _userProfileCacheGate = new();
    private readonly Dictionary<string, CachedUserProfile> _userProfileCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<TwitchUser?>> _userProfileRequests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sourceBadgeRefreshes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TwitchUser> _sourceChannelIdentities = new(StringComparer.Ordinal);
    private readonly Queue<string> _sourceChannelIdentityOrder = new();
    private readonly ConcurrentDictionary<string, byte> _sourceChannelIdentityRequests = new(StringComparer.Ordinal);
#if DEBUG
    private readonly HashSet<string> _sevenTvVisualDiagnostics = new(StringComparer.Ordinal);
#endif
    private readonly Dictionary<string, ChatMessageModel> _liveMessageIndex = new(StringComparer.Ordinal);
    private readonly Queue<string> _liveMessageIndexOrder = new();
    private readonly Dictionary<string, PendingChannelPointsMetadata> _pendingChannelPointsMetadata = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset ExpiresAt)> _pendingChannelPointsOrder = new();
    private CancellationTokenSource? _streamStatusCts;
    private CancellationTokenSource? _pinnedMessageCts;
    private Task? _streamStatusTask;
    private Task? _pinnedMessageTask;
    private CancellationTokenSource? _channelAssetsCts;
    private CancellationTokenSource? _oauthSignInCts;
    private readonly Dictionary<string, CancellationTokenSource> _channelAssetRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposeCts = new();

    private AppSettings _settings;
    private TwitchUser? _currentUser;
    private TwitchUser? _broadcaster;
    private ChatConnectionMode _connectionMode = ChatConnectionMode.SignedIn;
    private string _statusText = string.Empty;
    private string _searchText = string.Empty;
    private string _userFilter = string.Empty;
    private string _outgoingMessage = string.Empty;
    private string _profileImageUrl = string.Empty;
    private string _displayNameCompact = string.Empty;
    private string _avatarInitial = "?";
    private string _connectionStateText = string.Empty;
    private string _chatConnectionStateText = string.Empty;
    private string _chatEmptyTitle = string.Empty;
    private string _chatEmptyText = string.Empty;
    private string _streamStatusText = string.Empty;
    private bool _isStreamLive;
    private bool _hasAuthoritativeStreamStatus;
    private int _streamViewerCount;
    private string _streamViewerText = string.Empty;
    private Brush _connectionIndicatorBrush = CreateFrozenBrush("#FF6E7482");
    private Brush _chatIndicatorBrush = CreateFrozenBrush("#FF6E7482");
    private Brush _streamIndicatorBrush = CreateFrozenBrush("#FF6E7482");
    private bool _isBusy;
    private bool _isAddingChannel;
    private bool _autoScroll = true;
    private bool _isConnected;
    private bool _isChatConnected;
    private bool _isConnecting;
    private bool _filtersVisible;
    private bool _isAccountSignInInProgress;
    private ChannelSessionViewModel? _activeChannel;
    private ICollectionView _filteredMessages = null!;
    private bool _isChannelSwitcherOpen;
    private int _messageDrainScheduled;
    private int _pendingChatMessageCount;
    private int _shutdownStarted;
    private int _messagePresentationVersion = 1;
    private bool _isUserScrolling;
    private long _lastQueueDropWarning;
#if DEBUG
    private int _badgeMismatchDiagnostics;
    private long _processedLiveMessageCount;
    private long _filterRefreshCount;
#endif

    public ChatViewModel()
    {
        _settings = _settingsService.Load();
        _connectionMode = Settings.ConnectionMode;
        LocalizationService.ApplyToResources(Settings.Language);
        AnimationService.SetReduceMotion(Settings.ReduceMotion);
        _authService = new AuthService(() => AppTwitchDefaults.GetClientId(Settings), _tokenStore, _logger);
        _apiClient = new TwitchApiClient(() => AppTwitchDefaults.GetClientId(Settings), _authService, _logger);
        _eventSubClient = new TwitchEventSubClient(_apiClient, _logger);
        _readOnlyChatClient = new ReadOnlyChatClient(_logger, GetIrcTokenAsync);
        _moderationService = new ModerationService(_apiClient);
        _moderationCacheService = new ModerationCacheService(_logger);
        _emoteCache = new EmoteCache(_logger);
        _badgeService = new TwitchBadgeService(_apiClient, _logger);
        _thirdPartyEmoteService = new ThirdPartyEmoteService(_logger);
        _streamStatusService = new StreamStatusService(_apiClient, _logger);
        _overlayServer = new OverlayServerService(_logger);
        _chatLogService = new ChatLogService(_logger);

        Channels = [];
        Channels.CollectionChanged += (_, _) => RefreshChannelCapacity();
        _filteredMessages = CollectionViewSource.GetDefaultView(_emptyMessages);
        FilteredMessages.Filter = HasActiveMessageFilter ? FilterMessage : null;

        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            ApplyMessageFilter();
#if DEBUG
            _filterRefreshCount++;
            Debug.WriteLine($"WitherChat filter refreshes: {_filterRefreshCount}");
#endif
        };
        _messageBatchTimer.Tick += (_, _) =>
        {
            _messageBatchTimer.Stop();
            DrainPendingMessages();
        };

        _eventSubClient.MessageReceived += OnEventSubMessageReceived;
        _eventSubClient.SharedChatSessionChanged += OnSharedChatSessionChanged;
        _eventSubClient.StatusChanged += OnEventSubStatusChanged;
        _eventSubClient.ChannelPointsAuthorizationRequired += OnChannelPointsAuthorizationRequired;
        _eventSubClient.ChannelPointsCapabilityChanged += OnChannelPointsCapabilityChanged;
        _eventSubClient.ChatMessageDeleted += OnChatMessageDeleted;
        _eventSubClient.UserMessagesCleared += OnUserMessagesCleared;
        _eventSubClient.UserBanned += OnEventSubUserBanned;
        _eventSubClient.UserUnbanned += OnEventSubUserUnbanned;
        _eventSubClient.UnbanRequestCreated += OnUnbanRequestCreated;
        _eventSubClient.UnbanRequestResolved += OnUnbanRequestResolved;
        _eventSubClient.AutoModMessageHeld += OnAutoModMessageHeld;
        _eventSubClient.AutoModMessageUpdated += OnAutoModMessageUpdated;
        _readOnlyChatClient.MessageReceived += OnReadOnlyChannelMessageReceived;
        _readOnlyChatClient.ChannelStatusChanged += OnReadOnlyChannelStatusChanged;
        _readOnlyChatClient.ChannelIdentityResolved += OnReadOnlyChannelIdentityResolved;
        _readOnlyChatClient.UserModerated += OnIrcUserModerated;
        _readOnlyChatClient.MessageDeleted += OnIrcMessageDeleted;
        _readOnlyChatClient.ChatCleared += OnIrcChatCleared;
        _chatLogService.WriteFailed += OnChatLogWriteFailed;
        _authService.SessionInvalidated += OnAuthSessionInvalidated;

        ConnectCommand = new AsyncRelayCommand(ConnectTwitchAsync, () => CanRunInteractiveCommand && !IsAccountSignInInProgress);
        SignInCommand = new AsyncRelayCommand(SignInWithTwitchAsync, () => CanRunInteractiveCommand && !IsAccountSignInInProgress);
        CancelSignInCommand = new RelayCommand(_ => CancelTwitchSignIn(), _ => !IsShuttingDown && IsAccountSignInInProgress);
        ReconnectCommand = new AsyncRelayCommand(ReconnectChatAsync, () => CanRunInteractiveCommand && HasActiveChannel);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, () => CanRunInteractiveCommand && !IsAccountSignInInProgress);
        OpenChatLogsCommand = new AsyncRelayCommand(OpenChatLogsAsync, () => CanRunInteractiveCommand);
        OpenModerationCommand = new AsyncRelayCommand(OpenModerationAsync, () => CanRunInteractiveCommand && CanModerate);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => CanRunInteractiveCommand && IsAccountAuthenticated);
        SendMessageCommand = new AsyncRelayCommand(SendMessageAsync, () => CanRunInteractiveCommand && CanSendMessages && !string.IsNullOrWhiteSpace(OutgoingMessage));
        ClearMessagesCommand = new RelayCommand(_ => ClearMessages(), _ => !IsShuttingDown);
        IncreaseFontCommand = new RelayCommand(_ => ChangeFontSize(1), _ => !IsShuttingDown);
        DecreaseFontCommand = new RelayCommand(_ => ChangeFontSize(-1), _ => !IsShuttingDown);
        ToggleFiltersCommand = new RelayCommand(_ => FiltersVisible = !FiltersVisible, _ => !IsShuttingDown);
        AddChannelCommand = new AsyncRelayCommand(AddChannelAsync, () => CanRunInteractiveCommand && CanAddChannel);
        RemoveChannelCommand = new AsyncRelayCommand(
            session => session is ChannelSessionViewModel channel ? RemoveChannelAsync(channel) : Task.CompletedTask,
            session => CanRunInteractiveCommand && session is ChannelSessionViewModel { IsPrimaryAccountChannel: false });
        SwitchChannelCommand = new RelayCommand(session =>
        {
            if (session is ChannelSessionViewModel channel)
            {
                ActiveChannel = channel;
                PersistChannels();
            }
        }, _ => CanRunInteractiveCommand);
        ToggleChannelSwitcherCommand = new RelayCommand(_ => IsChannelSwitcherOpen = !IsChannelSwitcherOpen, _ => CanRunInteractiveCommand);
        StatusText = L("Ready");
    }

    public ObservableCollection<ChannelSessionViewModel> Channels { get; }
    public ObservableCollection<ChatMessageModel> Messages => ActiveChannel?.Messages ?? _emptyMessages;
    public ICollectionView FilteredMessages => _filteredMessages;
#if DEBUG
    internal long FilterRefreshCount => _filterRefreshCount;
#endif
    public event EventHandler? ActiveChannelChanging;
    public event EventHandler? ActiveMessagesChanged;

    public ICommand ConnectCommand { get; }
    public ICommand SignInCommand { get; }
    public ICommand CancelSignInCommand { get; }
    public ICommand ReconnectCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenChatLogsCommand { get; }
    public ICommand OpenModerationCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand SendMessageCommand { get; }
    public ICommand ClearMessagesCommand { get; }
    public ICommand IncreaseFontCommand { get; }
    public ICommand DecreaseFontCommand { get; }
    public ICommand ToggleFiltersCommand { get; }
    public ICommand AddChannelCommand { get; }
    public ICommand RemoveChannelCommand { get; }
    public ICommand SwitchChannelCommand { get; }
    public ICommand ToggleChannelSwitcherCommand { get; }

    public ChannelSessionViewModel? ActiveChannel
    {
        get => _activeChannel;
        set
        {
            if (ReferenceEquals(_activeChannel, value))
            {
                return;
            }

            var previousChannel = _activeChannel;
            ActiveChannelChanging?.Invoke(this, EventArgs.Empty);

            if (_activeChannel is not null)
            {
                _activeChannel.IsActive = false;
                _activeChannel.AutoScroll = AutoScroll;
            }

            _activeChannel = value;
            if (_activeChannel is not null)
            {
                _activeChannel.IsActive = true;
                _activeChannel.UnreadCount = 0;
                _autoScroll = _activeChannel.AutoScroll;
                Settings.LastActiveChannelLogin = _activeChannel.ChannelLogin;
                _thirdPartyEmoteService.SetActiveChannel(ChannelAssetKey(_activeChannel));
                UpdateChatState(
                    _activeChannel.IsConnected ? "connected" :
                    _activeChannel.IsConnecting ? "connecting" :
                    string.IsNullOrWhiteSpace(_activeChannel.ConnectionError) ? "disconnected" : "error",
                    _activeChannel.ConnectionError);
                UpdateStreamStatus(new StreamStatusInfo(
                    _activeChannel.IsLive,
                    _activeChannel.ViewerCount,
                    _activeChannel.StreamTitle,
                    _activeChannel.GameName,
                    _activeChannel.StreamStartedAt,
                    _activeChannel.HasAuthoritativeStreamStatus));
                if (!IsAccountAuthenticated)
                {
                    _broadcaster = SessionToUser(_activeChannel);
                }
            }
            else
            {
                Settings.LastActiveChannelLogin = string.Empty;
                if (!IsAccountAuthenticated || Channels.Count == 0)
                {
                    _broadcaster = null;
                }
                UpdateChatState("disconnected");
                UpdateStreamStatus(new StreamStatusInfo(false, 0, string.Empty));
            }

            _filteredMessages = CollectionViewSource.GetDefaultView(Messages);
            _filteredMessages.Filter = HasActiveMessageFilter ? FilterMessage : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Messages));
            OnPropertyChanged(nameof(FilteredMessages));
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(ShowChatEmptyState));
            OnPropertyChanged(nameof(AutoScroll));
            OnPropertyChanged(nameof(CanSendMessages));
            OnPropertyChanged(nameof(CanModerate));
            OnPropertyChanged(nameof(ActiveChannelLabel));
            RaiseStatePropertiesChanged();
            ActiveMessagesChanged?.Invoke(this, EventArgs.Empty);
            if (previousChannel?.AutoScroll == true)
            {
                TrimMessagesToLimit(previousChannel, force: true);
            }
            _overlayServer.ClearMessages();
            IsChannelSwitcherOpen = false;
            RaiseCommandState();
            StartPinnedMessagePolling();
        }
    }

    public string ActiveChannelLabel => ActiveChannel is null
        ? string.Empty
        : string.Format(CultureInfo.CurrentCulture, L("ChannelFormat"), "@" + ActiveChannel.ChannelLogin);
    public int EffectiveChannelCount => Channels
        .Select(channel => NormalizeChannelLogin(channel.ChannelLogin))
        .Where(IsValidChannelLogin)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
    public bool CanAddChannel => EffectiveChannelCount < MaxChannels && !_isAddingChannel;
    public bool HasChannels => Channels.Count > 0;
    public bool HasActiveChannel => ActiveChannel is not null;
    public bool IsActiveChatConnected => ActiveChannel?.IsConnected == true;
    public bool IsAccountAuthenticated => _currentUser is not null && _authService.HasAccessToken;
    public bool ShowSignInButton => !IsBusy &&
                                    !IsAccountSignInInProgress &&
                                    !IsAccountAuthenticated &&
                                    HasActiveChannel;
    public string SignInButtonText => IsAccountAuthenticated ? L("SignInAgain") : L("SignInToSend");
    public bool ShowConnectButton => !IsBusy && !HasActiveChannel && !IsAccountSignInInProgress;
    public bool ShowCancelSignInButton => IsAccountSignInInProgress;
    public bool ShowLogoutButton => IsAccountAuthenticated;
    public bool ShowReconnectButton => HasActiveChannel;

    public bool IsAccountSignInInProgress
    {
        get => _isAccountSignInInProgress;
        private set
        {
            if (SetProperty(ref _isAccountSignInInProgress, value))
            {
                OnPropertyChanged(nameof(ShowConnectButton));
                OnPropertyChanged(nameof(ShowSignInButton));
                OnPropertyChanged(nameof(ShowCancelSignInButton));
                RaiseCommandState();
            }
        }
    }

    public bool IsChannelSwitcherOpen
    {
        get => _isChannelSwitcherOpen;
        set => SetProperty(ref _isChannelSwitcherOpen, value);
    }

    public AppSettings Settings => _settings;

    public bool AlwaysOnTop => Settings.AlwaysOnTop;

    public bool UseTornBlackMessageTheme =>
        string.Equals(Settings.MessageVisualTheme, "TornBlack", StringComparison.OrdinalIgnoreCase);

    public Brush MessagePrimaryBrush => UseTornBlackMessageTheme
        ? Brushes.White
        : GetApplicationBrush("PrimaryText", Brushes.White);

    public Brush MessageSecondaryBrush => UseTornBlackMessageTheme
        ? Brushes.LightGray
        : GetApplicationBrush("SecondaryText", Brushes.LightGray);

    public Brush MessageMutedBrush => UseTornBlackMessageTheme
        ? Brushes.DarkGray
        : GetApplicationBrush("MutedText", Brushes.DarkGray);

    public HorizontalAlignment WindowControlsAlignment =>
        string.Equals(Settings.WindowControlsPosition, "Right", StringComparison.OrdinalIgnoreCase)
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;

    public FlowDirection WindowControlsFlowDirection =>
        string.Equals(Settings.WindowControlsPosition, "Right", StringComparison.OrdinalIgnoreCase)
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

    public bool UseWindowsWindowControls =>
        string.Equals(Settings.WindowControlsStyle, "Windows", StringComparison.OrdinalIgnoreCase);

    public bool UseMacWindowControls => !UseWindowsWindowControls;

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
                RaiseStatePropertiesChanged();
                RaiseCommandState();
            }
        }
    }

    public bool IsSignedIn => IsAccountAuthenticated;

    public bool IsReadOnlyMode => !IsAccountAuthenticated && HasActiveChannel;

    public bool CanSendMessages => IsAccountAuthenticated &&
                                   ActiveChannel is { IsConnected: true, CanSend: true } channel &&
                                   !channel.HasSendRestriction &&
                                   !string.IsNullOrWhiteSpace(channel.BroadcasterId);

    public bool HasSendRestriction => ActiveChannel?.HasSendRestriction == true;

    public string SendRestrictionText => ActiveChannel?.SendRestrictionText ?? string.Empty;

    public bool CanModerate => IsAccountAuthenticated && ActiveChannel is { HasConfirmedModerationAccess: true };

    public bool CanUseCreatorControls => CanModerate;
    public bool HasAutoModScope => _authService.HasScope(AuthService.AutoModScope);

    public bool ShowMessageComposer => IsAccountAuthenticated && HasActiveChannel;

    public bool ShowReadOnlyComposerNotice => !IsAccountAuthenticated && HasActiveChannel;

    public string ReadOnlyComposerText => L("WatchOnlySendingUnavailable");

    public string DisconnectButtonToolTip => IsAccountAuthenticated ? L("LogoutTwitch") : L("DisconnectFromChannel");

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string HeaderTitle => IsAccountAuthenticated
        ? (string.IsNullOrWhiteSpace(DisplayNameCompact) ? _currentUser?.Login ?? AppInfo.Name : DisplayNameCompact)
        : L("Guest");

    public string HeaderSubtitle => IsAccountAuthenticated
        ? "@" + (_currentUser?.Login ?? string.Empty)
        : L("TwitchNotConnected");

    public string AccountDisplayName => IsAccountAuthenticated
        ? (string.IsNullOrWhiteSpace(_currentUser?.DisplayName) ? _currentUser?.Login ?? string.Empty : _currentUser.DisplayName)
        : L("Guest");

    public string AccountLogin => IsAccountAuthenticated ? _currentUser?.Login ?? string.Empty : string.Empty;

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

    public bool HasProfileImage => IsAccountAuthenticated && !string.IsNullOrWhiteSpace(ProfileImageUrl);

    public bool ShowAvatarPlaceholder => !HasProfileImage;

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
            if (!HasActiveChannel)
            {
                return L("SignInFirst");
            }

            if (!IsAccountAuthenticated)
            {
                return L("WatchOnlySendingUnavailable");
            }

            if (!IsActiveChatConnected)
            {
                return L("ChatNotConnected");
            }

            if (HasSendRestriction)
            {
                return SendRestrictionText;
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

    public bool IsSharedChatActive => ActiveChannel?.IsSharedChatActive == true;
    public string SharedChatStatusText => ActiveChannel is { SharedChatParticipantCount: > 0 } session
        ? string.Format(CultureInfo.CurrentCulture, L("SharedChatParticipants"), session.SharedChatParticipantCount)
        : L("SharedChat");

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
                RaiseStatePropertiesChanged();
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
                RaiseStatePropertiesChanged();
                RaiseCommandState();
            }
        }
    }

    public bool HasMessages => Messages.Count > 0;

    public bool ShowChatEmptyState => HasActiveChannel && !HasMessages;

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
                if (value)
                {
                    RefreshBadgeSettings();
                }
                else
                {
                    ReleaseAllBadgeImages();
                }
            }
        }
    }

    public bool AutoScroll
    {
        get => ActiveChannel?.AutoScroll ?? _autoScroll;
        set
        {
            SetProperty(ref _autoScroll, value);
            if (ActiveChannel is not null)
            {
                ActiveChannel.AutoScroll = value;
                if (value)
                {
                    FlushPendingVisualMessages(ActiveChannel);
                    ActiveChannel.NewMessagesBelowCount = 0;
                    TrimMessagesToLimit(ActiveChannel, force: true);
                }
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(ShowConnectButton));
                OnPropertyChanged(nameof(ShowSignInButton));
                RaiseCommandState();
            }
        }
    }

    public async Task InitializeAsync()
    {
        ApplyTheme();
        await ConfigureOverlayAsync(showErrors: false).ConfigureAwait(true);
        SetGuestAccountState();
        UpdateAccountState("not signed in");
        UpdateChatState("disconnected");
        UpdateStreamStatus(new StreamStatusInfo(false, 0, string.Empty));
        if (!AppTwitchDefaults.IsClientIdConfigured(Settings))
        {
            StatusText = L("ReleaseClientIdMissing");
            return;
        }

        IsBusy = true;
        try
        {
            var token = await _authService.TryRestoreSessionAsync(_disposeCts.Token).ConfigureAwait(true);
            if (token is null)
            {
                var fallbackChannel = Settings.SavedChannelLogins.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(fallbackChannel))
                {
                    await ConnectReadOnlyChannelAsync(fallbackChannel, saveSettings: false).ConfigureAwait(true);
                    return;
                }

                StatusText = L("SignInFirst");
                return;
            }

            await LoadIdentityAndConnectAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("Initialization failed", ex);
            StatusText = UserFacingError(ex, "UnexpectedError");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ConnectTwitchAsync()
    {
        if (Application.Current.MainWindow is not MainWindow mainWindow)
        {
            return;
        }

        var result = await mainWindow.OpenConnectTwitchPanelAsync(
            Settings.Language,
            ActiveChannel?.ChannelLogin ?? string.Empty,
            _apiClient).ConfigureAwait(true);

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

    private async Task AddChannelAsync()
    {
        if (!CanAddChannel)
        {
            if (EffectiveChannelCount >= MaxChannels)
            {
                StatusText = L("ChannelLimitHint");
            }

            return;
        }

        _isAddingChannel = true;
        RefreshChannelCapacity();
        try
        {
            if (Application.Current.MainWindow is not MainWindow mainWindow)
            {
                return;
            }

            var result = await mainWindow.OpenConnectTwitchPanelAsync(
                Settings.Language,
                ActiveChannel?.ChannelLogin ?? string.Empty,
                _apiClient).ConfigureAwait(true);
            if (result?.Accepted != true || string.IsNullOrWhiteSpace(result.ChannelLogin))
            {
                return;
            }

            await AddReadOnlyChannelSessionAsync(result.ChannelLogin, activate: true).ConfigureAwait(true);
        }
        finally
        {
            _isAddingChannel = false;
            RefreshChannelCapacity();
        }
    }

    private bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) != 0;
    private bool CanRunInteractiveCommand => !IsShuttingDown && !IsBusy;

    private async Task<ChannelSessionViewModel?> AddReadOnlyChannelSessionAsync(
        string channelLogin,
        bool activate)
    {
        var login = NormalizeChannelLogin(channelLogin);
        if (!IsValidChannelLogin(login))
        {
            StatusText = L("TwitchChannelNameRequired");
            return null;
        }

        var existing = FindChannel(login);
        if (existing is not null)
        {
            if (activate)
            {
                ActiveChannel = existing;
            }

            return existing;
        }

        if (EffectiveChannelCount >= MaxChannels)
        {
            StatusText = L("ChannelLimitHint");
            return null;
        }

        TwitchUser? user = null;
        var streamStatus = new StreamStatusInfo(false, 0, string.Empty, IsAuthoritative: false);
        if (_authService.HasAccessToken)
        {
            try
            {
                user = await _apiClient.GetUserByLoginAsync(login, _disposeCts.Token).ConfigureAwait(true);
                if (user is not null)
                {
                    streamStatus = await _streamStatusService.GetStatusAsync(user.Id, _disposeCts.Token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Channel profile lookup skipped: {ex.GetType().Name}");
            }
        }

        var session = new ChannelSessionViewModel(login)
        {
            BroadcasterId = user?.Id ?? string.Empty,
            DisplayName = string.IsNullOrWhiteSpace(user?.DisplayName) ? login : user.DisplayName,
            ProfileImageUrl = user?.ProfileImageUrl ?? string.Empty,
            IsLive = streamStatus.IsLive,
            ViewerCount = streamStatus.ViewerCount,
            StreamTitle = streamStatus.Title,
            GameName = streamStatus.GameName,
            StreamStartedAt = streamStatus.StartedAt,
            HasAuthoritativeStreamStatus = streamStatus.IsAuthoritative,
            ConnectionStatus = L("ChannelConnecting"),
            CanSend = IsSignedIn && !string.IsNullOrWhiteSpace(user?.Id),
            CanModerate = false
        };
        if (_currentUser is null)
        {
            ConnectionMode = ChatConnectionMode.ReadOnly;
            Settings.ConnectionMode = ChatConnectionMode.ReadOnly;
            IsConnected = true;
            SetGuestAccountState();
        }

        Channels.Add(session);
        SetChannelConnectionState(session, "connecting");
        if (activate || ActiveChannel is null)
        {
            ActiveChannel = session;
        }
        PersistChannels();

        if (!_authService.HasAccessToken || !_authService.HasScope("chat:read"))
        {
            var error = L("ChatReadRequiresSignIn");
            SetChannelConnectionState(session, "error", error);
            StatusText = error;
            RaiseStatePropertiesChanged();
            return session;
        }

        try
        {
            _logger.Info($"IRC channel join requested: {login}");
            await _readOnlyChatClient.JoinChannelAsync(login, _disposeCts.Token).ConfigureAwait(true);
            var joined = await _readOnlyChatClient
                .WaitForChannelJoinAsync(login, TimeSpan.FromSeconds(20), _disposeCts.Token)
                .ConfigureAwait(true);
            SetChannelConnectionState(
                session,
                joined ? "connected" : "error",
                joined ? null : L("WatchOnlyConnectFailed"));
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"IRC channel join failed: {login}, {ex.GetType().Name}");
            SetChannelConnectionState(session, "error", L("WatchOnlyConnectFailed"));
        }
        finally
        {
            if (session.IsConnecting && !_disposeCts.IsCancellationRequested)
            {
                SetChannelConnectionState(session, "error", L("WatchOnlyConnectFailed"));
            }
        }

        if (session.IsConnected && !string.IsNullOrWhiteSpace(session.BroadcasterId))
        {
            await RefreshModerationAccessAsync(session).ConfigureAwait(true);
        }

        (AddChannelCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddChannel));
        RaiseStatePropertiesChanged();
        return session;
    }

    public async Task SignInWithTwitchAsync()
    {
        if (IsAccountSignInInProgress)
        {
            return;
        }

        if (!AppTwitchDefaults.IsClientIdConfigured(Settings))
        {
            _dialogs.ShowError(
                "Twitch Client ID",
                L("ReleaseClientIdDetail"));
            await OpenSettingsAsync().ConfigureAwait(true);
            return;
        }

        if (!ValidateOAuthAndOverlayPorts(Settings))
        {
            return;
        }

        IsChannelSwitcherOpen = false;
        var signInCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _oauthSignInCts?.Dispose();
        _oauthSignInCts = signInCts;
        IsAccountSignInInProgress = true;
        IsBusy = true;
        _logger.Info("OAuth sign-in requested.");
        try
        {
            UpdateAccountState("signing in");
            if (ConnectionMode != ChatConnectionMode.ReadOnly)
            {
                UpdateChatState("disconnected");
            }

            StatusText = L("OpeningTwitchLogin");
            await _authService.SignInWithImplicitGrantAsync(
                Settings.RedirectUri,
                url =>
                {
                    if (!_dialogs.OpenUrl(url))
                    {
                        throw new InvalidOperationException(L("OpenLinkFailed"));
                    }
                },
                _ => Application.Current.Dispatcher.InvokeAsync(() => StatusText = L("OpeningTwitchLogin")),
                forceVerify: false,
                cancellationToken: signInCts.Token).ConfigureAwait(true);
            signInCts.Token.ThrowIfCancellationRequested();

            if (ReferenceEquals(_oauthSignInCts, signInCts))
            {
                _oauthSignInCts = null;
                IsAccountSignInInProgress = false;
            }

            StatusText = L("TwitchConnected");
            ConnectionMode = ChatConnectionMode.SignedIn;
            Settings.ConnectionMode = ChatConnectionMode.SignedIn;
            _settingsService.Save(Settings);
            await LoadIdentityAndConnectAsync().ConfigureAwait(true);
            _logger.Info("OAuth current user loaded.");
        }
        catch (OperationCanceledException) when (signInCts.IsCancellationRequested)
        {
            _logger.Info("OAuth canceled.");
            if (!IsAccountAuthenticated)
            {
                UpdateAccountState("not signed in");
            }

            StatusText = L("TwitchLoginCanceled");
        }
        catch (OAuthPortUnavailableException ex)
        {
            _logger.Error("Twitch OAuth port unavailable", ex);
            var message = L("OAuthPortBusy") + Environment.NewLine + ex.RedirectUri;
            _dialogs.ShowError(L("TwitchOAuthPortBusyTitle"), message);
            StatusText = message;
            if (!IsAccountAuthenticated)
            {
                UpdateAccountState("not signed in");
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error($"OAuth failed: {ex.GetType().Name}", ex);
            var message = UserFacingError(ex, "OAuthBrowserFailed");
            _dialogs.ShowError(L("ConnectTwitch"), message);
            StatusText = message;
            if (!IsAccountAuthenticated)
            {
                UpdateAccountState("not signed in");
            }
        }
        finally
        {
            if (ReferenceEquals(_oauthSignInCts, signInCts))
            {
                _oauthSignInCts = null;
            }
            signInCts.Dispose();
            IsAccountSignInInProgress = false;
            IsBusy = false;
        }
    }

    private void CancelTwitchSignIn()
    {
        var signInCts = _oauthSignInCts;
        if (signInCts is null || signInCts.IsCancellationRequested)
        {
            return;
        }

        StatusText = L("TwitchLoginCanceled");
        signInCts.Cancel();
    }

    private async Task ConnectReadOnlyChannelAsync(string channelLogin, bool saveSettings)
    {
        var login = NormalizeChannelLogin(channelLogin);
        if (!IsValidChannelLogin(login))
        {
            _dialogs.ShowError(L("ConnectTwitch"), L("TwitchChannelNameRequired"));
            return;
        }

        var keepAuthenticatedAccount = IsAccountAuthenticated;
        var ownsBusyState = !IsBusy;
        if (ownsBusyState)
        {
            IsBusy = true;
        }
        try
        {
            if (keepAuthenticatedAccount)
            {
                Settings.LastReadOnlyChannel = login;
                var session = await AddReadOnlyChannelSessionAsync(login, activate: true).ConfigureAwait(true);
                if (session is not null && saveSettings)
                {
                    PersistChannels();
                }

                return;
            }

            await _eventSubClient.StopAsync().ConfigureAwait(true);
            await _readOnlyChatClient.StopAsync().ConfigureAwait(true);
            StopStreamStatusPolling();
            CancelChannelAssetRefresh();
            _thirdPartyEmoteService.Clear();
            _broadcaster = null;
            ResetChannelSessions();

            Settings.LastReadOnlyChannel = login;
            Settings.ConnectionMode = ChatConnectionMode.ReadOnly;
            ConnectionMode = Settings.ConnectionMode;
            _currentUser = null;
            SetGuestAccountState();
            IsConnected = true;
            OutgoingMessage = string.Empty;
            FiltersVisible = false;
            UpdateStreamStatus(new StreamStatusInfo(false, 0, string.Empty, IsAuthoritative: false));
            UpdateChatState("connecting", L("ChatConnecting"));
            StatusText = L("ChannelConnecting");

            var channels = new[] { login }
                .Concat(Settings.SavedChannelLogins)
                .Select(NormalizeChannelLogin)
                .Where(IsValidChannelLogin)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();
            foreach (var savedLogin in channels)
            {
                await AddReadOnlyChannelSessionAsync(
                    savedLogin,
                    activate: string.Equals(savedLogin, Settings.LastActiveChannelLogin, StringComparison.OrdinalIgnoreCase))
                    .ConfigureAwait(true);
            }

            ActiveChannel ??= Channels.FirstOrDefault();
            if (saveSettings)
            {
                PersistChannels();
            }

            if (Channels.All(channel => !channel.IsPrimaryAccountChannel && !channel.IsConnected))
            {
                var error = !_authService.HasAccessToken || !_authService.HasScope("chat:read")
                    ? L("ChatReadRequiresSignIn")
                    : L("WatchOnlyConnectFailed");
                UpdateChatState("error", error);
                StatusText = error;
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("Read-only chat connect failed", ex);
            foreach (var channel in Channels.Where(channel => !channel.IsPrimaryAccountChannel))
            {
                SetChannelConnectionState(channel, "error", L("WatchOnlyConnectFailed"));
            }
            UpdateChatState("error", L("WatchOnlyConnectFailed"));
            _dialogs.ShowError(L("ConnectTwitch"), L("WatchOnlyConnectFailed"));
            StatusText = L("WatchOnlyConnectFailed");
        }
        finally
        {
            if (ownsBusyState)
            {
                IsBusy = false;
            }
        }
    }

    public async Task ReconnectChatAsync()
    {
        var channel = ActiveChannel;
        if (channel is null || !_reconnectingChannels.Add(channel.ChannelLogin))
        {
            return;
        }

        ClearSendRestriction(channel);

        var ownsBusyState = !IsBusy;
        if (ownsBusyState)
        {
            IsBusy = true;
        }
        _logger.Info($"Reconnect requested: channel={channel.ChannelLogin}");
        SetChannelConnectionState(channel, "connecting");
        try
        {
            if (!channel.IsPrimaryAccountChannel)
            {
                if (!_authService.HasAccessToken || !_authService.HasScope("chat:read"))
                {
                    SetChannelConnectionState(channel, "error", L("ChatReadRequiresSignIn"));
                    StatusText = L("ChatReadRequiresSignIn");
                    return;
                }

                _logger.Info($"IRC channel part requested: {channel.ChannelLogin}");
                await _readOnlyChatClient.PartChannelAsync(channel.ChannelLogin, _disposeCts.Token).ConfigureAwait(true);
                _logger.Info($"IRC channel join requested: {channel.ChannelLogin}");
                await _readOnlyChatClient.JoinChannelAsync(channel.ChannelLogin, _disposeCts.Token).ConfigureAwait(true);
                var ircConnected = await _readOnlyChatClient
                    .WaitForChannelJoinAsync(channel.ChannelLogin, TimeSpan.FromSeconds(20), _disposeCts.Token)
                    .ConfigureAwait(true);
                SetChannelConnectionState(
                    channel,
                    ircConnected ? "connected" : "error",
                    ircConnected ? null : L("WatchOnlyConnectFailed"));
                if (ircConnected)
                {
                    await RefreshModerationAccessAsync(channel).ConfigureAwait(true);
                }
                return;
            }

            if (!IsAccountAuthenticated || _currentUser is null)
            {
                SetChannelConnectionState(channel, "error", L("SignInFirst"));
                return;
            }

            _broadcaster = SessionToUser(channel);
            StatusText = L("ChannelConnecting");
            await StartChatLogSessionAsync().ConfigureAwait(true);
            _logger.Info("Primary EventSub connecting.");
            await _eventSubClient.StartAsync(channel.BroadcasterId, _currentUser.Id, _disposeCts.Token).ConfigureAwait(true);
            TrackBackgroundTask(RefreshChannelAssetsInBackgroundAsync());

            foreach (var session in Channels.Where(item => !string.IsNullOrWhiteSpace(item.BroadcasterId)))
            {
                await _eventSubClient.TrySubscribeChatMessageAsync(session.BroadcasterId, _disposeCts.Token).ConfigureAwait(true);
                await _eventSubClient.TrySubscribeChannelPointsAsync(session.BroadcasterId, _disposeCts.Token).ConfigureAwait(true);
                await _eventSubClient.TrySubscribeModerationAsync(
                    session.BroadcasterId,
                    _currentUser.Id,
                    session.CanModerate,
                    _disposeCts.Token).ConfigureAwait(true);
            }

            var connected = await _eventSubClient
                .WaitForInitialConnectionAsync(TimeSpan.FromSeconds(25), _disposeCts.Token)
                .ConfigureAwait(true);
            if (connected)
            {
                SetChannelConnectionState(channel, "connected");
                _logger.Info("Primary EventSub connected.");
                if (!_authService.HasScope(AuthService.ChannelPointsScope))
                {
                    StatusText = L("ChannelPointsSignInAgain");
                }
            }
            else
            {
                SetChannelConnectionState(channel, "error", L("WatchOnlyConnectFailed"));
                StatusText = L("ChatConnectTimedOut");
                _logger.Warn("Primary EventSub timeout.");
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("Chat connect failed", ex);
            var message = UserFacingError(ex, "ChatConnectFailed");
            SetChannelConnectionState(channel, "error", message);
            StatusText = message;
        }
        finally
        {
            _reconnectingChannels.Remove(channel.ChannelLogin);
            if (ownsBusyState)
            {
                IsBusy = false;
            }
        }
    }

    public async Task OpenSettingsAsync()
    {
        var previous = Settings.Clone();
        var previousClientId = AppTwitchDefaults.GetClientId(previous);
        var editable = Settings.Clone();
        if (Application.Current.MainWindow is not MainWindow mainWindow)
        {
            return;
        }

        var result = await mainWindow.OpenSettingsPanelAsync(
            editable,
            TestOverlayFromSettingsAsync,
            isSignedIn: IsSignedIn,
            isReadOnlyMode: IsReadOnlyMode,
            accountDisplayName: _currentUser?.DisplayName ?? string.Empty,
            accountLogin: _currentUser?.Login ?? string.Empty,
            accountProfileImageUrl: _currentUser?.ProfileImageUrl ?? string.Empty,
            readOnlyChannel: IsReadOnlyMode
                ? ActiveChannel?.ChannelLogin ?? string.Empty
                : Settings.LastReadOnlyChannel).ConfigureAwait(true);

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
            LocalizationService.ApplyToResources(Settings.Language);
            RefreshLocalizedText();
            await ConfigureOverlayAsync(showErrors: false).ConfigureAwait(true);
            return;
        }

        CopySettings(editable, Settings);
        _settingsService.Save(Settings);
        LocalizationService.ApplyToResources(Settings.Language);
        AnimationService.SetReduceMotion(Settings.ReduceMotion);
        ApplyTheme();
        RefreshLocalizedText();
        if (previous.MessageLimit != Settings.MessageLimit)
        {
            var pendingLimit = LiveChatBufferPolicy.GetTarget(Settings.MessageLimit);
            foreach (var channel in Channels)
            {
                while (channel.PendingVisualMessages.Count > pendingLimit)
                {
                    channel.PendingVisualMessages.Dequeue();
                }

                if (channel.AutoScroll)
                {
                    TrimMessagesToLimit(channel, force: true);
                }
            }
        }
        OnPropertyChanged(nameof(FontSize));
        OnPropertyChanged(nameof(ShowTimestamps));
        OnPropertyChanged(nameof(ShowBadges));
        OnPropertyChanged(nameof(AlwaysOnTop));
        OnPropertyChanged(nameof(UseTornBlackMessageTheme));
        OnPropertyChanged(nameof(MessagePrimaryBrush));
        OnPropertyChanged(nameof(MessageSecondaryBrush));
        OnPropertyChanged(nameof(MessageMutedBrush));
        OnPropertyChanged(nameof(WindowControlsAlignment));
        OnPropertyChanged(nameof(WindowControlsFlowDirection));
        OnPropertyChanged(nameof(UseWindowsWindowControls));
        OnPropertyChanged(nameof(UseMacWindowControls));
        StatusText = L("SettingsSaved");
        await ConfigureOverlayAsync(showErrors: true).ConfigureAwait(true);
        if (HasChatLogSettingsChanged(previous, Settings))
        {
            await _chatLogService.ResetChannelSessionsAsync().ConfigureAwait(true);
            if (IsAccountAuthenticated && Channels.Any(channel => channel.IsPrimaryAccountChannel))
            {
                await StartChatLogSessionAsync().ConfigureAwait(true);
            }
            else
            {
                await _chatLogService.StopSessionAsync().ConfigureAwait(true);
            }
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
        var emoteSettingsChanged =
            previous.EnableTwitchEmotes != Settings.EnableTwitchEmotes ||
            previous.EnableBttvEmotes != Settings.EnableBttvEmotes ||
            previous.EnableSevenTvEmotes != Settings.EnableSevenTvEmotes;
        var badgesEnabled = !previous.EnableBadges && Settings.EnableBadges;

        if (clientChanged)
        {
            await LogoutAsync().ConfigureAwait(true);
            StatusText = L("ClientIdChangedSignInAgain");
            return;
        }

        if (emoteSettingsChanged)
        {
            RefreshEmoteSettings();
        }

        if (badgesEnabled)
        {
            RefreshBadgeSettings();
        }
        else if (previous.EnableBadges && !Settings.EnableBadges)
        {
            ReleaseAllBadgeImages();
        }
    }

    private async Task OpenChatLogsAsync()
    {
        if (Application.Current.MainWindow is not MainWindow mainWindow)
        {
            return;
        }

        await mainWindow.OpenChatLogsPanelAsync(Settings.Clone(), _chatLogService).ConfigureAwait(true);
    }

    private async Task OpenModerationAsync()
    {
        if (Application.Current.MainWindow is MainWindow mainWindow)
        {
            await mainWindow.OpenModerationPanelAsync(this).ConfigureAwait(true);
        }
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

        var activeChannel = ActiveChannel;
        if (_currentUser is null || activeChannel is null || string.IsNullOrWhiteSpace(activeChannel.BroadcasterId))
        {
            _dialogs.ShowError("Twitch", L("SignInFirst"));
            return;
        }

        if (!activeChannel.IsConnected)
        {
            _dialogs.ShowError("Twitch", L("ChatNotConnected"));
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _apiClient.SendChatMessageAsync(
                activeChannel.BroadcasterId,
                _currentUser.Id,
                text,
                _disposeCts.Token).ConfigureAwait(true);
            if (!result.IsSent)
            {
                if (TryGetSendRestrictionType(result.DropCode, out var restrictionType))
                {
                    SetSendRestriction(activeChannel, restrictionType, null);
                }
                else
                {
                    _dialogs.ShowError(UiL("SendMessageRejectedTitle"), GetSendMessageDropText(result));
                }
                return;
            }

            ClearSendRestriction(activeChannel);

            var localMessage = new ChatMessageModel
            {
                Id = result.MessageId,
                MessageId = result.MessageId,
                Timestamp = DateTimeOffset.Now,
                UserId = _currentUser.Id,
                ChannelLogin = activeChannel.ChannelLogin,
                Login = _currentUser.Login,
                DisplayName = _currentUser.DisplayName,
                Text = text,
                Color = "#CFA8FF",
                IsLocalEcho = true
            };

            ApplyKnownSenderPresentation(activeChannel, localMessage);
            localMessage.Parts.Add(ChatMessagePartModel.TextPart(text));
            PrepareMessageForDisplay(localMessage, activeChannel);
            AddMessage(activeChannel, localMessage);
            OutgoingMessage = string.Empty;
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("Send chat message failed", ex);
            _dialogs.ShowError(L("Error"), UserFacingError(ex, "SendMessageFailed"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string GetSendMessageDropText(SendChatMessageResult result)
    {
        var key = (result.DropCode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "automod_held" => "SendMessageAutoModHeld",
            "blocked_term" => "SendMessageBlockedTerm",
            "msg_duplicate" => "SendMessageDuplicate",
            "msg_ratelimit" => "SendMessageRateLimited",
            "followers_only" => "SendMessageFollowersOnly",
            "sub_only" or "shared_chat_sub_only" => "SendMessageSubscribersOnly",
            "msg_emoteonly" => "SendMessageEmoteOnly",
            "msg_requires_verified_phone_number" => "SendMessagePhoneRequired",
            "msg_banned" or "banned" or "user_banned" => "SendMessageBanned",
            "msg_timedout" or "timed_out" or "timeout" => "SendMessageTimedOut",
            "msg_channel_blocked" => "SendMessageNoPermission",
            "no_permission" => "SendMessageNoPermission",
            "restricted_chat" => "SendMessageRestrictedChat",
            "channel_suspended" => "SendMessageChannelSuspended",
            _ => "SendMessageRejectedGeneric"
        };

        return UiL(key);
    }

    private static bool TryGetSendRestrictionType(string? dropCode, out PunishmentType punishmentType)
    {
        punishmentType = (dropCode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "msg_banned" or "banned" or "user_banned" => PunishmentType.Ban,
            "msg_timedout" or "timed_out" or "timeout" => PunishmentType.Timeout,
            _ => PunishmentType.Unknown
        };
        return punishmentType != PunishmentType.Unknown;
    }

    private async Task ConfigureOverlayAsync(bool showErrors)
    {
        try
        {
            await _overlayServer.ConfigureAsync(Settings).ConfigureAwait(true);
            if (Settings.EnableObsOverlay)
            {
                StatusText = string.Format(
                    CultureInfo.CurrentCulture,
                    L("OverlayRunningFormat"),
                    Settings.OverlayUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("OBS overlay start failed", ex);
            var message = UserFacingError(ex, "OverlayUnavailable");
            StatusText = message;
            if (showErrors)
            {
                _dialogs.ShowError(L("ObsOverlay"), message);
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
        return string.Format(CultureInfo.CurrentCulture, LocalizationService.Get(settings.Language, "OverlayTestSent"), settings.OverlayUrl);
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

    public async Task TimeoutTenMinutesAsync(ChatMessageModel? message)
    {
        if (!CanModerateMessage(message))
        {
            return;
        }

        await RunModerationAsync(message!, new ModerationRequest { DurationSeconds = 600 }, false).ConfigureAwait(true);
    }

    public async Task CustomTimeoutUserAsync(ChatMessageModel? message)
    {
        if (!CanModerateMessage(message))
        {
            return;
        }

        var targetSession = FindSessionForMessage(message!);
        if (targetSession is null)
        {
            return;
        }

        var request = _dialogs.ShowCustomTimeoutDialog(message!, targetSession.DisplayName);
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
            StatusText = _dialogs.CopyText(message.Login)
                ? L("UsernameCopied")
                : L("ClipboardUnavailable");
        }
    }

    public async Task LoadUserProfileAsync(ChatMessageModel? message)
    {
        if (message is null || string.IsNullOrWhiteSpace(message.UserId) || message.HasProfileImage || !IsAccountAuthenticated)
        {
            return;
        }

        var profile = await GetCachedUserProfileAsync(message.UserId).ConfigureAwait(true);
        if (profile is not null)
        {
            message.ProfileImageUrl = profile.ProfileImageUrl;
        }
    }

    public void CopyMessage(ChatMessageModel? message)
    {
        if (message is not null)
        {
            StatusText = _dialogs.CopyText(message.Text)
                ? L("MessageCopied")
                : L("ClipboardUnavailable");
        }
    }

    public void OpenUserOnTwitch(ChatMessageModel? message)
    {
        if (message is not null && !string.IsNullOrWhiteSpace(message.Login))
        {
            OpenUserOnTwitch(message.Login);
        }
    }

    public void OpenUserOnTwitch(string? login)
    {
        var normalizedLogin = (login ?? string.Empty).Trim().TrimStart('@');
        if (!string.IsNullOrWhiteSpace(normalizedLogin))
        {
            if (!_dialogs.OpenUrl("https://www.twitch.tv/" + Uri.EscapeDataString(normalizedLogin)))
            {
                StatusText = L("OpenLinkFailed");
            }
        }
    }

    public async Task LogoutAsync()
    {
        var ownsBusyState = !IsBusy;
        if (ownsBusyState)
        {
            IsBusy = true;
        }

        _logger.Info("Logout requested.");
        try
        {
            try
            {
                await Task.WhenAll(
                    _eventSubClient.StopAsync(),
                    _readOnlyChatClient.StopAsync()).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Chat transports stopped during logout with {ex.GetType().Name}");
            }

            StopStreamStatusPolling();
            StopPinnedMessagePolling();
            CancelChannelAssetRefresh();
            _authService.Logout();
            try
            {
                await _chatLogService.StopSessionAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Chat log session stopped during logout with {ex.GetType().Name}");
            }

            _currentUser = null;
            ConnectionMode = ChatConnectionMode.ReadOnly;
            Settings.ConnectionMode = ConnectionMode;
            foreach (var channel in Channels)
            {
                channel.IsPrimaryAccountChannel = false;
                channel.CanSend = false;
                channel.CanModerate = false;
                channel.IsBroadcaster = false;
                channel.IsModerator = false;
                channel.ModerationCheckCompleted = false;
                channel.ModerationStatus = string.Empty;
                channel.ModerationCheckError = string.Empty;
                ClearRestrictedModerationData(channel);
                ClearPinnedMessage(channel);
                SetChannelConnectionState(channel, "disconnected");
            }

            await _moderationCacheService.ClearAsync().ConfigureAwait(true);

            _broadcaster = ActiveChannel is null ? null : SessionToUser(ActiveChannel);
            IsConnected = false;
            IsConnecting = false;
            SetGuestAccountState();
            FiltersVisible = false;
            StatusText = L("TwitchDisconnected");
            UpdateAccountState("not signed in");
            if (ActiveChannel is null)
            {
                UpdateChatState("disconnected");
            }
            else
            {
                UpdateChatState("disconnected", L("ChatDisconnected"));
            }

            UpdateStreamStatus(new StreamStatusInfo(false, 0, string.Empty));
            PersistChannels();
            RaiseStatePropertiesChanged();

        }
        finally
        {
            if (ownsBusyState)
            {
                IsBusy = false;
            }
        }
    }

    internal void BeginShutdown()
    {
        lock (_backgroundTaskGate)
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            {
                return;
            }
        }

        DetachExternalEventHandlers();
        CancelSafely(_oauthSignInCts);
        CancelSafely(_disposeCts);
        StopStreamStatusPolling();
        StopPinnedMessagePolling();
        CancelChannelAssetRefresh();
        _filterTimer.Stop();
        _messageBatchTimer.Stop();
        ClearPendingChatMessages();
        Interlocked.Exchange(ref _messageDrainScheduled, 0);
        RaiseCommandState();
    }

    public async ValueTask DisposeAsync()
    {
        BeginShutdown();
        ClearPendingChatMessages();
        try
        {
            await Task.WhenAll(
                _streamStatusTask ?? Task.CompletedTask,
                _pinnedMessageTask ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _streamStatusTask = null;
        _pinnedMessageTask = null;
        _filterTimer.Stop();
        _messageBatchTimer.Stop();
        DetachExternalEventHandlers();
        await WaitForActiveCommandsAsync().ConfigureAwait(false);
        await DrainBackgroundTasksAsync().ConfigureAwait(false);
        await _eventSubClient.DisposeAsync().ConfigureAwait(false);
        await _readOnlyChatClient.DisposeAsync().ConfigureAwait(false);
        await _overlayServer.DisposeAsync().ConfigureAwait(false);
        await _chatLogService.DisposeAsync().ConfigureAwait(false);
        await _moderationCacheService.DisposeAsync().ConfigureAwait(false);
        _badgeService.Dispose();
        _thirdPartyEmoteService.Dispose();
        await _emoteCache.DisposeAsync().ConfigureAwait(false);
        _apiClient.Dispose();
        _authService.Dispose();
        _messageHydrationGate.Dispose();
        _disposeCts.Dispose();
    }

    private void DetachExternalEventHandlers()
    {
        _eventSubClient.MessageReceived -= OnEventSubMessageReceived;
        _eventSubClient.SharedChatSessionChanged -= OnSharedChatSessionChanged;
        _eventSubClient.StatusChanged -= OnEventSubStatusChanged;
        _eventSubClient.ChannelPointsAuthorizationRequired -= OnChannelPointsAuthorizationRequired;
        _eventSubClient.ChannelPointsCapabilityChanged -= OnChannelPointsCapabilityChanged;
        _eventSubClient.ChatMessageDeleted -= OnChatMessageDeleted;
        _eventSubClient.UserMessagesCleared -= OnUserMessagesCleared;
        _eventSubClient.UserBanned -= OnEventSubUserBanned;
        _eventSubClient.UserUnbanned -= OnEventSubUserUnbanned;
        _eventSubClient.UnbanRequestCreated -= OnUnbanRequestCreated;
        _eventSubClient.UnbanRequestResolved -= OnUnbanRequestResolved;
        _eventSubClient.AutoModMessageHeld -= OnAutoModMessageHeld;
        _eventSubClient.AutoModMessageUpdated -= OnAutoModMessageUpdated;
        _readOnlyChatClient.MessageReceived -= OnReadOnlyChannelMessageReceived;
        _readOnlyChatClient.ChannelStatusChanged -= OnReadOnlyChannelStatusChanged;
        _readOnlyChatClient.ChannelIdentityResolved -= OnReadOnlyChannelIdentityResolved;
        _readOnlyChatClient.UserModerated -= OnIrcUserModerated;
        _readOnlyChatClient.MessageDeleted -= OnIrcMessageDeleted;
        _readOnlyChatClient.ChatCleared -= OnIrcChatCleared;
        _chatLogService.WriteFailed -= OnChatLogWriteFailed;
        _authService.SessionInvalidated -= OnAuthSessionInvalidated;
    }

    private async Task WaitForActiveCommandsAsync()
    {
        var commands = new ICommand[]
        {
            ConnectCommand,
            SignInCommand,
            ReconnectCommand,
            OpenSettingsCommand,
            OpenChatLogsCommand,
            OpenModerationCommand,
            LogoutCommand,
            SendMessageCommand,
            AddChannelCommand,
            RemoveChannelCommand
        };
        var tasks = commands
            .OfType<AsyncRelayCommand>()
            .Select(command => command.ExecutionTask)
            .Where(task => !task.IsCompleted)
            .ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Active command shutdown failed: {ex.GetBaseException().GetType().Name}");
        }
    }

    private void TrackBackgroundTask(Task task)
    {
        lock (_backgroundTaskGate)
        {
            if (!IsShuttingDown)
            {
                _backgroundTasks.TryAdd(task, 0);
            }
        }
        _ = task.ContinueWith(
            completed =>
            {
                _backgroundTasks.TryRemove(completed, out _);
                if (completed.Exception is { } exception)
                {
                    _logger.Warn($"Background operation failed: {exception.GetBaseException().GetType().Name}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal async Task ObserveInteractiveTaskAsync(Task task)
    {
        TrackBackgroundTask(task);
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Interactive operation failed: {ex.GetBaseException().GetType().Name}");
        }
    }

    private async Task DrainBackgroundTasksAsync()
    {
        while (!_backgroundTasks.IsEmpty)
        {
            var tasks = _backgroundTasks.Keys.ToArray();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // Individual failures are observed and logged by TrackBackgroundTask.
            }
        }
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
        _currentUser = await _apiClient.GetCurrentUserAsync(_disposeCts.Token).ConfigureAwait(true);
        _authService.SaveProfile(_currentUser);
        _logger.Info($"OAuth success: user id={_currentUser.Id}, login={_currentUser.Login}");
        _logger.Info($"Account signed in: {_currentUser.Login}");
        IsConnected = true;
        UpdateAccountState("signed in");
        ProfileImageUrl = _currentUser.ProfileImageUrl;
        DisplayNameCompact = string.IsNullOrWhiteSpace(_currentUser.DisplayName) ? _currentUser.Login : _currentUser.DisplayName;
        AvatarInitial = CreateAvatarInitial(DisplayNameCompact, _currentUser.Login);
        RaiseStatePropertiesChanged();

        var broadcaster = _currentUser;

        foreach (var channel in Channels)
        {
            channel.IsPrimaryAccountChannel = false;
        }

        var primary = FindChannel(broadcaster.Login);
        if (primary is null && Channels.Count >= 3)
        {
            await RemoveChannelAsync(Channels[^1]).ConfigureAwait(true);
        }

        primary = FindChannel(broadcaster.Login);
        if (primary is null)
        {
            primary = new ChannelSessionViewModel(broadcaster.Login);
            Channels.Insert(0, primary);
        }
        else
        {
            var primaryIndex = Channels.IndexOf(primary);
            if (primaryIndex > 0)
            {
                Channels.Move(primaryIndex, 0);
            }
        }

        primary.IsPrimaryAccountChannel = true;
        primary.IsBroadcaster = true;
        primary.IsModerator = false;
        primary.CanModerate = true;
        primary.ModerationCheckCompleted = true;
        primary.ModerationStatus = L("ModeratorPermissionsConfirmed");
        primary.BroadcasterId = broadcaster.Id;
        _moderationCacheService.RestoreSession(primary);
        primary.DisplayName = string.IsNullOrWhiteSpace(broadcaster.DisplayName) ? broadcaster.Login : broadcaster.DisplayName;
        primary.ProfileImageUrl = broadcaster.ProfileImageUrl;
        SetChannelConnectionState(primary, "connecting");
        _broadcaster = broadcaster;
        _logger.Info($"Primary channel promoted: {primary.ChannelLogin}");

        foreach (var login in Settings.SavedChannelLogins
                     .Where(login => !string.Equals(login, broadcaster.Login, StringComparison.OrdinalIgnoreCase))
                     .Where(login => FindChannel(login) is null)
                     .Take(Math.Max(0, 3 - Channels.Count)))
        {
            await AddReadOnlyChannelSessionAsync(
                login,
                activate: false)
                .ConfigureAwait(true);
        }

        foreach (var channel in Channels)
        {
            channel.CanSend = !string.IsNullOrWhiteSpace(channel.BroadcasterId);
            await RefreshModerationAccessAsync(channel).ConfigureAwait(true);
        }

        try
        {
            await _readOnlyChatClient.JoinChannelAsync(primary.ChannelLogin, _disposeCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"IRC JOIN for primary moderation events failed: {ex.GetType().Name}");
        }

        ActiveChannel = primary;
        PersistChannels();
        OnPropertyChanged(nameof(CanModerate));
        OnPropertyChanged(nameof(CanUseCreatorControls));
        TrackBackgroundTask(RefreshModerationStateInBackgroundAsync(primary));
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
            status = await _streamStatusService.GetStatusAsync(_broadcaster.Id, _disposeCts.Token).ConfigureAwait(false);
            if (!status.IsAuthoritative &&
                Channels.FirstOrDefault(channel => channel.IsPrimaryAccountChannel) is { HasAuthoritativeStreamStatus: true } primary)
            {
                status = new StreamStatusInfo(
                    primary.IsLive,
                    primary.ViewerCount,
                    primary.StreamTitle,
                    primary.GameName,
                    primary.StreamStartedAt);
            }

            if (status.IsAuthoritative)
            {
                await Application.Current.Dispatcher.InvokeAsync(() => UpdateStreamStatus(status));
            }
        }

        await _chatLogService.StartSessionAsync(Settings, _broadcaster, status, _disposeCts.Token).ConfigureAwait(false);
    }

    private async Task RefreshChannelAssetsAsync(CancellationToken cancellationToken = default)
    {
        if (_broadcaster is null)
        {
            return;
        }

        var broadcasterId = _broadcaster.Id;
        var targetSession = Channels.FirstOrDefault(channel =>
            string.Equals(channel.BroadcasterId, broadcasterId, StringComparison.Ordinal));
        await Task.WhenAll(
            targetSession is not null && Settings.EnableBadges
                ? RunAssetStepAsync(
                    "Twitch badges",
                    () => RefreshBadgeCatalogForSessionAsync(targetSession, broadcasterId, cancellationToken))
                : Task.CompletedTask,
            RunAssetStepAsync("Third-party emotes", () => _thirdPartyEmoteService.RefreshAsync(
                broadcasterId,
                Settings.EnableBttvEmotes,
                Settings.EnableSevenTvEmotes,
                cancellationToken)),
            RunAssetStepAsync("Stream status", () => RefreshStreamStatusAsync(cancellationToken))).ConfigureAwait(false);

        if (targetSession is not null && !cancellationToken.IsCancellationRequested)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!Channels.Contains(targetSession) ||
                    !string.Equals(targetSession.BroadcasterId, broadcasterId, StringComparison.Ordinal))
                {
                    return;
                }

                InvalidateMessagePresentations(targetSession);
            }, DispatcherPriority.Background, cancellationToken);
        }

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
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Channel assets refresh skipped: {ex.GetType().Name}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _channelAssetsCts, null, cancellation);
            cancellation.Dispose();
        }
    }

    private async Task RefreshReadOnlyChannelAssetsAsync(string channelLogin, string broadcasterId)
    {
        var cancellation = BeginChannelAssetRefresh(channelLogin);
        var targetSession = FindChannel(channelLogin);
        try
        {
            await Task.WhenAll(
                _thirdPartyEmoteService.RefreshAsync(
                    broadcasterId,
                    Settings.EnableBttvEmotes,
                    Settings.EnableSevenTvEmotes,
                    cancellation.Token),
                targetSession is not null && Settings.EnableBadges
                    ? RefreshBadgeCatalogForSessionAsync(targetSession, broadcasterId, cancellation.Token)
                    : Task.CompletedTask).ConfigureAwait(false);

            cancellation.Token.ThrowIfCancellationRequested();
            if (targetSession is not null)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!Channels.Contains(targetSession) ||
                        !string.Equals(targetSession.BroadcasterId, broadcasterId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    InvalidateMessagePresentations(targetSession);
                }, DispatcherPriority.Background, cancellation.Token);
            }

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
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Read-only channel emotes refresh failed: {ex.GetType().Name}");
        }
        finally
        {
            lock (_channelAssetRefreshes)
            {
                if (_channelAssetRefreshes.TryGetValue(channelLogin, out var current) &&
                    ReferenceEquals(current, cancellation))
                {
                    _channelAssetRefreshes.Remove(channelLogin);
                }
            }

            cancellation.Dispose();
        }
    }

    private void RefreshEmoteSettings()
    {
        InvalidateMessagePresentations();
        foreach (var channel in Channels)
        {
            CancelChannelAssetRefresh(channel.ChannelLogin);
            if (!string.IsNullOrWhiteSpace(channel.BroadcasterId))
            {
                TrackBackgroundTask(RefreshReadOnlyChannelAssetsAsync(channel.ChannelLogin, channel.BroadcasterId));
            }
        }
    }

    public async Task DeleteMessageAsync(ChatMessageModel? message)
    {
        var targetSession = message is null ? null : FindSessionForMessage(message);
        var moderator = _currentUser;
        if (message is null || targetSession is null || moderator is null || string.IsNullOrWhiteSpace(message.MessageId) ||
            !CanDeleteMessage(message)) return;
        var targetBroadcasterId = targetSession.BroadcasterId;
        var targetMessageId = message.MessageId;
        var operationKey = targetBroadcasterId + "\n" + targetMessageId;
        if (!_messageDeleteOperationsInProgress.Add(operationKey))
        {
            return;
        }
        try
        {
            await _moderationService.DeleteMessageAsync(targetBroadcasterId, moderator.Id, targetMessageId, _disposeCts.Token).ConfigureAwait(true);
            TryObserveModerationEvent(targetBroadcasterId + "\n" + targetMessageId + "\nDelete");
            MarkMessagesModerated(
                targetSession,
                candidate => string.Equals(candidate.MessageId, targetMessageId, StringComparison.Ordinal),
                ModerationMessageState.Deleted,
                moderator.Id,
                moderator.DisplayName);
            StatusText = L("MessageDeleted");
        }
        catch (TwitchApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            MarkMessagesModerated(targetSession, candidate => string.Equals(candidate.MessageId, targetMessageId, StringComparison.Ordinal), ModerationMessageState.Deleted);
            StatusText = L("MessageDeleted");
        }
        catch (TwitchApiException ex)
        {
            _logger.Warn($"Delete message failed: status={(int)ex.StatusCode}");
            StatusText = ex.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? L("ModerationSignInRequired")
                : L("ModerationActionFailed");
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Delete message failed: {ex.GetType().Name}");
            StatusText = L("ModerationActionFailed");
        }
        finally
        {
            _messageDeleteOperationsInProgress.Remove(operationKey);
        }
    }

    public bool CanShowDeleteMessage(ChatMessageModel? message) =>
        message is not null && !message.IsModerated && !string.IsNullOrWhiteSpace(message.MessageId) &&
        CanModerateTarget(message) && _authService.HasScope(AuthService.ChatModerationScope);

    public bool CanDeleteMessage(ChatMessageModel? message) =>
        CanShowDeleteMessage(message) && message is not null && FindSessionForMessage(message) is { } session &&
        !_messageDeleteOperationsInProgress.Contains(session.BroadcasterId + "\n" + message.MessageId);

    public bool IsModerationOperationInProgress(ChatMessageModel? message, bool permanentBan)
    {
        var session = message is null ? null : FindSessionForMessage(message);
        return session is not null && message is not null &&
               _moderationOperationsInProgress.Contains(
                   session.BroadcasterId + "\n" + message.UserId + "\n" + (permanentBan ? "ban" : "timeout"));
    }

    private void RefreshBadgeSettings()
    {
        foreach (var channel in Channels.Where(channel => !string.IsNullOrWhiteSpace(channel.BroadcasterId)))
        {
            TrackBackgroundTask(RefreshBadgeCatalogForSessionAsync(channel, channel.BroadcasterId, _disposeCts.Token));
        }
    }

    private async Task RefreshBadgeCatalogForSessionAsync(
        ChannelSessionViewModel targetSession,
        string targetBroadcasterId,
        CancellationToken cancellationToken)
    {
        var generation = targetSession.BadgeCatalogGeneration + 1;
        targetSession.BadgeCatalogGeneration = generation;

        var snapshot = await _badgeService.RefreshAsync(targetBroadcasterId, cancellationToken).ConfigureAwait(false);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!Channels.Contains(targetSession) ||
                targetSession.BadgeCatalogGeneration != generation ||
                !string.Equals(targetSession.BroadcasterId, targetBroadcasterId, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var message in targetSession.Messages.TakeLast(LiveChatBufferPolicy.RecentMessageAssetRefreshLimit))
            {
                ApplyBadgesForMessage(message, targetSession);
                if (ReferenceEquals(targetSession, ActiveChannel) &&
                    message.Badges.Any(badge => !string.IsNullOrWhiteSpace(badge.ImageUrl) && badge.ImageSource is null))
                {
                    QueueMessageImageLoad(message);
                }
            }

            if (string.IsNullOrWhiteSpace(snapshot.Error))
            {
                _logger.Info(
                    $"Channel badges loaded: channel={targetSession.ChannelLogin}, broadcasterId={targetBroadcasterId}, " +
                    $"sets={snapshot.SetCount}, versions={snapshot.VersionCount}");
            }
            else
            {
                _logger.Warn(
                    $"Channel badge catalog refresh was incomplete: channel={targetSession.ChannelLogin}, " +
                    $"broadcasterId={targetBroadcasterId}, error={snapshot.Error}");
            }
        }, DispatcherPriority.Background, cancellationToken);
    }

    private CancellationTokenSource BeginChannelAssetRefresh()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _channelAssetsCts, next);
        CancelSafely(previous);
        return next;
    }

    private void CancelChannelAssetRefresh()
    {
        var cancellation = Interlocked.Exchange(ref _channelAssetsCts, null);
        CancelSafely(cancellation);
        lock (_channelAssetRefreshes)
        {
            foreach (var channelCancellation in _channelAssetRefreshes.Values)
            {
                CancelSafely(channelCancellation);
            }

            _channelAssetRefreshes.Clear();
        }
    }

    private CancellationTokenSource BeginChannelAssetRefresh(string channelLogin)
    {
        var next = new CancellationTokenSource();
        lock (_channelAssetRefreshes)
        {
            if (_channelAssetRefreshes.Remove(channelLogin, out var previous))
            {
                CancelSafely(previous);
            }

            _channelAssetRefreshes[channelLogin] = next;
        }

        return next;
    }

    private void CancelChannelAssetRefresh(string channelLogin)
    {
        lock (_channelAssetRefreshes)
        {
            if (_channelAssetRefreshes.Remove(channelLogin, out var cancellation))
            {
                CancelSafely(cancellation);
            }
        }
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

    private void PrepareMessageForDisplay(ChatMessageModel message, ChannelSessionViewModel? knownSession = null)
    {
        var session = knownSession ?? FindChannel(message.ChannelLogin) ?? ActiveChannel;
        ApplyStableUserColor(message, session);
        ApplyBadgesForMessage(message, session);
        HydrateCachedBadges(message);
        var channelKey = session is null ? string.Empty : ChannelAssetKey(session);

        var sourceParts = message.OriginalParts;

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
                    part.Media = null;
                    message.Parts.Add(ChatMessagePartModel.TextPart(part.Text));
                }

                continue;
            }

            if (part.Kind == ChatMessagePartKind.Text && (Settings.EnableBttvEmotes || Settings.EnableSevenTvEmotes))
            {
                foreach (var renderedPart in RenderThirdPartyTextPart(part.Text, channelKey))
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
        message.RefreshLongMessageState();
        message.PresentationVersion = _messagePresentationVersion;

        if (message.IsSharedChatMessage && string.IsNullOrWhiteSpace(message.SourceChannelLabel))
        {
            QueueSourceChannelIdentityResolution(message);
        }

    }

    private void QueueSourceChannelIdentityResolution(ChatMessageModel message)
    {
        var sourceBroadcasterId = string.IsNullOrWhiteSpace(message.SourceBroadcasterId)
            ? message.SourceRoomId
            : message.SourceBroadcasterId;
        if (string.IsNullOrWhiteSpace(sourceBroadcasterId))
        {
            return;
        }

        var known = Channels.FirstOrDefault(channel =>
            string.Equals(channel.BroadcasterId, sourceBroadcasterId, StringComparison.Ordinal));
        if (known is not null)
        {
            message.SourceChannelLogin = known.ChannelLogin;
            message.SourceChannelDisplayName = known.DisplayName;
            return;
        }

        if (_sourceChannelIdentities.TryGetValue(sourceBroadcasterId, out var cached))
        {
            message.SourceChannelLogin = cached.Login;
            message.SourceChannelDisplayName = cached.DisplayName;
            return;
        }

        if (_sourceChannelIdentityRequests.TryAdd(sourceBroadcasterId, 0))
        {
            TrackBackgroundTask(ResolveSourceChannelIdentityAsync(sourceBroadcasterId));
        }
    }

    private async Task ResolveSourceChannelIdentityAsync(string sourceBroadcasterId)
    {
        try
        {
            var user = await GetCachedUserProfileAsync(sourceBroadcasterId).ConfigureAwait(false);
            if (user is null || _disposeCts.IsCancellationRequested)
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_sourceChannelIdentities.TryAdd(sourceBroadcasterId, user))
                {
                    _sourceChannelIdentityOrder.Enqueue(sourceBroadcasterId);
                    while (_sourceChannelIdentities.Count > SourceIdentityCacheLimit &&
                           _sourceChannelIdentityOrder.TryDequeue(out var oldest))
                    {
                        _sourceChannelIdentities.Remove(oldest);
                    }
                }

                foreach (var pendingMessage in Channels
                             .SelectMany(channel => channel.Messages.Concat(channel.PendingVisualMessages))
                             .Where(candidate => string.Equals(
                                 string.IsNullOrWhiteSpace(candidate.SourceBroadcasterId)
                                     ? candidate.SourceRoomId
                                     : candidate.SourceBroadcasterId,
                                 sourceBroadcasterId,
                                 StringComparison.Ordinal))
                             .TakeLast(LiveChatBufferPolicy.RecentMessageAssetRefreshLimit))
                {
                    pendingMessage.SourceChannelLogin = user.Login;
                    pendingMessage.SourceChannelDisplayName = user.DisplayName;
                }
            }, DispatcherPriority.Background);
        }
        finally
        {
            _sourceChannelIdentityRequests.TryRemove(sourceBroadcasterId, out _);
        }
    }

    private static void ApplyStableUserColor(ChatMessageModel message, ChannelSessionViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        var userKey = !string.IsNullOrWhiteSpace(message.UserId)
            ? "id:" + message.UserId
            : string.IsNullOrWhiteSpace(message.Login)
                ? string.Empty
                : "login:" + message.Login.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(userKey))
        {
            return;
        }

        if (TryNormalizeChatColor(message.Color, out var officialColor))
        {
            if (!session.UserColors.TryGetValue(userKey, out var cachedColor))
            {
                session.UserColorOrder.Enqueue(userKey);
            }

            session.UserColors[userKey] = officialColor;
            message.Color = officialColor;
        }
        else if (session.UserColors.TryGetValue(userKey, out var cachedColor))
        {
            message.Color = cachedColor;
        }

        while (session.UserColors.Count > UserColorCacheLimit && session.UserColorOrder.Count > 0)
        {
            session.UserColors.Remove(session.UserColorOrder.Dequeue());
        }
    }

    private static bool TryNormalizeChatColor(string? value, out string color)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length == 7 && value[0] == '#' &&
            value.AsSpan(1).ToString().All(Uri.IsHexDigit))
        {
            color = value.ToUpperInvariant();
            return true;
        }

        color = string.Empty;
        return false;
    }

    private void ApplyBadgesForMessage(ChatMessageModel message, ChannelSessionViewModel? session)
    {
        if (!Settings.EnableBadges)
        {
            foreach (var badge in message.Badges)
            {
                badge.ImageSource = null;
            }
            return;
        }

        var badgeBroadcasterId = !string.IsNullOrWhiteSpace(message.BadgeBroadcasterId)
            ? message.BadgeBroadcasterId
            : !string.IsNullOrWhiteSpace(message.RoomId)
                ? message.RoomId
                : session?.BroadcasterId ?? string.Empty;

        _badgeService.ApplyBadgeImages(badgeBroadcasterId, message.Badges);
#if DEBUG
        if (session is not null &&
            !string.IsNullOrWhiteSpace(message.RoomId) &&
            !string.Equals(message.RoomId, session.BroadcasterId, StringComparison.Ordinal) &&
            Interlocked.Increment(ref _badgeMismatchDiagnostics) <= 50)
        {
            Debug.WriteLine(
                $"Badge catalog mismatch: messageRoomId={message.RoomId}, " +
                $"sessionBroadcasterId={session.BroadcasterId}, sourceRoomId={message.SourceRoomId}");
        }
#endif

        if (Settings.EnableBadges &&
            IsAccountAuthenticated &&
            !string.IsNullOrWhiteSpace(badgeBroadcasterId) &&
            !string.Equals(badgeBroadcasterId, session?.BroadcasterId, StringComparison.Ordinal) &&
            !_badgeService.HasChannelCatalog(badgeBroadcasterId))
        {
            QueueSourceBadgeCatalogRefresh(badgeBroadcasterId);
        }
    }

    private void QueueSourceBadgeCatalogRefresh(string broadcasterId)
    {
        lock (_sourceBadgeRefreshes)
        {
            if (!_sourceBadgeRefreshes.Add(broadcasterId))
            {
                return;
            }
        }

        TrackBackgroundTask(RefreshSourceBadgeCatalogAsync(broadcasterId));
    }

    private async Task RefreshSourceBadgeCatalogAsync(string broadcasterId)
    {
        try
        {
            await _badgeService.EnsureChannelCatalogAsync(broadcasterId, _disposeCts.Token).ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var session in Channels)
                {
                    foreach (var message in session.Messages
                                 .Where(message => string.Equals(
                                     message.BadgeBroadcasterId,
                                     broadcasterId,
                                     StringComparison.Ordinal))
                                 .TakeLast(LiveChatBufferPolicy.RecentMessageAssetRefreshLimit))
                    {
                        ApplyBadgesForMessage(message, session);
                        if (ReferenceEquals(session, ActiveChannel) &&
                            message.Badges.Any(badge => !string.IsNullOrWhiteSpace(badge.ImageUrl) && badge.ImageSource is null))
                        {
                            QueueMessageImageLoad(message);
                        }
                    }
                }
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Shared Chat badge catalog load failed: broadcaster_id={broadcasterId}, {ex.GetType().Name}");
        }
        finally
        {
            lock (_sourceBadgeRefreshes)
            {
                _sourceBadgeRefreshes.Remove(broadcasterId);
            }
        }
    }

    private async Task LoadMessageImagesAsync(
        ChatMessageModel message,
        CancellationToken cancellationToken = default,
        bool requireVisible = false)
    {
        var badgeLoadTasks = Settings.EnableBadges
            ? message.Badges
                .Where(badge => badge.ImageSource is null && !string.IsNullOrWhiteSpace(badge.ImageUrl))
                .Select(async badge =>
                {
                    var image = await _emoteCache.GetImageAsync(badge.ImageUrl, cancellationToken).ConfigureAwait(false);
                    return (Action)(() =>
                    {
                        if (Settings.EnableBadges)
                        {
                            badge.ImageSource = image;
                        }
                    });
                })
            : Enumerable.Empty<Task<Action>>();
        var imageLoadTasks = EnumerateImageParts(message.Parts)
            .Where(part => part.Media is null && !string.IsNullOrWhiteSpace(part.ImageUrl))
            .Select(async part =>
            {
                var media = await _emoteCache.GetMediaAsync(
                    part.CacheKey,
                    part.ImageUrl,
                    part.FallbackImageUrl,
                    cancellationToken).ConfigureAwait(false);
#if DEBUG
                LogSevenTvVisualDiagnostics(part, media);
#endif
                return (Action)(() => part.Media = media);
            })
            .Concat(badgeLoadTasks)
            .ToArray();

        if (imageLoadTasks.Length == 0)
        {
            return;
        }

        var updates = await Task.WhenAll(imageLoadTasks).ConfigureAwait(false);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (requireVisible &&
                (_isUserScrolling || !_visibleMessages.ContainsKey(message)))
            {
                return;
            }

            foreach (var update in updates)
            {
                update();
            }
        }, DispatcherPriority.Background, cancellationToken);
    }

    private static IEnumerable<ChatMessagePartModel> EnumerateImageParts(
        IEnumerable<ChatMessagePartModel> parts)
    {
        foreach (var part in parts)
        {
            yield return part;
            foreach (var overlay in EnumerateImageParts(part.OverlayParts))
            {
                yield return overlay;
            }
        }
    }

#if DEBUG
    private void LogSevenTvVisualDiagnostics(ChatMessagePartModel part, EmoteMedia? media)
    {
        if (!string.Equals(part.Provider, "7TV", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_sevenTvVisualDiagnostics)
        {
            if (_sevenTvVisualDiagnostics.Count >= 75 || !_sevenTvVisualDiagnostics.Add(part.EmoteId))
            {
                return;
            }
        }

        var frame = media?.FirstFrame as System.Windows.Media.Imaging.BitmapSource;
        var format = Uri.TryCreate(part.ImageUrl, UriKind.Absolute, out var uri)
            ? Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant()
            : "unknown";
        var renderPath = part.IsZeroWidth ? "zero-width" : media?.IsAnimated == true ? "animated" : "static";
        _logger.Info(
            $"7TV visual: code={part.Text}, id={part.EmoteId}, flags={part.Flags}, zeroWidth={part.IsZeroWidth}, " +
            $"animated={part.DeclaredAnimated}, url={part.ImageUrl}, format={format}, " +
            $"source={part.SourceWidth}x{part.SourceHeight}, decoded={frame?.PixelWidth ?? 0}x{frame?.PixelHeight ?? 0}, " +
            $"frames={media?.Frames.Count ?? 0}, display={part.DisplayWidth:F1}x{part.DisplayHeight:F1}, " +
            $"path={renderPath}, decodeError={(media is null ? "decode-failed" : "none")}");
    }
#endif

    private IEnumerable<ChatMessagePartModel> RenderThirdPartyTextPart(string text, string channelKey)
    {
        return ThirdPartyEmoteTokenizer.Tokenize(
            text,
            code => _thirdPartyEmoteService.TryGetEmote(channelKey, code, out var emote) &&
                    (string.Equals(emote.Provider, "BTTV", StringComparison.OrdinalIgnoreCase)
                        ? Settings.EnableBttvEmotes
                        : !string.Equals(emote.Provider, "7TV", StringComparison.OrdinalIgnoreCase) || Settings.EnableSevenTvEmotes)
                ? emote
                : null);
    }

    private static void ApplyZeroWidthLayout(IEnumerable<ChatMessagePartModel> parts)
    {
        foreach (var part in parts)
        {
            part.OverlayPrevious = false;
        }
    }

    private async Task RefreshModerationAccessAsync(ChannelSessionViewModel session)
    {
        if (!session.ModerationCheckCompleted &&
            string.Equals(session.ModerationStatus, L("CheckingModeratorPermissions"), StringComparison.Ordinal))
        {
            return;
        }

        var currentUser = _currentUser;
        var broadcasterId = session.BroadcasterId;
        if (currentUser is null || string.IsNullOrWhiteSpace(broadcasterId))
        {
            session.CanModerate = false;
            session.IsBroadcaster = false;
            session.IsModerator = false;
            session.ModerationCheckCompleted = false;
            ClearRestrictedModerationData(session);
            return;
        }

        session.ModerationStatus = L("CheckingModeratorPermissions");
        session.ModerationCheckError = string.Empty;
        session.ModerationCheckCompleted = false;
        ChannelModerationAccess access;
        try
        {
            access = await _apiClient
                .GetModerationAccessAsync(broadcasterId, currentUser.Id, _disposeCts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            return;
        }

        if (!Channels.Contains(session) ||
            !string.Equals(_currentUser?.Id, currentUser.Id, StringComparison.Ordinal) ||
            !string.Equals(session.BroadcasterId, broadcasterId, StringComparison.Ordinal))
        {
            return;
        }

        ApplyModerationAccess(session, access);
    }

    private void ApplyModerationAccess(ChannelSessionViewModel session, ChannelModerationAccess access)
    {
        session.IsBroadcaster = access.IsBroadcaster;
        session.IsModerator = access.IsModerator;
        session.CanModerate = access.CanModerate;
        session.ModerationCheckCompleted = true;
        session.ModerationCheckError = access.FailureReason;
        session.ModerationStatus = GetLocalizedModerationStatus(session);

        if (session.HasConfirmedModerationAccess)
        {
            _moderationCacheService.RestoreSession(session);
        }
        else
        {
            ClearRestrictedModerationData(session);
        }

        if (session.HasConfirmedModerationAccess && _currentUser is not null && !string.IsNullOrWhiteSpace(session.BroadcasterId))
        {
            TrackBackgroundTask(_eventSubClient.TrySubscribeModerationAsync(
                session.BroadcasterId,
                _currentUser.Id,
                true,
                _disposeCts.Token));
        }

        if (ReferenceEquals(session, ActiveChannel))
        {
            RaiseStatePropertiesChanged();
            StartPinnedMessagePolling();
        }
    }

    private Task<TwitchUser?> GetCachedUserProfileAsync(string userId)
    {
        lock (_userProfileCacheGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_userProfileCache.TryGetValue(userId, out var cached) &&
                now - cached.FetchedAt < UserProfileCacheTtl)
            {
                _userProfileCache[userId] = cached with { LastAccessAt = now };
                return Task.FromResult<TwitchUser?>(cached.User);
            }

            _userProfileCache.Remove(userId);

            if (_userProfileRequests.TryGetValue(userId, out var pending))
            {
                return pending;
            }

            var request = LoadAndCacheUserProfileAsync(userId);
            _userProfileRequests[userId] = request;
            return request;
        }
    }

    private async Task<TwitchUser?> LoadAndCacheUserProfileAsync(string userId)
    {
        try
        {
            var user = await _apiClient.GetUserByIdAsync(userId, _disposeCts.Token).ConfigureAwait(false);
            if (user is not null)
            {
                lock (_userProfileCacheGate)
                {
                    var now = DateTimeOffset.UtcNow;
                    _userProfileCache[userId] = new CachedUserProfile(user, now, now);
                    TrimUserProfileCache(now);
                }
            }

            return user;
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warn($"User profile lookup skipped: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            lock (_userProfileCacheGate)
            {
                _userProfileRequests.Remove(userId);
            }
        }
    }

    private async Task RunModerationAsync(ChatMessageModel message, ModerationRequest request, bool permanentBan)
    {
        var targetSession = FindSessionForMessage(message);
        var moderator = _currentUser;
        if (moderator is null || targetSession is null || !CanModerateTarget(message))
        {
            return;
        }
        var targetBroadcasterId = targetSession.BroadcasterId;
        var targetUserId = message.UserId;
        var operationKey = targetBroadcasterId + "\n" + targetUserId + "\n" + (permanentBan ? "ban" : "timeout");
        if (!_moderationOperationsInProgress.Add(operationKey))
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (permanentBan)
            {
                await _moderationService.BanUserAsync(targetBroadcasterId, moderator.Id, targetUserId, request.Reason, _disposeCts.Token).ConfigureAwait(true);
                StatusText = string.Format(CultureInfo.CurrentCulture, L("UserBannedFormat"), message.UserLabel);
                MergePunishment(targetSession, new ActivePunishmentState
                {
                    UserId = targetUserId,
                    UserLogin = message.Login,
                    DisplayName = message.DisplayName,
                    Type = PunishmentType.Ban,
                    StartedAt = DateTimeOffset.UtcNow,
                    ModeratorId = moderator.Id,
                    ModeratorName = moderator.DisplayName,
                    Reason = request.Reason,
                    Source = PunishmentSource.LocalAction,
                    LastUpdatedAt = DateTimeOffset.UtcNow
                });
                UpsertLocalPunishment(targetSession, message, null, request.Reason);
                MarkMessagesModerated(targetSession, candidate => candidate.Kind == ChatMessageKind.Regular && candidate.UserId == targetUserId,
                    ModerationMessageState.Banned, moderator.Id, moderator.DisplayName, request.Reason);
            }
            else
            {
                var duration = request.DurationSeconds ?? 600;
                await _moderationService.TimeoutUserAsync(targetBroadcasterId, moderator.Id, targetUserId, duration, request.Reason, _disposeCts.Token).ConfigureAwait(true);
                StatusText = string.Format(
                    CultureInfo.CurrentCulture,
                    L("UserTimedOutFormat"),
                    message.UserLabel,
                    FormatTimeoutDuration(duration));
                MergePunishment(targetSession, new ActivePunishmentState
                {
                    UserId = targetUserId,
                    UserLogin = message.Login,
                    DisplayName = message.DisplayName,
                    Type = PunishmentType.Timeout,
                    StartedAt = DateTimeOffset.UtcNow,
                    EndsAt = DateTimeOffset.UtcNow.AddSeconds(duration),
                    DurationSeconds = duration,
                    ModeratorId = moderator.Id,
                    ModeratorName = moderator.DisplayName,
                    Reason = request.Reason,
                    Source = PunishmentSource.LocalAction,
                    LastUpdatedAt = DateTimeOffset.UtcNow
                });
                UpsertLocalPunishment(targetSession, message, DateTimeOffset.UtcNow.AddSeconds(duration), request.Reason);
                MarkMessagesModerated(targetSession, candidate => candidate.Kind == ChatMessageKind.Regular && candidate.UserId == targetUserId,
                    ModerationMessageState.TimedOut, moderator.Id, moderator.DisplayName, request.Reason);
            }
            _moderationCacheService.ScheduleSave(targetSession);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("Moderation action failed", ex);
            var errorMessage = UserFacingError(ex, "ModerationActionFailed");
            _dialogs.ShowError(L("Moderation"), errorMessage);
            StatusText = errorMessage;
        }
        finally
        {
            _moderationOperationsInProgress.Remove(operationKey);
            IsBusy = false;
        }
    }

    private bool CanModerateMessage(ChatMessageModel? message)
    {
        if (message is null)
        {
            return false;
        }

        if (!CanModerateTarget(message))
        {
            _dialogs.ShowError(L("Moderation"), L("SignInFirst"));
            return false;
        }

        if (!_authService.HasScope(AuthService.BannedUsersScope))
        {
            StatusText = L("ModerationSignInRequired");
            return false;
        }

        return true;
    }

    public bool CanBanOrTimeoutTarget(ChatMessageModel? message) =>
        CanModerateTarget(message) && _authService.HasScope(AuthService.BannedUsersScope);

    public bool CanModerateTarget(ChatMessageModel? message)
    {
        var channel = message is null ? null : FindSessionForMessage(message);
        return message is not null &&
               _currentUser is not null &&
               channel is { CanModerate: true, IsConnected: true } &&
               !string.IsNullOrWhiteSpace(message.UserId) &&
               string.Equals(message.ChannelLogin, channel.ChannelLogin, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(message.UserId, _currentUser.Id, StringComparison.Ordinal) &&
               !string.Equals(message.UserId, channel.BroadcasterId, StringComparison.Ordinal);
    }

    private ChannelSessionViewModel? FindSessionForMessage(ChatMessageModel message)
    {
        if (!string.IsNullOrWhiteSpace(message.BroadcasterId))
        {
            var byBroadcaster = Channels.FirstOrDefault(channel =>
                string.Equals(channel.BroadcasterId, message.BroadcasterId, StringComparison.Ordinal));
            if (byBroadcaster is not null)
            {
                return byBroadcaster;
            }
        }

        if (!string.IsNullOrWhiteSpace(message.RoomId))
        {
            var byRoom = Channels.FirstOrDefault(channel =>
                string.Equals(channel.BroadcasterId, message.RoomId, StringComparison.Ordinal));
            if (byRoom is not null)
            {
                return byRoom;
            }
        }

        return FindChannel(message.ChannelLogin);
    }

    private void OnEventSubMessageReceived(object? sender, ChatMessageModel message)
    {
        QueueIncomingMessage(message.ChannelLogin, primaryChannel: true, message);
    }

    private void OnSharedChatSessionChanged(object? sender, SharedChatSessionEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            var session = Channels.FirstOrDefault(channel =>
                string.Equals(channel.BroadcasterId, e.BroadcasterId, StringComparison.Ordinal));
            if (session is null)
            {
                return;
            }

            session.IsSharedChatActive = e.IsActive;
            session.SharedChatSessionId = e.IsActive ? e.SessionId : string.Empty;
            session.SharedChatHostLogin = e.IsActive ? e.HostBroadcasterLogin : string.Empty;
            session.SharedChatParticipantCount = e.IsActive ? e.Participants.Count : 0;
            if (ReferenceEquals(session, ActiveChannel))
            {
                OnPropertyChanged(nameof(IsSharedChatActive));
                OnPropertyChanged(nameof(SharedChatStatusText));
            }
        }, DispatcherPriority.DataBind);
    }

    private static void MarkMessagesModerated(
        ChannelSessionViewModel session,
        Func<ChatMessageModel, bool> predicate,
        ModerationMessageState state,
        string? moderatorId = null,
        string? moderatorName = null,
        string? reason = null)
    {
        foreach (var message in session.Messages.Where(predicate).ToArray())
        {
            message.MarkModerated(state, reason, moderatorId, moderatorName);
        }
        foreach (var message in session.PendingVisualMessages.Where(predicate).ToArray())
        {
            message.MarkModerated(state, reason, moderatorId, moderatorName);
        }
    }

    private void ClearRestrictedModerationData(ChannelSessionViewModel session)
    {
        session.PendingAutoModMessages.Clear();
        session.BannedUsers.Clear();
        session.UnbanRequests.Clear();
        session.ActivePunishments.Clear();
        session.BannedUsersCursor = string.Empty;
        session.UnbanRequestsCursor = string.Empty;
        session.BannedUsersLoadError = string.Empty;
        session.BannedUsersStatus = L("NoModeratorPermissions");
        session.UnbanRequestsStatus = L("NoModeratorPermissions");
        session.BannedUsersCapability = BannedUsersCapability.Unavailable;
        session.HasLoadedBannedUsers = false;
        session.HasCachedBannedUsers = false;
        session.IsBannedUsersDataStale = false;
        session.RequiresModerationReauthentication = false;
        session.UnreadUnbanRequests = 0;
    }

    private static void UpsertLocalPunishment(
        ChannelSessionViewModel session,
        ChatMessageModel message,
        DateTimeOffset? expiresAt,
        string reason)
    {
        var existing = session.BannedUsers.FirstOrDefault(item => string.Equals(item.UserId, message.UserId, StringComparison.Ordinal));
        if (existing is not null)
        {
            session.BannedUsers.Remove(existing);
        }
        session.BannedUsers.Insert(0, new BannedUserEntry
        {
            UserId = message.UserId,
            UserLogin = message.Login,
            DisplayName = message.DisplayName,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            Reason = reason
        });
        TrimBannedUsers(session);
    }

    public async Task ManageAutoModMessageAsync(HeldAutoModMessage? item, bool allow)
    {
        var targetSession = item is null
            ? null
            : Channels.FirstOrDefault(channel => string.Equals(channel.BroadcasterId, item.BroadcasterId, StringComparison.Ordinal));
        var moderator = _currentUser;
        if (item is null || targetSession is null || moderator is null || item.IsActionInProgress ||
            item.Status != HeldAutoModStatus.Pending || !_authService.HasScope(AuthService.AutoModScope))
        {
            return;
        }

        item.IsActionInProgress = true;
        item.ErrorMessage = string.Empty;
        try
        {
            await _moderationService.ManageHeldAutoModMessageAsync(
                moderator.Id,
                item.MessageId,
                allow,
                _disposeCts.Token).ConfigureAwait(true);
            item.Status = allow ? HeldAutoModStatus.Approved : HeldAutoModStatus.Denied;
            targetSession.PendingAutoModMessages.Remove(item);
            StatusText = L(allow ? "MessageAllowed" : "MessageDenied");
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            item.ErrorMessage = L("ModerationActionFailed");
            _logger.Warn($"AutoMod action failed: {ex.GetType().Name}");
        }
        finally
        {
            item.IsActionInProgress = false;
        }
    }

    public async Task RefreshBannedUsersAsync(ChannelSessionViewModel? session = null, bool loadMore = false)
    {
        var targetSession = session ?? ActiveChannel;
        var currentUser = _currentUser;
        if (targetSession is null || targetSession.IsBannedUsersLoading)
        {
            return;
        }

        if (currentUser is null || !targetSession.HasConfirmedModerationAccess)
        {
            ClearRestrictedModerationData(targetSession);
            return;
        }

        var targetBroadcasterId = targetSession.BroadcasterId;
        var currentUserId = currentUser.Id;
        var hasReadScope = _authService.HasScope(AuthService.BannedUsersScope) ||
                           _authService.HasScope("moderator:read:banned_users") ||
                           _authService.HasScope("moderation:read");
        if (string.IsNullOrWhiteSpace(targetBroadcasterId))
        {
            targetSession.BannedUsersLoadError = L("BannedUsersWrongBroadcaster");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var expired in targetSession.BannedUsers.Where(entry => entry.ExpiresAt is { } end && end <= now).ToArray())
        {
            targetSession.ActivePunishments.Remove(expired.UserId);
            targetSession.BannedUsers.Remove(expired);
        }
        RemoveExpiredPunishments(targetSession, now);

        if (!string.Equals(targetBroadcasterId, currentUserId, StringComparison.Ordinal))
        {
            TrimBannedUsers(targetSession);
            targetSession.BannedUsersCapability = BannedUsersCapability.ObservedOnly;
            targetSession.RequiresModerationReauthentication = false;
            targetSession.BannedUsersLoadError = string.Empty;
            targetSession.BannedUsersStatus = L("BannedUsersModeratorLimited");
            targetSession.HasCachedBannedUsers = targetSession.BannedUsers.Count > 0;
            return;
        }

        if (!hasReadScope)
        {
            targetSession.BannedUsersCapability = BannedUsersCapability.ReauthenticationRequired;
            targetSession.RequiresModerationReauthentication = true;
            targetSession.BannedUsersLoadError = L("BannedUsersMissingScope");
            return;
        }

        targetSession.IsBannedUsersLoading = true;
        targetSession.BannedUsersLoadError = string.Empty;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var page = await _moderationService.GetBannedUsersAsync(
                targetBroadcasterId,
                loadMore ? targetSession.BannedUsersCursor : null,
                _disposeCts.Token).ConfigureAwait(true);
            if (!Channels.Contains(targetSession) ||
                !string.Equals(targetSession.BroadcasterId, targetBroadcasterId, StringComparison.Ordinal) ||
                !string.Equals(_currentUser?.Id, currentUserId, StringComparison.Ordinal))
            {
                return;
            }
            if (!loadMore)
            {
                var returnedIds = page.Users.Select(user => user.UserId).ToHashSet(StringComparer.Ordinal);
                foreach (var stale in targetSession.BannedUsers.Where(entry => !returnedIds.Contains(entry.UserId)).ToArray())
                {
                    targetSession.BannedUsers.Remove(stale);
                    targetSession.ActivePunishments.Remove(stale.UserId);
                }
            }
            foreach (var user in page.Users)
            {
                var existing = targetSession.BannedUsers.FirstOrDefault(entry => string.Equals(entry.UserId, user.UserId, StringComparison.Ordinal));
                if (existing is null)
                {
                    targetSession.BannedUsers.Add(user);
                }
                else if (existing.ExpiresAt != user.ExpiresAt || !string.Equals(existing.Reason, user.Reason, StringComparison.Ordinal))
                {
                    var index = targetSession.BannedUsers.IndexOf(existing);
                    targetSession.BannedUsers[index] = user;
                }
                MergePunishment(targetSession, new ActivePunishmentState
                {
                    UserId = user.UserId,
                    UserLogin = user.UserLogin,
                    DisplayName = user.DisplayName,
                    Type = user.IsPermanent ? PunishmentType.Ban : PunishmentType.Timeout,
                    StartedAt = user.CreatedAt,
                    EndsAt = user.ExpiresAt,
                    DurationSeconds = user.ExpiresAt is { } end
                        ? Math.Max(1, (int)Math.Min(int.MaxValue, (end - user.CreatedAt).TotalSeconds))
                        : null,
                    Reason = user.Reason,
                    Source = PunishmentSource.EventSub,
                    LastUpdatedAt = DateTimeOffset.UtcNow
                });
            }
            TrimBannedUsers(targetSession);
            targetSession.BannedUsersCursor = page.Cursor;
            targetSession.HasLoadedBannedUsers = true;
            targetSession.HasCachedBannedUsers = targetSession.BannedUsers.Count > 0;
            targetSession.IsBannedUsersDataStale = false;
            targetSession.RequiresModerationReauthentication = false;
            targetSession.BannedUsersCapability = BannedUsersCapability.Authoritative;
            targetSession.LastBannedUsersRefreshAt = DateTimeOffset.UtcNow;
            targetSession.BannedUsersStatus = targetSession.BannedUsers.Count == 0 ? L("NoBannedUsers") : string.Empty;
            _moderationCacheService.ScheduleSave(targetSession);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException ex)
        {
            targetSession.BannedUsersLoadError = L("TwitchNetworkError");
            targetSession.IsBannedUsersDataStale = targetSession.BannedUsers.Count > 0;
            targetSession.BannedUsersCapability = BannedUsersCapability.ObservedOnly;
            _logger.Warn($"Get Banned Users timed out: channel={targetSession.ChannelLogin}, broadcaster_id={targetBroadcasterId}, elapsed_ms={stopwatch.ElapsedMilliseconds}, error={ex.GetType().Name}");
        }
        catch (TwitchApiException ex)
        {
            ApplyBannedUsersError(targetSession, ex);
            _logger.Warn($"Get Banned Users failed: endpoint={ex.EndpointName}, status={(int)ex.StatusCode}, error={ex.TwitchError}, message={ex.TwitchMessage}, channel={targetSession.ChannelLogin}, broadcaster_id={targetBroadcasterId}, current_user_id={currentUserId}, ids_match={string.Equals(targetBroadcasterId, currentUserId, StringComparison.Ordinal)}, has_scope={hasReadScope}, elapsed_ms={stopwatch.ElapsedMilliseconds}");
        }
        catch (HttpRequestException ex)
        {
            targetSession.BannedUsersLoadError = L("TwitchNetworkError");
            targetSession.IsBannedUsersDataStale = targetSession.BannedUsers.Count > 0;
            targetSession.BannedUsersCapability = BannedUsersCapability.ObservedOnly;
            _logger.Warn($"Get Banned Users network failure: channel={targetSession.ChannelLogin}, broadcaster_id={targetBroadcasterId}, elapsed_ms={stopwatch.ElapsedMilliseconds}, error={ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            targetSession.BannedUsersLoadError = L("BannedUsersRefreshFailed");
            targetSession.IsBannedUsersDataStale = targetSession.BannedUsers.Count > 0;
            _logger.Warn($"Get Banned Users processing failed: channel={targetSession.ChannelLogin}, broadcaster_id={targetBroadcasterId}, elapsed_ms={stopwatch.ElapsedMilliseconds}, error={ex.GetType().Name}");
        }
        finally
        {
            targetSession.IsBannedUsersLoading = false;
        }
    }

    private void ApplyBannedUsersError(ChannelSessionViewModel session, TwitchApiException exception)
    {
        session.IsBannedUsersDataStale = session.BannedUsers.Count > 0;
        if (exception.IsMissingScope)
        {
            session.BannedUsersCapability = BannedUsersCapability.ReauthenticationRequired;
            session.RequiresModerationReauthentication = true;
            session.BannedUsersLoadError = L("BannedUsersMissingScope");
        }
        else if (exception.IsExpiredToken)
        {
            session.BannedUsersCapability = BannedUsersCapability.ReauthenticationRequired;
            session.RequiresModerationReauthentication = true;
            session.BannedUsersLoadError = L("TwitchSessionExpired");
        }
        else if (exception.IsPermissionDenied)
        {
            session.BannedUsersCapability = BannedUsersCapability.ObservedOnly;
            session.BannedUsersLoadError = L("BannedUsersForbidden");
        }
        else if ((int)exception.StatusCode == 429)
        {
            session.BannedUsersLoadError = L("TwitchRateLimited");
        }
        else
        {
            session.BannedUsersLoadError = L("BannedUsersRefreshFailed");
        }
        session.BannedUsersStatus = session.IsBannedUsersDataStale ? L("BannedUsersStale") : string.Empty;
    }

    private async Task RefreshModerationStateInBackgroundAsync(ChannelSessionViewModel session)
    {
        try
        {
            await RefreshBannedUsersAsync(session).ConfigureAwait(true);
            if (_authService.HasScope(AuthService.UnbanRequestsScope))
            {
                await RefreshUnbanRequestsAsync(session).ConfigureAwait(true);
            }
            else
            {
                session.UnbanRequestsStatus = L("UnbanRequestsSignInAgain");
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Background moderation refresh failed: channel={session.ChannelLogin}, error={ex.GetType().Name}");
        }
    }

    public async Task RefreshUnbanRequestsAsync(
        ChannelSessionViewModel? session = null,
        UnbanRequestStatus status = UnbanRequestStatus.Pending,
        bool loadMore = false)
    {
        var targetSession = session ?? ActiveChannel;
        var currentUser = _currentUser;
        if (targetSession is null || targetSession.IsUnbanRequestsLoading ||
            string.IsNullOrWhiteSpace(targetSession.BroadcasterId))
        {
            return;
        }

        if (currentUser is null || !targetSession.HasConfirmedModerationAccess)
        {
            ClearRestrictedModerationData(targetSession);
            return;
        }

        if (!_authService.HasScope(AuthService.UnbanRequestsScope))
        {
            targetSession.UnbanRequestsStatus = L("UnbanRequestsSignInAgain");
            return;
        }

        var targetBroadcasterId = targetSession.BroadcasterId;
        var moderatorId = currentUser.Id;
        targetSession.IsUnbanRequestsLoading = true;
        targetSession.UnbanRequestsStatus = string.Empty;
        targetSession.UnbanRequestFilter = status;
        try
        {
            var page = await _moderationService.GetUnbanRequestsAsync(
                targetBroadcasterId,
                moderatorId,
                status,
                loadMore ? targetSession.UnbanRequestsCursor : null,
                _disposeCts.Token).ConfigureAwait(true);
            if (!Channels.Contains(targetSession) ||
                !string.Equals(targetSession.BroadcasterId, targetBroadcasterId, StringComparison.Ordinal) ||
                !string.Equals(_currentUser?.Id, moderatorId, StringComparison.Ordinal))
            {
                return;
            }

            if (!loadMore)
            {
                var returnedIds = page.Requests.Select(item => item.RequestId).ToHashSet(StringComparer.Ordinal);
                foreach (var stale in targetSession.UnbanRequests
                             .Where(item => item.Status == status && !returnedIds.Contains(item.RequestId))
                             .ToArray())
                {
                    targetSession.UnbanRequests.Remove(stale);
                }
            }
            foreach (var request in page.Requests)
            {
                UpsertUnbanRequest(targetSession, request);
                TrackBackgroundTask(HydrateUnbanRequestProfileAsync(request));
            }
            targetSession.UnbanRequestsCursor = page.Cursor;
            targetSession.UnbanRequestsStatus = page.Requests.Count == 0 && !loadMore ? L("NoUnbanRequests") : string.Empty;
            if (status == UnbanRequestStatus.Pending)
            {
                targetSession.UnreadUnbanRequests = 0;
            }
            _moderationCacheService.ScheduleSave(targetSession);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException ex)
        {
            targetSession.UnbanRequestsStatus = L("TwitchNetworkError");
            _logger.Warn($"Get Unban Requests timed out: broadcaster_id={targetBroadcasterId}, error={ex.GetType().Name}");
        }
        catch (TwitchApiException ex)
        {
            targetSession.UnbanRequestsStatus = ex.IsMissingScope || ex.IsExpiredToken
                ? L("UnbanRequestsSignInAgain")
                : ex.IsPermissionDenied
                    ? L("UnbanRequestsForbidden")
                    : ex.StatusCode == System.Net.HttpStatusCode.BadRequest
                        ? L("ChannelNotAcceptingUnbanRequests")
                        : (int)ex.StatusCode == 429 ? L("TwitchRateLimited") : L("BannedUsersRefreshFailed");
            _logger.Warn($"Get Unban Requests failed: status={(int)ex.StatusCode}, error={ex.TwitchError}, message={ex.TwitchMessage}, broadcaster_id={targetBroadcasterId}, moderator_id={moderatorId}");
        }
        catch (HttpRequestException ex)
        {
            targetSession.UnbanRequestsStatus = L("TwitchNetworkError");
            _logger.Warn($"Get Unban Requests network failure: broadcaster_id={targetBroadcasterId}, error={ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            targetSession.UnbanRequestsStatus = L("BannedUsersRefreshFailed");
            _logger.Warn($"Get Unban Requests failed: broadcaster_id={targetBroadcasterId}, error={ex.GetType().Name}");
        }
        finally
        {
            targetSession.IsUnbanRequestsLoading = false;
        }
    }

    public async Task ResolveUnbanRequestAsync(UnbanRequestEntry? request, bool approve)
    {
        var targetSession = request is null
            ? null
            : Channels.FirstOrDefault(session => string.Equals(session.BroadcasterId, request.BroadcasterId, StringComparison.Ordinal));
        var moderator = _currentUser;
        if (request is null || targetSession is null || moderator is null || request.IsActionInProgress ||
            !targetSession.HasConfirmedModerationAccess)
        {
            return;
        }
        if (!_authService.HasScope(AuthService.UnbanRequestsScope))
        {
            targetSession.UnbanRequestsStatus = L("UnbanRequestsSignInAgain");
            return;
        }

        var resolution = _dialogs.ShowUnbanRequestResolutionDialog(request, approve);
        if (resolution is null)
        {
            return;
        }

        request.IsActionInProgress = true;
        request.ErrorMessage = string.Empty;
        try
        {
            var resolved = await _moderationService.ResolveUnbanRequestAsync(
                targetSession.BroadcasterId,
                moderator.Id,
                request.RequestId,
                approve ? UnbanRequestStatus.Approved : UnbanRequestStatus.Denied,
                resolution.ResolutionText,
                _disposeCts.Token).ConfigureAwait(true);
            UpsertUnbanRequest(targetSession, resolved);
            if (approve)
            {
                RemoveUserPunishmentState(targetSession, request.UserId, request.UserLogin);
            }
            StatusText = L(approve ? "UnbanRequestApproved" : "UnbanRequestDenied");
            _moderationCacheService.ScheduleSave(targetSession);
        }
        catch (TwitchApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict)
        {
            await RefreshUnbanRequestsAsync(targetSession, request.Status).ConfigureAwait(true);
            request.ErrorMessage = ex.TwitchMessage;
        }
        catch (TwitchApiException ex)
        {
            request.ErrorMessage = ex.IsMissingScope || ex.IsExpiredToken
                ? L("UnbanRequestsSignInAgain")
                : ex.IsPermissionDenied ? L("UnbanRequestsForbidden") : L("ModerationActionFailed");
            _logger.Warn($"Resolve Unban Request failed: status={(int)ex.StatusCode}, error={ex.TwitchError}, message={ex.TwitchMessage}, broadcaster_id={targetSession.BroadcasterId}");
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException ex)
        {
            request.ErrorMessage = L("TwitchNetworkError");
            _logger.Warn($"Resolve Unban Request timed out: broadcaster_id={targetSession.BroadcasterId}, error={ex.GetType().Name}");
        }
        catch (HttpRequestException ex)
        {
            request.ErrorMessage = L("TwitchNetworkError");
            _logger.Warn($"Resolve Unban Request network failure: broadcaster_id={targetSession.BroadcasterId}, error={ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            request.ErrorMessage = L("ModerationActionFailed");
            _logger.Warn($"Resolve Unban Request failed: broadcaster_id={targetSession.BroadcasterId}, error={ex.GetType().Name}");
        }
        finally
        {
            request.IsActionInProgress = false;
        }
    }

    private static void UpsertUnbanRequest(ChannelSessionViewModel session, UnbanRequestEntry request)
    {
        var existing = session.UnbanRequests.FirstOrDefault(item => string.Equals(item.RequestId, request.RequestId, StringComparison.Ordinal));
        if (existing is null)
        {
            session.UnbanRequests.Insert(0, request);
            TrimUnbanRequests(session);
            return;
        }
        if (string.IsNullOrWhiteSpace(request.ProfileImageUrl))
        {
            request.ProfileImageUrl = existing.ProfileImageUrl;
        }
        session.UnbanRequests[session.UnbanRequests.IndexOf(existing)] = request;
    }

    private static void TrimUnbanRequests(ChannelSessionViewModel session)
    {
        while (session.UnbanRequests.Count > MaxUnbanRequestsPerChannel)
        {
            var removalIndex = -1;
            for (var index = session.UnbanRequests.Count - 1; index >= 0; index--)
            {
                var candidate = session.UnbanRequests[index];
                if (candidate.Status != UnbanRequestStatus.Pending && !candidate.IsActionInProgress)
                {
                    removalIndex = index;
                    break;
                }
            }

            session.UnbanRequests.RemoveAt(removalIndex >= 0 ? removalIndex : session.UnbanRequests.Count - 1);
        }
    }

    private async Task HydrateUnbanRequestProfileAsync(UnbanRequestEntry request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || !string.IsNullOrWhiteSpace(request.ProfileImageUrl))
        {
            return;
        }
        var profile = await GetCachedUserProfileAsync(request.UserId).ConfigureAwait(true);
        if (profile is not null)
        {
            request.ProfileImageUrl = profile.ProfileImageUrl;
        }
    }

    private static void RemoveUserPunishmentState(ChannelSessionViewModel session, string userId, string userLogin)
    {
        foreach (var key in session.ActivePunishments
                     .Where(pair => string.Equals(pair.Value.UserId, userId, StringComparison.Ordinal) ||
                                    (!string.IsNullOrWhiteSpace(userLogin) && string.Equals(pair.Value.UserLogin, userLogin, StringComparison.OrdinalIgnoreCase)))
                     .Select(pair => pair.Key).ToArray())
        {
            session.ActivePunishments.Remove(key);
        }
        foreach (var item in session.BannedUsers
                     .Where(item => string.Equals(item.UserId, userId, StringComparison.Ordinal) ||
                                    (!string.IsNullOrWhiteSpace(userLogin) && string.Equals(item.UserLogin, userLogin, StringComparison.OrdinalIgnoreCase)))
                     .ToArray())
        {
            session.BannedUsers.Remove(item);
        }
    }

    public async Task RemovePunishmentAsync(BannedUserEntry? entry, ChannelSessionViewModel? session = null)
    {
        var targetSession = session ?? ActiveChannel;
        var moderator = _currentUser;
        if (entry is null || targetSession is null || moderator is null || entry.IsRemoving ||
            !targetSession.HasConfirmedModerationAccess)
        {
            return;
        }

        entry.IsRemoving = true;
        entry.ErrorMessage = string.Empty;
        try
        {
            await RemovePunishmentFromTwitchAsync(targetSession.BroadcasterId, moderator.Id, entry.UserId).ConfigureAwait(true);
            targetSession.ActivePunishments.Remove(entry.UserId);
            targetSession.BannedUsers.Remove(entry);
            _moderationCacheService.ScheduleSave(targetSession);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            entry.ErrorMessage = L("ModerationActionFailed");
            _logger.Warn($"Remove punishment failed: {ex.GetType().Name}");
        }
        finally
        {
            entry.IsRemoving = false;
        }
    }

    public async Task UnbanByLoginAsync(string? login, ChannelSessionViewModel? session = null)
    {
        var normalizedLogin = (login ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();
        var targetSession = session ?? ActiveChannel;
        if (targetSession is null || _currentUser is null || !targetSession.HasConfirmedModerationAccess ||
            normalizedLogin.Length is < 1 or > 25 ||
            normalizedLogin.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
        {
            return;
        }

        try
        {
            var target = await _apiClient.GetUserByLoginAsync(normalizedLogin, _disposeCts.Token).ConfigureAwait(true);
            if (target is null)
            {
                StatusText = L("ModerationActionFailed");
                return;
            }
            await RemovePunishmentFromTwitchAsync(targetSession.BroadcasterId, _currentUser.Id, target.Id).ConfigureAwait(true);
            targetSession.ActivePunishments.Remove(target.Id);
            var existing = targetSession.BannedUsers.FirstOrDefault(item => string.Equals(item.UserId, target.Id, StringComparison.Ordinal));
            if (existing is not null)
            {
                targetSession.BannedUsers.Remove(existing);
            }
            _moderationCacheService.ScheduleSave(targetSession);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText = L("ModerationActionFailed");
            _logger.Warn($"Unban by login failed: {ex.GetType().Name}");
        }
    }

    public ActivePunishmentState? GetActivePunishment(ChatMessageModel? message)
    {
        var session = message is null ? null : FindChannel(message.ChannelLogin);
        if (session is null)
        {
            return null;
        }
        RemoveExpiredPunishments(session, DateTimeOffset.UtcNow);
        var key = GetPunishmentKey(message!.UserId, message.Login);
        return string.IsNullOrWhiteSpace(key) || !session.ActivePunishments.TryGetValue(key, out var state) ? null : state;
    }

    public bool CanRemovePunishment(ChatMessageModel? message)
    {
        var state = GetActivePunishment(message);
        var session = message is null ? null : FindChannel(message.ChannelLogin);
        var operationKey = session is null || state is null ? string.Empty : session.BroadcasterId + "\n" + state.UserId;
        return state is not null && message is not null && CanModerateTarget(message) &&
               _authService.HasScope("moderator:manage:banned_users") &&
               !string.IsNullOrWhiteSpace(state.UserId) &&
               !_punishmentRemovalInProgress.Contains(operationKey);
    }

    public async Task RemovePunishmentAsync(ChatMessageModel? message)
    {
        var targetSession = message is null ? null : FindChannel(message.ChannelLogin);
        var moderator = _currentUser;
        var state = GetActivePunishment(message);
        if (targetSession is null || moderator is null || message is null || state is null ||
            !CanRemovePunishment(message) || string.IsNullOrWhiteSpace(state.UserId))
        {
            return;
        }

        var targetBroadcasterId = targetSession.BroadcasterId;
        var targetUserId = state.UserId;
        var operationKey = targetBroadcasterId + "\n" + targetUserId;
        if (!_punishmentRemovalInProgress.Add(operationKey))
        {
            return;
        }
        try
        {
            await RemovePunishmentFromTwitchAsync(targetBroadcasterId, moderator.Id, targetUserId).ConfigureAwait(true);
            targetSession.ActivePunishments.Remove(GetPunishmentKey(targetUserId, state.UserLogin));
            var listed = targetSession.BannedUsers.FirstOrDefault(entry => string.Equals(entry.UserId, targetUserId, StringComparison.Ordinal));
            if (listed is not null)
            {
                targetSession.BannedUsers.Remove(listed);
            }
            AddMessage(targetSession, new ChatMessageModel
            {
                Id = "moderation-" + Guid.NewGuid().ToString("N"),
                ChannelLogin = targetSession.ChannelLogin,
                BroadcasterId = targetSession.BroadcasterId,
                Kind = ChatMessageKind.ModerationAction,
                DisplayName = L("Moderation"),
                Text = string.Format(CultureInfo.CurrentCulture, L("PunishmentRemovedEvent"), moderator.DisplayName, message.UserLabel),
                Parts = [ChatMessagePartModel.TextPart(string.Format(CultureInfo.CurrentCulture, L("PunishmentRemovedEvent"), moderator.DisplayName, message.UserLabel))]
            });
            StatusText = L("PunishmentRemoved");
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText = L("ModerationActionFailed");
            _logger.Warn($"Remove punishment from message failed: {ex.GetType().Name}");
        }
        finally
        {
            _punishmentRemovalInProgress.Remove(operationKey);
        }
    }

    private async Task RemovePunishmentFromTwitchAsync(string broadcasterId, string moderatorId, string targetUserId)
    {
        try
        {
            await _moderationService.UnbanOrUntimeoutAsync(
                broadcasterId,
                moderatorId,
                targetUserId,
                _disposeCts.Token).ConfigureAwait(true);
        }
        catch (TwitchApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Twitch reports an already-cleared punishment as a bad request. The
            // authoritative remote state is unpunished, so stale local state is removed.
        }
        catch (TwitchApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            await Task.Delay(100, _disposeCts.Token).ConfigureAwait(true);
            await _moderationService.UnbanOrUntimeoutAsync(
                broadcasterId,
                moderatorId,
                targetUserId,
                _disposeCts.Token).ConfigureAwait(true);
        }
    }

    private void OnIrcUserModerated(object? sender, ChannelUserModeratedEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            var session = FindSessionForIrcModeration(e.RoomId, e.ChannelLogin);
            if (session is null)
            {
                return;
            }

            var matchingMessage = session.Messages.LastOrDefault(message =>
                (!string.IsNullOrWhiteSpace(e.TargetUserId) && string.Equals(message.UserId, e.TargetUserId, StringComparison.Ordinal)) ||
                (string.IsNullOrWhiteSpace(e.TargetUserId) && !string.IsNullOrWhiteSpace(e.TargetLogin) &&
                 string.Equals(message.Login, e.TargetLogin, StringComparison.OrdinalIgnoreCase)));
            var state = new ActivePunishmentState
            {
                UserId = e.TargetUserId,
                UserLogin = e.TargetLogin,
                DisplayName = matchingMessage?.DisplayName ?? string.Empty,
                Type = e.PunishmentType,
                StartedAt = e.ObservedAt,
                EndsAt = e.DurationSeconds is { } seconds ? e.ObservedAt.AddSeconds(seconds) : null,
                DurationSeconds = e.DurationSeconds,
                Source = PunishmentSource.IrcClearChat,
                LastUpdatedAt = e.ObservedAt
            };
            var visualState = ToModerationState(e.PunishmentType);
            if (IsCurrentAccount(e.TargetUserId, e.TargetLogin))
            {
                SetSendRestriction(session, e.PunishmentType, state.EndsAt);
            }
            MarkMessagesModerated(
                session,
                message => !string.IsNullOrWhiteSpace(e.TargetUserId)
                    ? string.Equals(message.UserId, e.TargetUserId, StringComparison.Ordinal)
                    : string.Equals(message.Login, e.TargetLogin, StringComparison.OrdinalIgnoreCase),
                visualState);
            if (session.HasConfirmedModerationAccess)
            {
                MergePunishment(session, state);
                UpsertObservedPunishment(session, state);
                _moderationCacheService.ScheduleSave(session);
            }
        });
    }

    private void OnIrcMessageDeleted(object? sender, ChannelMessageDeletedEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            var session = FindSessionForIrcModeration(e.RoomId, e.ChannelLogin);
            if (session is not null && TryObserveModerationEvent(session.BroadcasterId + "\n" + e.TargetMessageId + "\nDelete"))
            {
                ApplyMessageDeletion(session, e.RoomId, e.TargetMessageId);
            }
        });
    }

    private void OnIrcChatCleared(object? sender, ChannelChatClearedEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            var session = FindSessionForIrcModeration(e.RoomId, e.ChannelLogin);
            if (session is not null)
            {
                MarkMessagesModerated(session, message => message.Kind == ChatMessageKind.Regular, ModerationMessageState.RemovedByModeration);
            }
        });
    }

    private ChannelSessionViewModel? FindSessionForIrcModeration(string roomId, string channelLogin)
    {
        if (!string.IsNullOrWhiteSpace(roomId))
        {
            var byRoom = Channels.FirstOrDefault(channel => string.Equals(channel.BroadcasterId, roomId, StringComparison.Ordinal));
            if (byRoom is not null)
            {
                return byRoom;
            }

            var unresolvedByLogin = FindChannel(channelLogin);
            if (unresolvedByLogin is not null && string.IsNullOrWhiteSpace(unresolvedByLogin.BroadcasterId))
            {
                unresolvedByLogin.BroadcasterId = roomId;
                return unresolvedByLogin;
            }
            _logger.Warn($"IRC moderation room mismatch: room_id={roomId}, channel={channelLogin}");
            return null;
        }
        return FindChannel(channelLogin);
    }

    private bool IsCurrentAccount(string? userId, string? login) =>
        _currentUser is not null &&
        ((!string.IsNullOrWhiteSpace(userId) && string.Equals(_currentUser.Id, userId, StringComparison.Ordinal)) ||
         (!string.IsNullOrWhiteSpace(login) && string.Equals(_currentUser.Login, login, StringComparison.OrdinalIgnoreCase)));

    private void SetSendRestriction(ChannelSessionViewModel session, PunishmentType type, DateTimeOffset? endsAt)
    {
        if (type is not (PunishmentType.Ban or PunishmentType.Timeout))
        {
            return;
        }

        var generation = ++session.SendRestrictionGeneration;
        session.SendRestrictionType = type;
        session.SendRestrictionText = L(type == PunishmentType.Ban ? "SendMessageBanned" : "SendMessageTimedOut");
        session.SendRestrictionEndsAt = endsAt;
        session.HasSendRestriction = true;
        NotifySendRestrictionChanged(session);

        if (endsAt is { } expiration && expiration > DateTimeOffset.UtcNow)
        {
            TrackBackgroundTask(ClearSendRestrictionAfterDelayAsync(session, generation, expiration));
        }
    }

    private async Task ClearSendRestrictionAfterDelayAsync(
        ChannelSessionViewModel session,
        int generation,
        DateTimeOffset expiration)
    {
        try
        {
            var delay = expiration - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _disposeCts.Token).ConfigureAwait(false);
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            await dispatcher.InvokeAsync(() =>
            {
                if (session.SendRestrictionGeneration == generation)
                {
                    ClearSendRestriction(session);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ClearSendRestriction(ChannelSessionViewModel session)
    {
        session.SendRestrictionGeneration++;
        session.HasSendRestriction = false;
        session.SendRestrictionType = null;
        session.SendRestrictionText = string.Empty;
        session.SendRestrictionEndsAt = null;
        NotifySendRestrictionChanged(session);
    }

    private void NotifySendRestrictionChanged(ChannelSessionViewModel session)
    {
        if (!ReferenceEquals(ActiveChannel, session))
        {
            return;
        }

        OnPropertyChanged(nameof(HasSendRestriction));
        OnPropertyChanged(nameof(SendRestrictionText));
        OnPropertyChanged(nameof(CanSendMessages));
        OnPropertyChanged(nameof(SendButtonToolTip));
        RaiseCommandState();
    }

    private static string GetPunishmentKey(string? userId, string? login) =>
        !string.IsNullOrWhiteSpace(userId) ? userId : string.IsNullOrWhiteSpace(login) ? string.Empty : "login:" + login.Trim().ToLowerInvariant();

    private static ModerationMessageState ToModerationState(PunishmentType type) => type switch
    {
        PunishmentType.Ban => ModerationMessageState.Banned,
        PunishmentType.Timeout => ModerationMessageState.TimedOut,
        _ => ModerationMessageState.RemovedByModeration
    };

    private static void MergePunishment(ChannelSessionViewModel session, ActivePunishmentState incoming)
    {
        var key = GetPunishmentKey(incoming.UserId, incoming.UserLogin);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        var loginKey = string.IsNullOrWhiteSpace(incoming.UserLogin)
            ? string.Empty
            : "login:" + incoming.UserLogin.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(incoming.UserId) && !string.IsNullOrWhiteSpace(loginKey) &&
            session.ActivePunishments.Remove(loginKey, out var byLogin) &&
            !session.ActivePunishments.ContainsKey(key))
        {
            session.ActivePunishments[key] = byLogin;
        }
        else if (string.IsNullOrWhiteSpace(incoming.UserId) && !string.IsNullOrWhiteSpace(incoming.UserLogin))
        {
            var byKnownLogin = session.ActivePunishments.FirstOrDefault(pair =>
                string.Equals(pair.Value.UserLogin, incoming.UserLogin, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(byKnownLogin.Key))
            {
                key = byKnownLogin.Key;
            }
        }
        if (session.ActivePunishments.TryGetValue(key, out var existing))
        {
            existing.Type = incoming.Type == PunishmentType.Unknown ? existing.Type : incoming.Type;
            existing.UserLogin = string.IsNullOrWhiteSpace(incoming.UserLogin) ? existing.UserLogin : incoming.UserLogin;
            existing.DisplayName = string.IsNullOrWhiteSpace(incoming.DisplayName) ? existing.DisplayName : incoming.DisplayName;
            existing.StartedAt = incoming.StartedAt;
            existing.EndsAt = incoming.EndsAt;
            existing.DurationSeconds = incoming.DurationSeconds;
            existing.ModeratorId = string.IsNullOrWhiteSpace(incoming.ModeratorId) ? existing.ModeratorId : incoming.ModeratorId;
            existing.ModeratorName = string.IsNullOrWhiteSpace(incoming.ModeratorName) ? existing.ModeratorName : incoming.ModeratorName;
            existing.Reason = string.IsNullOrWhiteSpace(incoming.Reason) ? existing.Reason : incoming.Reason;
            existing.Source = incoming.Source > existing.Source ? incoming.Source : existing.Source;
            existing.LastUpdatedAt = incoming.LastUpdatedAt;
        }
        else
        {
            session.ActivePunishments[key] = incoming;
        }

        while (session.ActivePunishments.Count > 1000)
        {
            var oldest = session.ActivePunishments.MinBy(pair => pair.Value.LastUpdatedAt).Key;
            session.ActivePunishments.Remove(oldest);
        }
    }

    private static void RemoveExpiredPunishments(ChannelSessionViewModel session, DateTimeOffset now)
    {
        foreach (var expired in session.ActivePunishments
                     .Where(pair => pair.Value.Type == PunishmentType.Timeout && pair.Value.EndsAt is { } end && end <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            session.ActivePunishments.Remove(expired);
        }
    }

    private static void UpsertObservedPunishment(ChannelSessionViewModel session, ActivePunishmentState state)
    {
        if (string.IsNullOrWhiteSpace(state.UserId))
        {
            return;
        }
        var existing = session.BannedUsers.FirstOrDefault(entry => string.Equals(entry.UserId, state.UserId, StringComparison.Ordinal));
        if (existing is not null)
        {
            session.BannedUsers.Remove(existing);
        }
        session.BannedUsers.Insert(0, new BannedUserEntry
        {
            UserId = state.UserId,
            UserLogin = state.UserLogin,
            DisplayName = state.DisplayName,
            CreatedAt = state.StartedAt,
            ExpiresAt = state.EndsAt,
            Reason = state.Reason
        });
        TrimBannedUsers(session);
    }

    private static void TrimBannedUsers(ChannelSessionViewModel session)
    {
        while (session.BannedUsers.Count > MaxBannedUsersPerChannel)
        {
            session.BannedUsers.RemoveAt(session.BannedUsers.Count - 1);
        }
    }

    private void OnChatMessageDeleted(object? sender, ChatMessageDeletedEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            var session = Channels.FirstOrDefault(channel => string.Equals(channel.BroadcasterId, e.BroadcasterId, StringComparison.Ordinal));
            if (session is not null && TryObserveModerationEvent(e.BroadcasterId + "\n" + e.MessageId + "\nDelete"))
            {
                ApplyMessageDeletion(session, e.BroadcasterId, e.MessageId);
            }
        });
    }

    private void ApplyMessageDeletion(ChannelSessionViewModel session, string broadcasterId, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        var effectiveBroadcasterId = string.IsNullOrWhiteSpace(broadcasterId)
            ? session.BroadcasterId
            : broadcasterId;
        var correlationKey = CreateMessageCorrelationKey(effectiveBroadcasterId, messageId);
        RememberPendingMessageDeletion(correlationKey);

        static bool Matches(ChatMessageModel message, string targetId) =>
            string.Equals(message.MessageId, targetId, StringComparison.Ordinal) ||
            string.Equals(message.Id, targetId, StringComparison.Ordinal);

        MarkMessagesModerated(
            session,
            message => Matches(message, messageId),
            ModerationMessageState.Deleted);

        foreach (var pending in _pendingChatMessages
                     .Where(item => string.Equals(item.ChannelLogin, session.ChannelLogin, StringComparison.OrdinalIgnoreCase) &&
                                    Matches(item.Message, messageId)))
        {
            pending.Message.MarkModerated(ModerationMessageState.Deleted);
        }

        if (!string.IsNullOrWhiteSpace(correlationKey) &&
            _liveMessageIndex.TryGetValue(correlationKey, out var indexedMessage))
        {
            indexedMessage.MarkModerated(ModerationMessageState.Deleted);
        }
    }

    private void RememberPendingMessageDeletion(string correlationKey)
    {
        if (string.IsNullOrWhiteSpace(correlationKey) || !_pendingDeletedMessageKeys.Add(correlationKey))
        {
            return;
        }

        _pendingDeletedMessageOrder.Enqueue(correlationKey);
        while (_pendingDeletedMessageOrder.Count > PendingMetadataLimit)
        {
            _pendingDeletedMessageKeys.Remove(_pendingDeletedMessageOrder.Dequeue());
        }
    }

    private bool TryObserveModerationEvent(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !_seenModerationEventKeys.Add(key))
        {
            return false;
        }

        _seenModerationEventOrder.Enqueue(key);
        while (_seenModerationEventOrder.Count > 1500)
        {
            _seenModerationEventKeys.Remove(_seenModerationEventOrder.Dequeue());
        }
        return true;
    }

    private void OnUserMessagesCleared(object? sender, UserMessagesClearedEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            var session = Channels.FirstOrDefault(channel => string.Equals(channel.BroadcasterId, e.BroadcasterId, StringComparison.Ordinal));
            if (session is null)
            {
                return;
            }
            var state = ModerationMessageState.RemovedByModeration;
            if (session.HasConfirmedModerationAccess)
            {
                MergePunishment(session, new ActivePunishmentState
                {
                    UserId = e.TargetUserId,
                    Type = PunishmentType.Unknown,
                    StartedAt = DateTimeOffset.UtcNow,
                    Source = PunishmentSource.EventSub,
                    LastUpdatedAt = DateTimeOffset.UtcNow
                });
                state = session.ActivePunishments.TryGetValue(e.TargetUserId, out var known)
                    ? ToModerationState(known.Type)
                    : ModerationMessageState.RemovedByModeration;
            }
            MarkMessagesModerated(session, message => string.Equals(message.UserId, e.TargetUserId, StringComparison.Ordinal), state);
        });
    }

    private void OnEventSubUserBanned(object? sender, ChannelUserBannedEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            var session = Channels.FirstOrDefault(channel =>
                string.Equals(channel.BroadcasterId, e.BroadcasterId, StringComparison.Ordinal));
            if (session is null || string.IsNullOrWhiteSpace(e.TargetUserId) ||
                !session.HasConfirmedModerationAccess)
            {
                return;
            }

            var duration = e.EndsAt is { } end
                ? Math.Max(1, (int)Math.Min(int.MaxValue, (end - e.StartedAt).TotalSeconds))
                : (int?)null;
            var state = new ActivePunishmentState
            {
                UserId = e.TargetUserId,
                UserLogin = e.TargetUserLogin,
                DisplayName = e.TargetUserName,
                Type = e.IsPermanent ? PunishmentType.Ban : PunishmentType.Timeout,
                StartedAt = e.StartedAt,
                EndsAt = e.EndsAt,
                DurationSeconds = duration,
                ModeratorId = e.ModeratorUserId,
                ModeratorName = e.ModeratorUserName,
                Reason = e.Reason,
                Source = PunishmentSource.EventSub,
                LastUpdatedAt = DateTimeOffset.UtcNow
            };
            MergePunishment(session, state);
            UpsertObservedPunishment(session, state);
            MarkMessagesModerated(
                session,
                message => string.Equals(message.UserId, e.TargetUserId, StringComparison.Ordinal),
                e.IsPermanent ? ModerationMessageState.Banned : ModerationMessageState.TimedOut,
                e.ModeratorUserId,
                e.ModeratorUserName,
                e.Reason);
            _moderationCacheService.ScheduleSave(session);
        });
    }

    private void OnEventSubUserUnbanned(object? sender, ChannelUserUnbannedEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            var session = Channels.FirstOrDefault(channel =>
                string.Equals(channel.BroadcasterId, e.BroadcasterId, StringComparison.Ordinal));
            if (session is null || !session.HasConfirmedModerationAccess)
            {
                return;
            }

            session.ActivePunishments.Remove(GetPunishmentKey(e.TargetUserId, e.TargetUserLogin));
            foreach (var staleKey in session.ActivePunishments
                         .Where(pair => string.Equals(pair.Value.UserId, e.TargetUserId, StringComparison.Ordinal) ||
                                        (!string.IsNullOrWhiteSpace(e.TargetUserLogin) &&
                                         string.Equals(pair.Value.UserLogin, e.TargetUserLogin, StringComparison.OrdinalIgnoreCase)))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                session.ActivePunishments.Remove(staleKey);
            }

            foreach (var entry in session.BannedUsers
                         .Where(entry => string.Equals(entry.UserId, e.TargetUserId, StringComparison.Ordinal) ||
                                         (!string.IsNullOrWhiteSpace(e.TargetUserLogin) &&
                                          string.Equals(entry.UserLogin, e.TargetUserLogin, StringComparison.OrdinalIgnoreCase)))
                         .ToArray())
            {
                session.BannedUsers.Remove(entry);
            }
            _moderationCacheService.ScheduleSave(session);
        });
    }

    private void OnUnbanRequestCreated(object? sender, UnbanRequestEntry request)
    {
        DispatchExternalEvent(() =>
        {
            var session = Channels.FirstOrDefault(channel => string.Equals(channel.BroadcasterId, request.BroadcasterId, StringComparison.Ordinal));
            if (session is null || !session.HasConfirmedModerationAccess ||
                session.UnbanRequests.Any(item => string.Equals(item.RequestId, request.RequestId, StringComparison.Ordinal)))
            {
                return;
            }
            session.UnbanRequests.Insert(0, request);
            TrimUnbanRequests(session);
            session.UnreadUnbanRequests++;
            session.UnbanRequestsStatus = L("NewUnbanRequest");
            TrackBackgroundTask(HydrateUnbanRequestProfileAsync(request));
            _moderationCacheService.ScheduleSave(session);
        });
    }

    private void OnUnbanRequestResolved(object? sender, UnbanRequestEntry request)
    {
        DispatchExternalEvent(() =>
        {
            var session = Channels.FirstOrDefault(channel => string.Equals(channel.BroadcasterId, request.BroadcasterId, StringComparison.Ordinal));
            if (session is null || !session.HasConfirmedModerationAccess)
            {
                return;
            }
            UpsertUnbanRequest(session, request);
            if (request.Status == UnbanRequestStatus.Approved)
            {
                RemoveUserPunishmentState(session, request.UserId, request.UserLogin);
            }
            _moderationCacheService.ScheduleSave(session);
        });
    }

    private void OnAutoModMessageHeld(object? sender, HeldAutoModMessage item)
    {
        DispatchExternalEvent(() =>
        {
            var session = Channels.FirstOrDefault(channel => string.Equals(channel.BroadcasterId, item.BroadcasterId, StringComparison.Ordinal));
            if (session is null || !session.HasConfirmedModerationAccess ||
                session.PendingAutoModMessages.Any(existing => string.Equals(existing.MessageId, item.MessageId, StringComparison.Ordinal)))
            {
                return;
            }
            item.ChannelLogin = session.ChannelLogin;
            session.PendingAutoModMessages.Add(item);
            while (session.PendingAutoModMessages.Count > 300)
            {
                session.PendingAutoModMessages.RemoveAt(0);
            }
        });
    }

    private void OnAutoModMessageUpdated(object? sender, AutoModMessageUpdatedEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            var session = Channels.FirstOrDefault(channel => string.Equals(channel.BroadcasterId, e.BroadcasterId, StringComparison.Ordinal));
            var item = session?.HasConfirmedModerationAccess == true
                ? session.PendingAutoModMessages.FirstOrDefault(candidate => string.Equals(candidate.MessageId, e.MessageId, StringComparison.Ordinal))
                : null;
            if (item is not null)
            {
                item.Status = e.Status;
                if (e.Status != HeldAutoModStatus.Pending)
                {
                    session!.PendingAutoModMessages.Remove(item);
                }
            }
        });
    }

    private void TrimUserProfileCache(DateTimeOffset now)
    {
        if (_userProfileCache.Count <= UserProfileCacheSoftLimit)
        {
            return;
        }

        foreach (var expired in _userProfileCache
                     .Where(entry => now - entry.Value.FetchedAt >= UserProfileCacheTtl)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _userProfileCache.Remove(expired);
        }

        while (_userProfileCache.Count > UserProfileCacheHardLimit)
        {
            var oldest = _userProfileCache.MinBy(entry => entry.Value.LastAccessAt).Key;
            _userProfileCache.Remove(oldest);
        }
    }

    private void OnReadOnlyChannelMessageReceived(object? sender, ChannelChatMessageEventArgs e)
    {
        QueueIncomingMessage(e.ChannelLogin, primaryChannel: false, e.Message);
    }

    private void HandleEventSubChatMessage(ChatMessageModel message)
    {
        var session = Channels.FirstOrDefault(channel =>
            !string.IsNullOrWhiteSpace(message.BroadcasterId) &&
            string.Equals(channel.BroadcasterId, message.BroadcasterId, StringComparison.Ordinal));
        if (session is null)
        {
            return;
        }

        var key = CreateMessageCorrelationKey(session.BroadcasterId, message.MessageId);
        if (session.IsPrimaryAccountChannel)
        {
            ApplyPendingChannelPointsMetadata(key, message);
            if (IsChannelPointsMetadata(message))
            {
                ApplyViewerChannelPointsMetadata(message, message);
            }
            message.ChannelLogin = session.ChannelLogin;
            PrepareMessageForDisplay(message, session);
            AddMessage(session, message);
            return;
        }

        if (!IsChannelPointsMetadata(message))
        {
            return;
        }

        var matchedMessage = string.IsNullOrWhiteSpace(key)
            ? null
            : _liveMessageIndex.GetValueOrDefault(key);
        var matched = matchedMessage is not null;
        if (matchedMessage is not null)
        {
            ApplyViewerChannelPointsMetadata(matchedMessage, message);
        }
        else
        {
            StorePendingChannelPointsMetadata(key, message, fullRedemption: false);
        }

        _logger.Info(
            $"Channel Points metadata: channel={session.ChannelLogin}, messageId={message.MessageId}, " +
            $"messageType={message.MessageType}, customRewardId={(string.IsNullOrWhiteSpace(message.CustomRewardId) ? "empty" : "present")}, matchedIrc={matched.ToString().ToLowerInvariant()}");
    }

    private void HandleReadOnlyChatMessage(
        string channelLogin,
        ChatMessageModel message,
        bool logMessage = true)
    {
        var session = FindChannel(channelLogin);
        if (session is null)
        {
            return;
        }

        var broadcasterId = string.IsNullOrWhiteSpace(message.RoomId)
            ? session.BroadcasterId
            : message.RoomId;
        var key = CreateMessageCorrelationKey(broadcasterId, message.Id);
        ApplyPendingChannelPointsMetadata(key, message);
        message.ChannelLogin = session.ChannelLogin;
        PrepareMessageForDisplay(message, session);
        AddMessage(session, message, logMessage);
    }

    private void OnReadOnlyChannelIdentityResolved(object? sender, ChannelIdentityResolvedEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            var channelLogin = e.ChannelLogin;
            var broadcasterId = e.BroadcasterId;
            var session = FindChannel(channelLogin);
            if (session is null)
            {
                return;
            }

            session.BroadcasterId = broadcasterId;
            SetChannelConnectionState(session, "connected");
            session.CanSend = IsAccountAuthenticated;
            TrackBackgroundTask(RefreshModerationAccessAsync(session));
            if (IsAccountAuthenticated && session.IsPrimaryAccountChannel)
            {
                TrackBackgroundTask(RefreshModerationStateInBackgroundAsync(session));
            }
            if (ConnectionMode == ChatConnectionMode.ReadOnly && ReferenceEquals(session, ActiveChannel))
            {
                _broadcaster = SessionToUser(session);
            }

            if (ReferenceEquals(session, ActiveChannel))
            {
                _thirdPartyEmoteService.SetActiveChannel(broadcasterId);
                RaiseStatePropertiesChanged();
            }

            _logger.Info($"IRC channel joined: {channelLogin}");
            _logger.Info($"Read-only Twitch room resolved: channel={channelLogin}, broadcaster_id={broadcasterId}");
            TrackBackgroundTask(RefreshReadOnlyChannelAssetsAsync(channelLogin, broadcasterId));
            if (IsAccountAuthenticated)
            {
                TrackBackgroundTask(_eventSubClient.TrySubscribeChatMessageAsync(broadcasterId, _disposeCts.Token));
                TrackBackgroundTask(_eventSubClient.TrySubscribeChannelPointsAsync(broadcasterId, _disposeCts.Token));
            }
        });
    }

    private void HandleChannelPointsRedemption(ChatMessageModel message)
    {
        var session = Channels.FirstOrDefault(channel =>
                          !string.IsNullOrWhiteSpace(message.BroadcasterId) &&
                          string.Equals(channel.BroadcasterId, message.BroadcasterId, StringComparison.Ordinal))
                      ?? FindChannel(message.ChannelLogin);
        if (session is null || string.IsNullOrWhiteSpace(message.RedemptionId))
        {
            return;
        }

        var messageKey = CreateMessageCorrelationKey(session.BroadcasterId, message.MessageId);
        if (!string.IsNullOrWhiteSpace(messageKey))
        {
            if (_liveMessageIndex.TryGetValue(messageKey, out var existingMessage))
            {
                ApplyFullChannelPointsDetails(existingMessage, message);
                _logger.Info(
                    $"Full redemption: channel={session.ChannelLogin}, type={(string.IsNullOrWhiteSpace(message.RewardType) ? "custom" : "automatic")}, " +
                    $"rewardType={message.RewardType}, matchedMessage=true");
                return;
            }

            StorePendingChannelPointsMetadata(messageKey, message, fullRedemption: true);
            _logger.Info(
                $"Full redemption: channel={session.ChannelLogin}, type={(string.IsNullOrWhiteSpace(message.RewardType) ? "custom" : "automatic")}, " +
                $"rewardType={message.RewardType}, matchedMessage=false");
            return;
        }

        var deduplicationKey = session.ChannelLogin + "\n" + message.RedemptionId;
        if (!_seenRedemptionIds.Add(deduplicationKey))
        {
            return;
        }

        _seenRedemptionOrder.Enqueue(deduplicationKey);
        while (_seenRedemptionOrder.Count > RedemptionDedupLimit)
        {
            _seenRedemptionIds.Remove(_seenRedemptionOrder.Dequeue());
        }

        if (string.IsNullOrWhiteSpace(message.RewardTitle))
        {
            message.RewardTitle = GetAutomaticRewardTitle(message.RewardType);
        }

        var culture = CultureInfo.GetCultureInfo(Settings.Language);
        message.IsChannelPointsMessage = true;
        message.ChannelPointsDetailsAvailable = true;
        message.Kind = Settings.ShowChannelPointRedemptions
            ? ChatMessageKind.ChannelPointsRedemption
            : ChatMessageKind.Regular;
        message.RedemptionSummary = BuildChannelPointsSummary(message, culture);
        message.RewardUserInputLabel = L("ChannelPointsUserInput");

        if (Settings.EnableChatLogging && Settings.LogChannelPointRedemptions)
        {
            LogMessage(session, message);
        }

        if (Settings.ShowChannelPointRedemptions)
        {
            QueueIncomingMessage(session.ChannelLogin, primaryChannel: false, message, logMessage: false);
            TrackBackgroundTask(LoadUserProfileAsync(message));
        }
    }

    private static string CreateMessageCorrelationKey(string broadcasterId, string messageId)
    {
        broadcasterId = (broadcasterId ?? string.Empty).Trim();
        messageId = (messageId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(broadcasterId) || string.IsNullOrWhiteSpace(messageId)
            ? string.Empty
            : broadcasterId + "\n" + messageId;
    }

    private static bool IsChannelPointsMetadata(ChatMessageModel message) =>
        !string.IsNullOrWhiteSpace(message.CustomRewardId) ||
        string.Equals(message.MessageType, "channel_points_highlighted", StringComparison.Ordinal) ||
        string.Equals(message.MessageType, "channel_points_sub_only", StringComparison.Ordinal);

    private void ApplyViewerChannelPointsMetadata(ChatMessageModel target, ChatMessageModel metadata)
    {
        ApplyViewerChannelPointsMetadata(target, ChannelPointsMetadataSnapshot.From(metadata));
    }

    private void ApplyViewerChannelPointsMetadata(ChatMessageModel target, ChannelPointsMetadataSnapshot metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.CustomRewardId) &&
            !string.Equals(metadata.MessageType, "channel_points_highlighted", StringComparison.Ordinal) &&
            !string.Equals(metadata.MessageType, "channel_points_sub_only", StringComparison.Ordinal))
        {
            return;
        }

        target.MessageType = metadata.MessageType;
        target.CustomRewardId = metadata.CustomRewardId;
        target.IsChannelPointsMessage = true;
        target.ChannelPointsDetailsAvailable = false;
        target.RewardType = metadata.MessageType switch
        {
            "channel_points_highlighted" => "send_highlighted_message",
            "channel_points_sub_only" => "single_message_bypass_sub_mode",
            _ => string.Empty
        };
        target.RewardTitle = metadata.MessageType switch
        {
            "channel_points_highlighted" => L("AutomaticRewardSendHighlightedMessage"),
            "channel_points_sub_only" => L("AutomaticRewardSingleMessageBypassSubMode"),
            _ => L("ChannelPointsCustomReward")
        };
        target.Kind = Settings.ShowChannelPointRedemptions
            ? ChatMessageKind.ChannelPointsRedemption
            : ChatMessageKind.Regular;
        target.RedemptionSummary = string.Format(
            CultureInfo.GetCultureInfo(Settings.Language),
            L("ChannelPointsViewerFormat"),
            target.UserLabel,
            target.RewardTitle);
        target.RewardUserInputLabel = L("ChannelPointsUserInput");
    }

    private void ApplyFullChannelPointsDetails(ChatMessageModel target, ChatMessageModel full)
    {
        ApplyFullChannelPointsDetails(target, ChannelPointsMetadataSnapshot.From(full));
    }

    private void ApplyFullChannelPointsDetails(ChatMessageModel target, ChannelPointsMetadataSnapshot full)
    {
        target.IsChannelPointsMessage = true;
        target.ChannelPointsDetailsAvailable = true;
        target.RedemptionId = full.RedemptionId;
        target.RewardId = full.RewardId;
        target.RewardTitle = string.IsNullOrWhiteSpace(full.RewardTitle)
            ? GetAutomaticRewardTitle(full.RewardType)
            : full.RewardTitle;
        target.RewardCost = full.RewardCost;
        target.RewardType = full.RewardType;
        if (target.Parts.Count == 0 && !string.IsNullOrWhiteSpace(full.RewardUserInput))
        {
            target.RewardUserInput = full.RewardUserInput;
        }
        target.Kind = Settings.ShowChannelPointRedemptions
            ? ChatMessageKind.ChannelPointsRedemption
            : ChatMessageKind.Regular;
        target.RedemptionSummary = BuildChannelPointsSummary(
            target,
            CultureInfo.GetCultureInfo(Settings.Language));
        target.RewardUserInputLabel = L("ChannelPointsUserInput");
    }

    private string BuildChannelPointsSummary(ChatMessageModel message, CultureInfo culture)
    {
        if (message.RewardCost is { } rewardCost)
        {
            return string.Format(
                culture,
                L("ChannelPointsRedemptionFormat"),
                message.UserLabel,
                message.RewardTitle,
                rewardCost.ToString("N0", culture));
        }

        return string.Format(
            culture,
            L("ChannelPointsViewerFormat"),
            message.UserLabel,
            message.RewardTitle);
    }

    private void IndexLiveMessage(string key, ChatMessageModel message)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!_liveMessageIndex.ContainsKey(key))
        {
            _liveMessageIndexOrder.Enqueue(key);
        }
        _liveMessageIndex[key] = message;
        while (_liveMessageIndexOrder.Count > LiveMessageCorrelationLimit)
        {
            _liveMessageIndex.Remove(_liveMessageIndexOrder.Dequeue());
        }
    }

    private void StorePendingChannelPointsMetadata(string key, ChatMessageModel metadata, bool fullRedemption)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        TrimPendingChannelPointsMetadata();
        if (!_pendingChannelPointsMetadata.TryGetValue(key, out var pending))
        {
            pending = new PendingChannelPointsMetadata();
            _pendingChannelPointsMetadata[key] = pending;
            _pendingChannelPointsOrder.Enqueue((key, DateTimeOffset.UtcNow.AddMinutes(3)));
        }

        if (fullRedemption)
        {
            pending.FullRedemption = ChannelPointsMetadataSnapshot.From(metadata);
        }
        else
        {
            pending.ChatMetadata = ChannelPointsMetadataSnapshot.From(metadata);
        }

        while (_pendingChannelPointsMetadata.Count > PendingMetadataLimit && _pendingChannelPointsOrder.Count > 0)
        {
            _pendingChannelPointsMetadata.Remove(_pendingChannelPointsOrder.Dequeue().Key);
        }
        while (_pendingChannelPointsOrder.Count > PendingMetadataLimit * 2)
        {
            _pendingChannelPointsMetadata.Remove(_pendingChannelPointsOrder.Dequeue().Key);
        }
    }

    private void ApplyPendingChannelPointsMetadata(string key, ChatMessageModel target)
    {
        TrimPendingChannelPointsMetadata();
        if (string.IsNullOrWhiteSpace(key) || !_pendingChannelPointsMetadata.Remove(key, out var pending))
        {
            return;
        }

        if (pending.ChatMetadata is not null)
        {
            ApplyViewerChannelPointsMetadata(target, pending.ChatMetadata);
        }
        if (pending.FullRedemption is not null)
        {
            ApplyFullChannelPointsDetails(target, pending.FullRedemption);
        }
    }

    private void TrimPendingChannelPointsMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        while (_pendingChannelPointsOrder.TryPeek(out var entry) && entry.ExpiresAt <= now)
        {
            _pendingChannelPointsOrder.Dequeue();
            _pendingChannelPointsMetadata.Remove(entry.Key);
        }
    }

    private void OnEventSubStatusChanged(object? sender, EventSubConnectionStatusEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            StatusText = e.State switch
            {
                ChannelConnectionState.Connecting => L("ChannelConnecting"),
                ChannelConnectionState.Reconnecting => L("ChannelReconnecting"),
                ChannelConnectionState.Connected => L("ChannelConnected"),
                ChannelConnectionState.Disconnected => L("ChatDisconnected"),
                ChannelConnectionState.Error when e.ErrorCode == "subscription_revoked" => L("EventSubSubscriptionRevoked"),
                _ => L("ConnectionError")
            };
            var primary = Channels.FirstOrDefault(channel => channel.IsPrimaryAccountChannel);
            if (primary is not null)
            {
                var state = e.State switch
                {
                    ChannelConnectionState.Connected => "connected",
                    ChannelConnectionState.Connecting or ChannelConnectionState.Reconnecting => "connecting",
                    ChannelConnectionState.Error => "error",
                    _ => "disconnected"
                };
                SetChannelConnectionState(primary, state, e.State == ChannelConnectionState.Error ? StatusText : null);
                if (e.State == ChannelConnectionState.Connected)
                {
                    _logger.Info("Primary EventSub connected.");
                }
            }
        });
    }

    private void OnChannelPointsAuthorizationRequired(object? sender, EventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            StatusText = L("ChannelPointsSignInAgain");
            RaiseStatePropertiesChanged();
        });
    }

    private void OnReadOnlyChannelStatusChanged(object? sender, ChannelConnectionStatusEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            var channel = FindChannel(e.ChannelLogin);
            if (channel is null || channel.IsPrimaryAccountChannel)
            {
                return;
            }

            var state = e.State switch
            {
                ChannelConnectionState.Connected => "connected",
                ChannelConnectionState.Connecting or ChannelConnectionState.Reconnecting => "connecting",
                ChannelConnectionState.Error => "error",
                _ => "disconnected"
            };
            SetChannelConnectionState(
                channel,
                state,
                e.State == ChannelConnectionState.Error ? L("WatchOnlyConnectFailed") : null);
            StatusText = channel.ConnectionStatus;
        });
    }

    private void OnChatLogWriteFailed(object? sender, EventArgs e)
    {
        DispatchExternalEvent(() => StatusText = L("ChatLogWriteFailed"));
    }

    private void DispatchExternalEvent(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (IsShuttingDown)
        {
            return;
        }

        _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsShuttingDown)
            {
                action();
            }
        }), priority);
    }

    private void QueueIncomingMessage(
        string channelLogin,
        bool primaryChannel,
        ChatMessageModel message,
        bool logMessage = true)
    {
        if (IsShuttingDown)
        {
            return;
        }

        var dropped = 0;
        lock (_pendingChatMessagesGate)
        {
            if (IsShuttingDown)
            {
                return;
            }

            _pendingChatMessages.Enqueue(new PendingChatMessage(channelLogin, primaryChannel, message, logMessage));
            _pendingChatMessageCount++;
            while (_pendingChatMessageCount > MaxPendingChatMessages && _pendingChatMessages.TryDequeue(out _))
            {
                _pendingChatMessageCount--;
                dropped++;
            }
        }

        if (dropped > 0)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Volatile.Read(ref _lastQueueDropWarning);
            if (previous == 0 || Stopwatch.GetElapsedTime(previous, now) >= TimeSpan.FromMinutes(1))
            {
                Volatile.Write(ref _lastQueueDropWarning, now);
                _logger.Warn($"Incoming chat queue reached {MaxPendingChatMessages}; oldest events were dropped to protect memory.");
            }
        }

        SchedulePendingMessageDrain();
    }

    private void SchedulePendingMessageDrain()
    {
        if (IsShuttingDown)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _messageDrainScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsShuttingDown)
            {
                Interlocked.Exchange(ref _messageDrainScheduled, 0);
                return;
            }

            if (!_messageBatchTimer.IsEnabled)
            {
                _messageBatchTimer.Start();
            }
        }), DispatcherPriority.DataBind);
    }

    private void DrainPendingMessages()
    {
        if (IsShuttingDown)
        {
            ClearPendingChatMessages();
            Interlocked.Exchange(ref _messageDrainScheduled, 0);
            return;
        }

#if DEBUG
        var stopwatch = Stopwatch.StartNew();
#endif
        var processed = 0;
        var drainStarted = Stopwatch.GetTimestamp();
        try
        {
            while (processed < 128)
            {
                PendingChatMessage? pending;
                lock (_pendingChatMessagesGate)
                {
                    if (!_pendingChatMessages.TryDequeue(out pending) || pending is null)
                    {
                        break;
                    }

                    _pendingChatMessageCount--;
                }

                try
                {
                    if (pending.PrimaryChannel)
                    {
                        if (pending.Message.IsChannelPointsRedemption)
                        {
                            HandleChannelPointsRedemption(pending.Message);
                        }
                        else
                        {
                            HandleEventSubChatMessage(pending.Message);
                        }
                    }
                    else
                    {
                        HandleReadOnlyChatMessage(pending.ChannelLogin, pending.Message, pending.LogMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Incoming message UI update failed: {ex.GetType().Name}");
                }

                processed++;
                if (Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds >= IncomingMessageDrainBudgetMs)
                {
                    break;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _messageDrainScheduled, 0);
            if (!_pendingChatMessages.IsEmpty)
            {
                SchedulePendingMessageDrain();
            }
        }

#if DEBUG
        _processedLiveMessageCount += processed;
        if (processed > 0 && _processedLiveMessageCount % 500 < processed)
        {
            var counts = string.Join(", ", Channels.Select(channel => $"{channel.ChannelLogin}={channel.Messages.Count}"));
            int profileCount;
            lock (_userProfileCacheGate)
            {
                profileCount = _userProfileCache.Count;
            }
            Debug.WriteLine(
                $"WitherChat live messages={_processedLiveMessageCount}, batch={processed}, elapsed={stopwatch.Elapsed.TotalMilliseconds:F1}ms, " +
                $"sessions=[{counts}], media={_emoteCache.MediaCount}, images={_emoteCache.StaticImageCount}, " +
                $"decodedBytes={_emoteCache.ApproximateDecodedBytes:N0}, animations={AnimatedEmoteImage.ActiveCount}, " +
                $"profiles={profileCount}, pending={_pendingChannelPointsMetadata.Count}, " +
                $"managed={GC.GetTotalMemory(false):N0}, workingSet={Process.GetCurrentProcess().WorkingSet64:N0}");
        }
#endif
    }

    private void ClearPendingChatMessages()
    {
        lock (_pendingChatMessagesGate)
        {
            while (_pendingChatMessages.TryDequeue(out _))
            {
            }

            _pendingChatMessageCount = 0;
        }
    }

    private async Task LoadMessageImagesSafelyAsync(ChatMessageModel message)
    {
        var enteredGate = false;
        try
        {
            await _messageHydrationGate.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
            enteredGate = true;
            if (_isUserScrolling || !_visibleMessages.ContainsKey(message))
            {
                return;
            }

            await LoadMessageImagesAsync(message, _disposeCts.Token, requireVisible: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Incoming message image load failed: {ex.GetType().Name}");
        }
        finally
        {
            if (enteredGate)
            {
                _messageHydrationGate.Release();
            }
            _messageImageLoads.TryRemove(message, out _);
        }
    }

    private void AddMessage(ChannelSessionViewModel session, ChatMessageModel message, bool logMessage = true)
    {
        var serverMessageId = string.IsNullOrWhiteSpace(message.MessageId) ? message.Id : message.MessageId;
        var messageBroadcasterId = !string.IsNullOrWhiteSpace(message.RoomId)
            ? message.RoomId
            : !string.IsNullOrWhiteSpace(message.BroadcasterId)
                ? message.BroadcasterId
                : session.BroadcasterId;
        var deletionKey = CreateMessageCorrelationKey(messageBroadcasterId, serverMessageId);
        if (!string.IsNullOrWhiteSpace(deletionKey) && _pendingDeletedMessageKeys.Remove(deletionKey))
        {
            message.MarkModerated(ModerationMessageState.Deleted);
        }

        if (!string.IsNullOrWhiteSpace(message.Id))
        {
            var deduplicationKey = session.ChannelLogin + "\n" + message.Id;
            if (!_seenMessageIds.Add(deduplicationKey))
            {
                MergeDuplicateMessage(session, message);
                return;
            }

            _seenMessageOrder.Enqueue(deduplicationKey);
            TrimSeenIds();
        }

        message.ChannelLogin = session.ChannelLogin;
        message.IsPinned = IsPinnedMessage(session.PinnedMessage, message);
        IndexLiveMessage(CreateMessageCorrelationKey(messageBroadcasterId, message.MessageId), message);
        if (_currentUser is not null && string.Equals(message.UserId, _currentUser.Id, StringComparison.Ordinal))
        {
            if (message.Badges.Any(badge => string.Equals(badge.SetId, "broadcaster", StringComparison.OrdinalIgnoreCase)))
            {
                ApplyModerationAccess(session, new ChannelModerationAccess(true, false, true));
            }
            else if (message.Badges.Any(badge => string.Equals(badge.SetId, "moderator", StringComparison.OrdinalIgnoreCase)))
            {
                ApplyModerationAccess(session, new ChannelModerationAccess(false, true, true));
            }
        }
        var deferVisual = ShouldDeferVisualMessage(session);
        if (deferVisual)
        {
            session.PendingVisualMessages.Enqueue(message);
            var pendingLimit = LiveChatBufferPolicy.GetTarget(Settings.MessageLimit);
            while (session.PendingVisualMessages.Count > pendingLimit)
            {
                session.PendingVisualMessages.Dequeue();
            }
        }
        else session.Messages.Add(message);
        session.LastActivityAt = message.Timestamp;
        if (!ReferenceEquals(session, ActiveChannel))
        {
            session.UnreadCount = session.UnreadCount == int.MaxValue
                ? int.MaxValue
                : session.UnreadCount + 1;
        }
        else
        {
            _overlayServer.PublishMessage(message);
            if (deferVisual)
            {
                session.NewMessagesBelowCount = session.NewMessagesBelowCount == int.MaxValue
                    ? int.MaxValue
                    : session.NewMessagesBelowCount + 1;
            }
            else
            {
                OnPropertyChanged(nameof(HasMessages));
                OnPropertyChanged(nameof(ShowChatEmptyState));
            }
        }

        if (logMessage &&
            (!message.IsChannelPointsMessage ||
             (Settings.EnableChatLogging && Settings.LogChannelPointRedemptions)))
        {
            LogMessage(session, message);
        }
        if (!deferVisual) TrimMessagesToLimit(session);
    }

    private void ApplyKnownSenderPresentation(ChannelSessionViewModel session, ChatMessageModel localMessage)
    {
        var previous = session.Messages
            .LastOrDefault(message =>
                !message.IsLocalEcho &&
                ((!string.IsNullOrWhiteSpace(localMessage.UserId) &&
                  string.Equals(message.UserId, localMessage.UserId, StringComparison.Ordinal)) ||
                 string.Equals(message.Login, localMessage.Login, StringComparison.OrdinalIgnoreCase)));

        if (previous is not null)
        {
            if (!string.IsNullOrWhiteSpace(previous.Color))
            {
                localMessage.Color = previous.Color;
            }

            foreach (var badge in previous.Badges)
            {
                localMessage.Badges.Add(new BadgeModel
                {
                    SetId = badge.SetId,
                    Id = badge.Id,
                    Info = badge.Info,
                    ImageUrl = badge.ImageUrl,
                    Title = badge.Title,
                    ImageSource = badge.ImageSource
                });
            }
            return;
        }

        var roleBadge = session.IsBroadcaster ? "broadcaster" : session.IsModerator ? "moderator" : string.Empty;
        if (!string.IsNullOrWhiteSpace(roleBadge))
        {
            localMessage.Badges.Add(new BadgeModel { SetId = roleBadge, Id = "1" });
        }
    }

    internal void SetMessageImagesVisible(ChatMessageModel message, bool isVisible)
    {
        if (!isVisible)
        {
            _visibleMessages.TryRemove(message, out _);
            ReleaseMessageMedia(message);
            return;
        }

        _visibleMessages.TryAdd(message, 0);
        if (message.PresentationVersion != _messagePresentationVersion)
        {
            PrepareMessageForDisplay(message);
        }
        QueueMessageImageLoad(message);
    }

    private void InvalidateMessagePresentations(ChannelSessionViewModel? session = null)
    {
        _messagePresentationVersion = _messagePresentationVersion == int.MaxValue
            ? 1
            : _messagePresentationVersion + 1;

        foreach (var message in _visibleMessages.Keys.ToArray())
        {
            var messageSession = FindChannel(message.ChannelLogin);
            if (session is not null && !ReferenceEquals(messageSession, session))
            {
                continue;
            }

            PrepareMessageForDisplay(message, messageSession);
            QueueMessageImageLoad(message);
        }
    }

    private void QueueMessageImageLoad(ChatMessageModel message)
    {
        if (IsShuttingDown || !_visibleMessages.ContainsKey(message))
        {
            return;
        }

        HydrateVisibleBadges(message);
        HydrateCachedMessageMedia(message);
        if (_isUserScrolling ||
            !HasUnloadedMessageImages(message) ||
            !_messageImageLoads.TryAdd(message, 0))
        {
            return;
        }

        TrackBackgroundTask(LoadMessageImagesSafelyAsync(message));
    }

    private void HydrateCachedMessageMedia(ChatMessageModel message)
    {
        foreach (var part in EnumerateImageParts(message.Parts))
        {
            if (part.Media is null &&
                !string.IsNullOrWhiteSpace(part.CacheKey) &&
                _emoteCache.TryGetCachedMedia(part.CacheKey, out var media))
            {
                part.Media = media;
            }
        }
    }

    private void HydrateCachedBadges(ChatMessageModel message)
    {
        if (!Settings.EnableBadges)
        {
            return;
        }

        foreach (var badge in message.Badges)
        {
            if (badge.ImageSource is null &&
                !string.IsNullOrWhiteSpace(badge.ImageUrl) &&
                _emoteCache.TryGetCachedImage(badge.ImageUrl, out var image))
            {
                badge.ImageSource = image;
            }
        }
    }

    private void HydrateVisibleBadges(ChatMessageModel message)
    {
        HydrateCachedBadges(message);
        if (!_isUserScrolling ||
            !Settings.EnableBadges ||
            !_visibleMessages.ContainsKey(message))
        {
            return;
        }

        foreach (var imageUrl in message.Badges
                     .Where(badge => badge.ImageSource is null && !string.IsNullOrWhiteSpace(badge.ImageUrl))
                     .Select(badge => badge.ImageUrl)
                     .Distinct(StringComparer.Ordinal))
        {
            if (_emoteCache.TryGetImageTask(imageUrl, out var imageTask))
            {
                QueueBadgeImageLoad(imageUrl, imageTask);
            }
        }
    }

    private void QueueBadgeImageLoad(string imageUrl, Task<ImageSource?> imageTask)
    {
        if (IsShuttingDown ||
            _badgeImageLoads.Count >= MaxObservedBadgeLoadsWhileScrolling ||
            !_badgeImageLoads.TryAdd(imageUrl, 0))
        {
            return;
        }

        TrackBackgroundTask(LoadBadgeImageSafelyAsync(imageUrl, imageTask));
    }

    private async Task LoadBadgeImageSafelyAsync(string imageUrl, Task<ImageSource?> imageTask)
    {
        try
        {
            var image = await imageTask.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
            if (image is null || IsShuttingDown)
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (IsShuttingDown || !Settings.EnableBadges)
                {
                    return;
                }

                foreach (var message in _visibleMessages.Keys)
                {
                    foreach (var badge in message.Badges)
                    {
                        if (badge.ImageSource is null &&
                            string.Equals(badge.ImageUrl, imageUrl, StringComparison.Ordinal))
                        {
                            badge.ImageSource = image;
                        }
                    }
                }
            }, DispatcherPriority.Render, _disposeCts.Token);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Badge image load failed: {ex.GetType().Name}");
        }
        finally
        {
            _badgeImageLoads.TryRemove(imageUrl, out _);
        }
    }

    private static void ReleaseMessageMedia(ChatMessageModel message)
    {
        foreach (var part in EnumerateImageParts(message.Parts))
        {
            part.Media = null;
        }
    }

    private void ReleaseAllBadgeImages()
    {
        foreach (var message in Channels.SelectMany(channel =>
                     channel.Messages.Concat(channel.PendingVisualMessages)))
        {
            foreach (var badge in message.Badges)
            {
                badge.ImageSource = null;
            }
        }
    }

    private void MergeDuplicateMessage(ChannelSessionViewModel session, ChatMessageModel incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming.Id))
        {
            return;
        }

        var correlationBroadcasterId = !string.IsNullOrWhiteSpace(incoming.RoomId)
            ? incoming.RoomId
            : !string.IsNullOrWhiteSpace(incoming.BroadcasterId)
                ? incoming.BroadcasterId
                : session.BroadcasterId;
        var correlationMessageId = string.IsNullOrWhiteSpace(incoming.MessageId)
            ? incoming.Id
            : incoming.MessageId;
        var correlationKey = CreateMessageCorrelationKey(correlationBroadcasterId, correlationMessageId);
        var existing = string.IsNullOrWhiteSpace(correlationKey)
            ? null
            : _liveMessageIndex.GetValueOrDefault(correlationKey);
        existing ??= session.Messages.LastOrDefault(message =>
                        string.Equals(message.Id, incoming.Id, StringComparison.Ordinal))
                    ?? session.PendingVisualMessages.LastOrDefault(message =>
                        string.Equals(message.Id, incoming.Id, StringComparison.Ordinal));
        if (existing is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(incoming.Color))
        {
            existing.Color = incoming.Color;
        }

        if (incoming.Badges.Count > 0)
        {
            existing.Badges.Clear();
            foreach (var badge in incoming.Badges)
            {
                existing.Badges.Add(badge);
            }
        }

        if (incoming.OriginalParts.Count > 0 &&
            GetMessagePresentationRichness(incoming) > GetMessagePresentationRichness(existing))
        {
            existing.ReplaceOriginalParts(incoming.OriginalParts);
            PrepareMessageForDisplay(existing, session);
        }

        if (IsChannelPointsMetadata(incoming))
        {
            ApplyViewerChannelPointsMetadata(existing, incoming);
        }
        if (incoming.ChannelPointsDetailsAvailable)
        {
            ApplyFullChannelPointsDetails(existing, incoming);
        }
        if (incoming.IsModerated)
        {
            existing.MarkModerated(
                incoming.ModerationState,
                incoming.ModerationReason,
                incoming.ModeratedByUserId,
                incoming.ModeratedByDisplayName);
        }
        if (string.IsNullOrWhiteSpace(existing.SourceChannelLogin) && !string.IsNullOrWhiteSpace(incoming.SourceChannelLogin))
        {
            existing.SourceChannelLogin = incoming.SourceChannelLogin;
        }
        if (string.IsNullOrWhiteSpace(existing.SourceChannelDisplayName) && !string.IsNullOrWhiteSpace(incoming.SourceChannelDisplayName))
        {
            existing.SourceChannelDisplayName = incoming.SourceChannelDisplayName;
        }
        if (string.IsNullOrWhiteSpace(existing.ProfileImageUrl) && !string.IsNullOrWhiteSpace(incoming.ProfileImageUrl))
        {
            existing.ProfileImageUrl = incoming.ProfileImageUrl;
        }
        existing.IsPinned |= incoming.IsPinned;

        var broadcasterId = !string.IsNullOrWhiteSpace(existing.RoomId)
            ? existing.RoomId
            : !string.IsNullOrWhiteSpace(existing.BroadcasterId)
                ? existing.BroadcasterId
                : session.BroadcasterId;
        IndexLiveMessage(CreateMessageCorrelationKey(broadcasterId, existing.MessageId), existing);
        QueueMessageImageLoad(existing);
    }

    private static int GetMessagePresentationRichness(ChatMessageModel message) =>
        (message.OriginalParts.Count(part => part.Kind != ChatMessagePartKind.Text) * 10) +
        (IsChannelPointsMetadata(message) ? 5 : 0) +
        Math.Min(message.Badges.Count, 4);

    private bool ShouldDeferVisualMessage(ChannelSessionViewModel session) =>
        !session.AutoScroll;

    private void FlushPendingVisualMessages(ChannelSessionViewModel session)
    {
        if (session.PendingVisualMessages.Count == 0) return;
        var pending = session.PendingVisualMessages.ToArray();
        session.PendingVisualMessages.Clear();
        session.Messages.AppendRangeAndTrim(pending, LiveChatBufferPolicy.GetTarget(Settings.MessageLimit));
        if (ReferenceEquals(session, ActiveChannel))
        {
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(ShowChatEmptyState));
        }
    }

    private bool HasUnloadedMessageImages(ChatMessageModel message) =>
        EnumerateImageParts(message.Parts).Any(part =>
            part.Media is null && !string.IsNullOrWhiteSpace(part.ImageUrl)) ||
        Settings.EnableBadges && message.Badges.Any(badge =>
            badge.ImageSource is null && !string.IsNullOrWhiteSpace(badge.ImageUrl));

    private void LogMessage(ChannelSessionViewModel session, ChatMessageModel message)
    {
        if (session.IsPrimaryAccountChannel && IsAccountAuthenticated)
        {
            _chatLogService.Enqueue(message);
        }
        else
        {
            _chatLogService.Enqueue(
                Settings,
                session.ChannelLogin,
                session.BroadcasterId,
                session.DisplayName,
                new StreamStatusInfo(
                    session.IsLive,
                    session.ViewerCount,
                    session.StreamTitle,
                    session.GameName,
                    session.StreamStartedAt),
                message);
        }
    }

    private string GetAutomaticRewardTitle(string rewardType) => rewardType switch
    {
        "send_highlighted_message" => L("AutomaticRewardSendHighlightedMessage"),
        "single_message_bypass_sub_mode" => L("AutomaticRewardSingleMessageBypassSubMode"),
        "chosen_sub_emote_unlock" => L("AutomaticRewardChosenSubEmoteUnlock"),
        "random_sub_emote_unlock" => L("AutomaticRewardRandomSubEmoteUnlock"),
        "chosen_modified_sub_emote_unlock" => L("AutomaticRewardChosenModifiedSubEmoteUnlock"),
        "message_effect" => L("AutomaticRewardMessageEffect"),
        "gigantify_an_emote" => L("AutomaticRewardGigantifyEmote"),
        "celebration" => L("AutomaticRewardCelebration"),
        _ => L("TwitchAutomaticReward")
    };

    private void TrimMessagesToLimit(ChannelSessionViewModel session, bool force = false)
    {
        var target = LiveChatBufferPolicy.GetTarget(Settings.MessageLimit);
        var threshold = LiveChatBufferPolicy.GetTrimTrigger(Settings.MessageLimit);
        if (!force && session.Messages.Count < threshold)
        {
            return;
        }

        session.Messages.RemoveOldestRange(session.Messages.Count - target);
    }

    private void TrimSeenIds()
    {
        var max = Math.Max(LiveMessageCorrelationLimit, Settings.MessageLimit * 3);
        while (_seenMessageOrder.Count > max)
        {
            var id = _seenMessageOrder.Dequeue();
            _seenMessageIds.Remove(id);
        }
    }

    private async Task RemoveChannelAsync(ChannelSessionViewModel session)
    {
        if (!Channels.Contains(session))
        {
            return;
        }

        if (session.IsPrimaryAccountChannel)
        {
            return;
        }

        var ownsBusyState = !IsBusy;
        if (ownsBusyState)
        {
            IsBusy = true;
        }

        try
        {
            try
            {
                _logger.Info($"IRC channel part requested: {session.ChannelLogin}");
                await _readOnlyChatClient.PartChannelAsync(session.ChannelLogin, _disposeCts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.Warn($"IRC PART failed: {ex.GetType().Name}");
            }

            var index = Channels.IndexOf(session);
            var wasActive = ReferenceEquals(session, ActiveChannel);
            _thirdPartyEmoteService.Clear(ChannelAssetKey(session));
            CancelChannelAssetRefresh(session.ChannelLogin);
            await _chatLogService.StopChannelSessionAsync(session.ChannelLogin).ConfigureAwait(true);
            session.Messages.Clear();
            session.UnreadCount = 0;
            session.NewMessagesBelowCount = 0;
            session.SavedVerticalOffset = 0;
            session.AutoScroll = true;
            Channels.Remove(session);
            try
            {
                await _eventSubClient.RemoveBroadcasterAsync(
                    session.BroadcasterId,
                    _disposeCts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.Warn($"EventSub subscription cleanup failed: {ex.GetType().Name}");
            }

            if (wasActive)
            {
                ActiveChannel = Channels.Count == 0
                    ? null
                    : Channels[Math.Clamp(index, 0, Channels.Count - 1)];
            }

            if (string.Equals(Settings.LastReadOnlyChannel, session.ChannelLogin, StringComparison.OrdinalIgnoreCase))
            {
                Settings.LastReadOnlyChannel = string.Empty;
            }

            if (Channels.Count == 0)
            {
                Settings.LastActiveChannelLogin = string.Empty;
                Settings.LastReadOnlyChannel = string.Empty;
                _broadcaster = null;
                IsConnected = IsAccountAuthenticated;
                UpdateChatState("disconnected");
                UpdateStreamStatus(new StreamStatusInfo(false, 0, string.Empty));
                StatusText = L("ChatDisconnected");
            }

            PersistChannels();
            RaiseStatePropertiesChanged();
        }
        finally
        {
            if (ownsBusyState)
            {
                IsBusy = false;
            }
        }
    }

    private ChannelSessionViewModel? FindChannel(string? login)
    {
        var normalized = NormalizeChannelLogin(login);
        return Channels.FirstOrDefault(channel =>
            string.Equals(channel.ChannelLogin, normalized, StringComparison.OrdinalIgnoreCase));
    }

    internal void SetUserScrolling(bool isUserScrolling)
    {
        if (_isUserScrolling == isUserScrolling)
        {
            return;
        }

        _isUserScrolling = isUserScrolling;
        _messageBatchTimer.Interval = TimeSpan.FromMilliseconds(isUserScrolling ? 75 : 20);
        foreach (var message in _visibleMessages.Keys)
        {
            QueueMessageImageLoad(message);
        }
    }

    private void OnChannelPointsCapabilityChanged(object? sender, ChannelPointsCapabilityEventArgs e)
    {
        DispatchExternalEvent(() =>
        {
            var session = Channels.FirstOrDefault(channel =>
                string.Equals(channel.BroadcasterId, e.BroadcasterId, StringComparison.Ordinal));
            if (session is not null)
            {
                session.ChannelPointsDetailsAvailable = e.Available;
            }
        });
    }

    private void RefreshChannelCapacity()
    {
        OnPropertyChanged(nameof(EffectiveChannelCount));
        OnPropertyChanged(nameof(CanAddChannel));
        (AddChannelCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void PersistChannels()
    {
        Settings.SavedChannelLogins = Channels
            .Select(channel => channel.ChannelLogin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        Settings.LastActiveChannelLogin = ActiveChannel?.ChannelLogin ?? string.Empty;
        if (ConnectionMode == ChatConnectionMode.ReadOnly && ActiveChannel is not null)
        {
            Settings.LastReadOnlyChannel = ActiveChannel.ChannelLogin;
        }

        _settingsService.Save(Settings);
    }

    private void ResetChannelSessions()
    {
        Channels.Clear();
        _activeChannel = null;
        _filteredMessages = CollectionViewSource.GetDefaultView(_emptyMessages);
        _filteredMessages.Filter = HasActiveMessageFilter ? FilterMessage : null;
        OnPropertyChanged(nameof(ActiveChannel));
        OnPropertyChanged(nameof(Messages));
        OnPropertyChanged(nameof(FilteredMessages));
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(CanAddChannel));
        RaiseStatePropertiesChanged();
        (AddChannelCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        ActiveMessagesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TwitchUser SessionToUser(ChannelSessionViewModel session) => new()
    {
        Id = session.BroadcasterId,
        Login = session.ChannelLogin,
        DisplayName = session.DisplayName,
        ProfileImageUrl = session.ProfileImageUrl
    };

    private static string ChannelAssetKey(ChannelSessionViewModel session) =>
        string.IsNullOrWhiteSpace(session.BroadcasterId) ? session.ChannelLogin : session.BroadcasterId;

    private static string NormalizeChannelLogin(string? login) =>
        (login ?? string.Empty).Trim().TrimStart('@', '#').ToLowerInvariant();

    private static bool IsValidChannelLogin(string login) =>
        login.Length is > 0 and <= 25 && login.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private void ClearMessages()
    {
        if (ActiveChannel is not null)
        {
            var deduplicationPrefix = ActiveChannel.ChannelLogin + "\n";
            ActiveChannel.Messages.Clear();
            ActiveChannel.PendingVisualMessages.Clear();
            ActiveChannel.NewMessagesBelowCount = 0;

            var retainedKeys = _seenMessageOrder
                .Where(key => !key.StartsWith(deduplicationPrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var key in _seenMessageIds
                         .Where(key => key.StartsWith(deduplicationPrefix, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                _seenMessageIds.Remove(key);
            }
            _seenMessageOrder.Clear();
            foreach (var key in retainedKeys)
            {
                _seenMessageOrder.Enqueue(key);
            }
        }
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(ShowChatEmptyState));
        StatusText = L("ChatCleared");
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

    private bool HasActiveMessageFilter =>
        !string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrWhiteSpace(UserFilter);

    private void ApplyMessageFilter()
    {
        if (HasActiveMessageFilter)
        {
            if (FilteredMessages.Filter is null)
            {
                FilteredMessages.Filter = FilterMessage;
            }
            else
            {
                FilteredMessages.Refresh();
            }
        }
        else if (FilteredMessages.Filter is not null)
        {
            FilteredMessages.Filter = null;
        }
    }

    private async Task RefreshStreamStatusAsync(CancellationToken cancellationToken = default)
    {
        foreach (var session in Channels.Where(channel => !string.IsNullOrWhiteSpace(channel.BroadcasterId)).ToArray())
        {
            var status = await _streamStatusService.GetStatusAsync(session.BroadcasterId, cancellationToken).ConfigureAwait(false);
            if (!status.IsAuthoritative)
            {
                continue;
            }

            if (session.IsPrimaryAccountChannel)
            {
                await _chatLogService.UpdateStreamInfoAsync(status, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _chatLogService.UpdateChannelStreamInfoAsync(
                    session.ChannelLogin,
                    status,
                    cancellationToken).ConfigureAwait(false);
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                session.IsLive = status.IsLive;
                session.ViewerCount = status.IsLive ? status.ViewerCount : 0;
                session.StreamTitle = status.Title;
                session.GameName = status.GameName;
                session.StreamStartedAt = status.StartedAt;
                session.HasAuthoritativeStreamStatus = true;
                if (ReferenceEquals(session, ActiveChannel)) UpdateStreamStatus(status);
            });
        }
    }

    private void StartStreamStatusPolling()
    {
        StopStreamStatusPolling();
        if (_broadcaster is null || IsShuttingDown)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _streamStatusCts = cts;
        var task = PollStreamStatusAsync(cts);
        _streamStatusTask = task;
        TrackBackgroundTask(task);
    }

    private async Task PollStreamStatusAsync(CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(true);
                    await RefreshStreamStatusAsync(cancellationToken).ConfigureAwait(true);
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
        finally
        {
            Interlocked.CompareExchange(ref _streamStatusCts, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void StopStreamStatusPolling()
    {
        var cts = Interlocked.Exchange(ref _streamStatusCts, null);
        if (cts is null)
        {
            return;
        }

        CancelSafely(cts);
    }

    private void StartPinnedMessagePolling()
    {
        StopPinnedMessagePolling();
        var session = ActiveChannel;
        if (IsShuttingDown ||
            _currentUser is null ||
            session is null ||
            !session.HasConfirmedModerationAccess ||
            !_apiClient.HasChatModerationScope ||
            string.IsNullOrWhiteSpace(session.BroadcasterId))
        {
            ClearPinnedMessage(session);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _pinnedMessageCts = cts;
        var task = PollPinnedMessageAsync(cts);
        _pinnedMessageTask = task;
        TrackBackgroundTask(task);
    }

    private async Task PollPinnedMessageAsync(CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshPinnedMessageAsync(cancellationToken).ConfigureAwait(true);
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (TwitchApiException ex) when (ex.StatusCode is
                    System.Net.HttpStatusCode.Unauthorized or
                    System.Net.HttpStatusCode.Forbidden or
                    System.Net.HttpStatusCode.NotFound)
                {
                    await ClearPinnedMessageOnUiThreadAsync(cancellationToken).ConfigureAwait(true);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Pinned message refresh failed: {ex.GetType().Name}");
                    await ExpirePinnedMessageOnUiThreadAsync(cancellationToken).ConfigureAwait(true);
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _pinnedMessageCts, null, cancellation);
            cancellation.Dispose();
        }
    }

    private async Task RefreshPinnedMessageAsync(CancellationToken cancellationToken)
    {
        var session = ActiveChannel;
        var currentUser = _currentUser;
        if (session is null ||
            currentUser is null ||
            !session.HasConfirmedModerationAccess ||
            string.IsNullOrWhiteSpace(session.BroadcasterId))
        {
            return;
        }

        var broadcasterId = session.BroadcasterId;
        var pinnedMessage = await _apiClient
            .GetPinnedChatMessageAsync(broadcasterId, currentUser.Id, cancellationToken)
            .ConfigureAwait(false);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(session, ActiveChannel) ||
                !string.Equals(session.BroadcasterId, broadcasterId, StringComparison.Ordinal))
            {
                return;
            }

            ApplyPinnedMessage(session, pinnedMessage);
        }, DispatcherPriority.Background, cancellationToken);
    }

    private async Task ClearPinnedMessageOnUiThreadAsync(CancellationToken cancellationToken)
    {
        if (Application.Current is null)
        {
            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(
            () => ClearPinnedMessage(ActiveChannel),
            DispatcherPriority.Background,
            cancellationToken);
    }

    private async Task ExpirePinnedMessageOnUiThreadAsync(CancellationToken cancellationToken)
    {
        if (Application.Current is null)
        {
            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (ActiveChannel?.PinnedMessage?.EndsAt is { } endsAt && endsAt <= DateTimeOffset.UtcNow)
            {
                ClearPinnedMessage(ActiveChannel);
            }
        }, DispatcherPriority.Background, cancellationToken);
    }

    private static void ApplyPinnedMessage(ChannelSessionViewModel session, PinnedChatMessageModel? pinnedMessage)
    {
        if (pinnedMessage?.EndsAt is { } endsAt && endsAt <= DateTimeOffset.UtcNow)
        {
            pinnedMessage = null;
        }

        var previous = session.PinnedMessage;
        var previousMessageId = previous?.MessageId ?? string.Empty;
        var nextMessageId = pinnedMessage?.MessageId ?? string.Empty;
        var markerChanged = !string.Equals(previousMessageId, nextMessageId, StringComparison.Ordinal);
        var contentChanged = markerChanged ||
                             previous?.UpdatedAt != pinnedMessage?.UpdatedAt ||
                             previous?.EndsAt != pinnedMessage?.EndsAt ||
                             !string.Equals(previous?.Text, pinnedMessage?.Text, StringComparison.Ordinal) ||
                             !string.Equals(previous?.SenderLabel, pinnedMessage?.SenderLabel, StringComparison.Ordinal) ||
                             !string.Equals(previous?.PinnedByLabel, pinnedMessage?.PinnedByLabel, StringComparison.Ordinal);
        if (!contentChanged)
        {
            return;
        }

        session.PinnedMessage = pinnedMessage;
        if (!markerChanged)
        {
            return;
        }

        foreach (var message in session.Messages)
        {
            message.IsPinned = IsPinnedMessage(pinnedMessage, message);
        }

        foreach (var message in session.PendingVisualMessages)
        {
            message.IsPinned = IsPinnedMessage(pinnedMessage, message);
        }
    }

    private static void ClearPinnedMessage(ChannelSessionViewModel? session)
    {
        if (session is not null)
        {
            ApplyPinnedMessage(session, null);
        }
    }

    private static bool IsPinnedMessage(PinnedChatMessageModel? pinnedMessage, ChatMessageModel message)
    {
        if (pinnedMessage is null ||
            pinnedMessage.EndsAt is { } endsAt && endsAt <= DateTimeOffset.UtcNow ||
            string.IsNullOrWhiteSpace(pinnedMessage.MessageId))
        {
            return false;
        }

        return string.Equals(pinnedMessage.MessageId, message.MessageId, StringComparison.Ordinal) ||
               string.Equals(pinnedMessage.MessageId, message.Id, StringComparison.Ordinal);
    }

    private void StopPinnedMessagePolling()
    {
        var cts = Interlocked.Exchange(ref _pinnedMessageCts, null);
        if (cts is null)
        {
            return;
        }

        CancelSafely(cts);
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SetChannelConnectionState(ChannelSessionViewModel session, string state, string? error = null)
    {
        var oldState = session.IsConnected ? "connected" : session.IsConnecting ? "connecting" :
            string.IsNullOrWhiteSpace(session.ConnectionError) ? "disconnected" : "error";

        session.IsConnecting = string.Equals(state, "connecting", StringComparison.OrdinalIgnoreCase);
        session.IsConnected = string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase);
        session.ConnectionError = string.Equals(state, "error", StringComparison.OrdinalIgnoreCase)
            ? error ?? L("ChatError")
            : string.Empty;
        session.ConnectionStatus = state.ToLowerInvariant() switch
        {
            "connected" => L("ChannelConnected"),
            "connecting" => L("ChannelConnecting"),
            "error" => L("ChatError"),
            _ => L("ChannelDisconnected")
        };
        if (session.IsConnected)
        {
            session.LastConnectedAt = DateTimeOffset.UtcNow;
        }

        if (!string.Equals(oldState, state, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Info($"Channel state changed: {session.ChannelLogin} {oldState} -> {state}");
        }

        if (ReferenceEquals(session, ActiveChannel))
        {
            UpdateChatState(state, error);
            RaiseStatePropertiesChanged();
        }
    }

    private void UpdateAccountState(string state)
    {
        // API status must never contradict the authoritative account state.
        // A chat/IRC connection is independent and does not make the user signed in.
        if (string.Equals(state, "signed in", StringComparison.OrdinalIgnoreCase) &&
            !IsAccountAuthenticated)
        {
            state = "not signed in";
        }

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

        var requiresChatSignIn = string.Equals(state, "error", StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(detail, L("ChatReadRequiresSignIn"), StringComparison.Ordinal);

        ChatEmptyTitle = requiresChatSignIn
            ? L("ChatReadUnavailableTitle")
            : state switch
            {
                "connected" => L("EmptyConnectedTitle"),
                "connecting" => detail ?? L("ChatConnecting"),
                "error" => detail ?? L("ChatError"),
                _ => L("EmptyDisconnectedTitle")
            };

        ChatEmptyText = requiresChatSignIn
            ? L("ChatReadRequiresAccountHint")
            : state == "connected"
                ? $"{AppInfo.Name} · {L("VersionLine")}"
                : string.Empty;

        OnPropertyChanged(nameof(SendButtonToolTip));
        OnPropertyChanged(nameof(HeaderSubtitle));
    }
    private void UpdateStreamStatus(StreamStatusInfo status)
    {
        _hasAuthoritativeStreamStatus = status.IsAuthoritative;
        _isStreamLive = status.IsLive;
        _streamViewerCount = status.IsLive ? status.ViewerCount : 0;
        StreamStatusText = !status.IsAuthoritative
            ? L("StreamStatusUnknown")
            : status.IsLive ? L("StreamLive") : L("StreamOffline");
        StreamViewerText = status.IsLive && status.ViewerCount > 0
            ? FormatViewerCount(status.ViewerCount)
            : string.Empty;
        StreamIndicatorBrush = !status.IsAuthoritative
            ? CreateFrozenBrush("#FF73C7FF")
            : status.IsLive
            ? CreateFrozenBrush("#FFFF4D5E")
            : CreateFrozenBrush("#FF6E7482");
    }

    private async Task<TwitchTokenSet?> GetIrcTokenAsync(CancellationToken cancellationToken)
    {
        if (!_authService.HasAccessToken)
        {
            return null;
        }

        await _authService.EnsureValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return _authService.CurrentToken;
    }

    private string FormatViewerCount(int count)
    {
        var key = "ViewerMany";
        if (Settings.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            var lastTwoDigits = Math.Abs(count) % 100;
            var lastDigit = lastTwoDigits % 10;
            key = lastTwoDigits is >= 11 and <= 14
                ? "ViewerMany"
                : lastDigit switch
                {
                    1 => "ViewerOne",
                    2 or 3 or 4 => "ViewerFew",
                    _ => "ViewerMany"
                };
        }
        else if (count == 1)
        {
            key = "ViewerOne";
        }

        return $"{count:N0} {L(key)}";
    }

    private string FormatTimeoutDuration(int seconds)
    {
        seconds = Math.Max(1, seconds);
        var (value, unit) = seconds switch
        {
            _ when seconds % 86400 == 0 => (seconds / 86400, "Day"),
            _ when seconds % 3600 == 0 => (seconds / 3600, "Hour"),
            _ when seconds % 60 == 0 => (seconds / 60, "Minute"),
            _ => (seconds, "Second")
        };
        var plural = GetDurationPluralKey(value);
        return $"{value:N0} {L("Duration" + unit + plural)}";
    }

    private string GetDurationPluralKey(int value)
    {
        if (!Settings.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            return value == 1 ? "One" : "Many";
        }

        var lastTwoDigits = Math.Abs(value) % 100;
        var lastDigit = lastTwoDigits % 10;
        return lastTwoDigits is >= 11 and <= 14
            ? "Many"
            : lastDigit switch
            {
                1 => "One",
                2 or 3 or 4 => "Few",
                _ => "Many"
            };
    }

    private bool ValidateOAuthAndOverlayPorts(AppSettings settings)
    {
        settings.Normalize();
        string Localized(string key) => LocalizationService.Get(settings.Language, key);
        if (!AuthService.TryNormalizeRedirectUri(settings.RedirectUri, out var normalizedRedirectUri))
        {
            _dialogs.ShowError(Localized("OAuthRedirectUri"), string.Format(
                CultureInfo.CurrentCulture,
                Localized("RedirectLocalOnlyFormat"),
                AppTwitchDefaults.RedirectUri));
            return false;
        }

        settings.RedirectUri = normalizedRedirectUri;
        var redirectUri = new Uri(normalizedRedirectUri);

        if (redirectUri.Port == settings.OverlayPort)
        {
            _dialogs.ShowError(Localized("OAuthRedirectUri"), Localized("OAuthOverlayPortConflict"));
            StatusText = Localized("OAuthOverlayPortConflict");
            return false;
        }

        return true;
    }

    private string L(string key) => LocalizationService.Get(Settings.Language, key);

    private string UserFacingError(Exception exception, string fallbackKey) =>
        exception is InvalidOperationException or TwitchApiException
            ? exception.Message
            : L(fallbackKey);

    private string UiL(string key) =>
        Application.Current?.Resources[key] as string ?? L(key);

    private void SetGuestAccountState()
    {
        ProfileImageUrl = string.Empty;
        DisplayNameCompact = string.Empty;
        AvatarInitial = "W";
        RaiseStatePropertiesChanged();
    }

    private void OnAuthSessionInvalidated(object? sender, EventArgs e)
    {
        if (IsShuttingDown || Application.Current is null)
        {
            return;
        }

        TrackBackgroundTask(HandleAuthSessionInvalidatedAsync());
    }

    private async Task HandleAuthSessionInvalidatedAsync()
    {
        await Task.Yield();
        if (IsShuttingDown || _disposeCts.IsCancellationRequested || Application.Current is null)
        {
            return;
        }

        try
        {
            await Task.WhenAll(
                _eventSubClient.StopAsync(),
                _readOnlyChatClient.StopAsync()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Expired Twitch session transport cleanup failed: {ex.GetType().Name}");
        }

        if (IsShuttingDown || _disposeCts.IsCancellationRequested || Application.Current is null)
        {
            return;
        }

        try
        {
            await Application.Current.Dispatcher.InvokeAsync(
                ApplyUnauthenticatedState,
                DispatcherPriority.Send,
                _disposeCts.Token);
            await _moderationCacheService.ClearAsync(_disposeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Expired Twitch session cleanup failed: {ex.GetType().Name}");
        }
    }

    private void ApplyUnauthenticatedState()
    {
        if (_authService.HasAccessToken)
        {
            return;
        }

        _currentUser = null;
        ConnectionMode = ChatConnectionMode.ReadOnly;
        Settings.ConnectionMode = ChatConnectionMode.ReadOnly;
        StopStreamStatusPolling();
        StopPinnedMessagePolling();

        foreach (var channel in Channels)
        {
            channel.IsPrimaryAccountChannel = false;
            channel.IsBroadcaster = false;
            channel.IsModerator = false;
            channel.CanSend = false;
            channel.CanModerate = false;
            channel.ModerationCheckCompleted = false;
            channel.ModerationStatus = string.Empty;
            channel.ModerationCheckError = string.Empty;
            ClearRestrictedModerationData(channel);
            ClearPinnedMessage(channel);
            SetChannelConnectionState(channel, "disconnected");
        }

        _broadcaster = ActiveChannel is null ? null : SessionToUser(ActiveChannel);
        IsConnected = false;
        IsConnecting = false;
        SetGuestAccountState();
        UpdateAccountState("not signed in");
        UpdateChatState("disconnected", L("ChatDisconnected"));
        StatusText = L("TwitchSessionExpired");
        RaiseStatePropertiesChanged();
    }

    private void RaiseStatePropertiesChanged()
    {
        OnPropertyChanged(nameof(IsAccountAuthenticated));
        OnPropertyChanged(nameof(HasActiveChannel));
        OnPropertyChanged(nameof(HasChannels));
        OnPropertyChanged(nameof(IsActiveChatConnected));
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsReadOnlyMode));
        OnPropertyChanged(nameof(CanSendMessages));
        OnPropertyChanged(nameof(HasSendRestriction));
        OnPropertyChanged(nameof(SendRestrictionText));
        OnPropertyChanged(nameof(CanModerate));
        OnPropertyChanged(nameof(CanUseCreatorControls));
        OnPropertyChanged(nameof(ShowMessageComposer));
        OnPropertyChanged(nameof(ShowReadOnlyComposerNotice));
        OnPropertyChanged(nameof(ShowSignInButton));
        OnPropertyChanged(nameof(ShowCancelSignInButton));
        OnPropertyChanged(nameof(SignInButtonText));
        OnPropertyChanged(nameof(ShowConnectButton));
        OnPropertyChanged(nameof(ShowLogoutButton));
        OnPropertyChanged(nameof(ShowReconnectButton));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(AccountDisplayName));
        OnPropertyChanged(nameof(AccountLogin));
        OnPropertyChanged(nameof(ActiveChannelLabel));
        OnPropertyChanged(nameof(SendButtonToolTip));
        OnPropertyChanged(nameof(DisconnectButtonToolTip));
        OnPropertyChanged(nameof(HasProfileImage));
        OnPropertyChanged(nameof(ShowAvatarPlaceholder));
        OnPropertyChanged(nameof(IsSharedChatActive));
        OnPropertyChanged(nameof(SharedChatStatusText));
        RaiseCommandState();
    }

    private void RefreshLocalizedText()
    {
        foreach (var channel in Channels)
        {
            channel.ConnectionStatus = channel.IsConnected
                ? L("ChannelConnected")
                : channel.IsConnecting
                    ? L("ChannelConnecting")
                    : string.IsNullOrWhiteSpace(channel.ConnectionError)
                        ? L("ChannelDisconnected")
                        : L("ChatError");
            if (channel.HasSendRestriction && channel.SendRestrictionType is { } restrictionType)
            {
                channel.SendRestrictionText = L(
                    restrictionType == PunishmentType.Ban ? "SendMessageBanned" : "SendMessageTimedOut");
            }
            if (channel.ModerationCheckCompleted)
            {
                channel.ModerationStatus = GetLocalizedModerationStatus(channel);
            }
            else if (!string.IsNullOrWhiteSpace(channel.ModerationStatus))
            {
                channel.ModerationStatus = L("CheckingModeratorPermissions");
            }

            foreach (var message in channel.Messages.Concat(channel.PendingVisualMessages))
            {
                if (message.IsModerated)
                {
                    message.RefreshModerationText();
                }
                if (message.IsChannelPointsMessage)
                {
                    message.RedemptionSummary = BuildChannelPointsSummary(
                        message,
                        CultureInfo.GetCultureInfo(Settings.Language));
                    message.RewardUserInputLabel = L("ChannelPointsUserInput");
                }
            }
        }
        UpdateAccountState(IsAccountAuthenticated ? "signed in" : "not signed in");
        var activeChatState = ActiveChannel is null
            ? "disconnected"
            : ActiveChannel.IsConnected
                ? "connected"
                : ActiveChannel.IsConnecting
                    ? "connecting"
                    : string.IsNullOrWhiteSpace(ActiveChannel.ConnectionError)
                        ? "disconnected"
                        : "error";
        var activeChatDetail = ActiveChannel?.ConnectionError;
        if (string.Equals(activeChatState, "error", StringComparison.OrdinalIgnoreCase) &&
            !IsAccountAuthenticated)
        {
            activeChatDetail = L("ChatReadRequiresSignIn");
        }
        UpdateChatState(activeChatState, activeChatDetail);
        StreamStatusText = !_hasAuthoritativeStreamStatus
            ? L("StreamStatusUnknown")
            : _isStreamLive ? L("StreamLive") : L("StreamOffline");
        StreamViewerText = _isStreamLive && _streamViewerCount > 0
            ? FormatViewerCount(_streamViewerCount)
            : string.Empty;
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(SendButtonToolTip));
        OnPropertyChanged(nameof(ReadOnlyComposerText));
        OnPropertyChanged(nameof(DisconnectButtonToolTip));
        RaiseStatePropertiesChanged();
    }

    private string GetLocalizedModerationStatus(ChannelSessionViewModel session) =>
        session.ModerationCheckError switch
        {
            "missing_scope" or "http_401" => L("ModerationSignInAgain"),
            "network" => L("ModeratorPermissionsCheckFailed"),
            _ when session.CanModerate => L("ModeratorPermissionsConfirmed"),
            _ when string.IsNullOrWhiteSpace(session.ModerationCheckError) => L("NotChannelModerator"),
            _ => L("ModeratorPermissionsCheckFailed")
        };

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
        SetApplicationFont(Settings.UiFontFamily);
        SetBrush("AppBackground", dark ? "#FF07090F" : "#FFE9EDF4");
        SetBrush("PanelBackground", dark ? "#D0101420" : "#F4F7FBFF");
        SetBrush("PanelAltBackground", dark ? "#B8131824" : "#E8EEF6FF");
        SetBrush("GlassRowBrush", dark ? "#331D2433" : "#DDE8EFF8");
        SetBrush("GlassRowHoverBrush", dark ? "#5A2A3347" : "#F6FAFDFF");
        SetBrush("BorderBrushSoft", dark ? "#2BFFFFFF" : "#6697A1B3");
        SetBrush("BorderBrushBright", dark ? "#55FFFFFF" : "#AA6D7890");
        SetBrush("PrimaryText", dark ? "#F7F8FF" : "#151A25");
        SetBrush("SecondaryText", dark ? "#AEB4C4" : "#4F5A6C");
        SetBrush("MutedText", dark ? "#7F879A" : "#5F6B7D");
        SetBrush("ButtonBackgroundBrush", dark ? "#2AFFFFFF" : "#FFE8EEF7");
        SetBrush("ButtonHoverBrush", dark ? "#42FFFFFF" : "#FFDCE6F5");
        SetBrush("ButtonPressedBrush", dark ? "#24FFFFFF" : "#FFC9D7EA");
        SetBrush("ButtonDisabledBrush", dark ? "#12FFFFFF" : "#FFE3E8F0");
        SetBrush("TextBoxBackgroundBrush", dark ? "#22FFFFFF" : "#FFF8FAFD");
        SetBrush("TextBoxFocusedBrush", dark ? "#33FFFFFF" : "#FFF1F5FA");
        SetBrush("DangerBrush", dark ? "#FF6B7A" : "#C92E44");
        SetBrush("SuccessBrush", dark ? "#6FE7B4" : "#16865D");
        SetBrush("AccentBrush", dark ? "#9F8CFF" : "#2F5AE8");
        SetBrush("PrimaryButtonTextBrush", dark ? "#0B0D12" : "#FFFFFFFF");
        SetBrush("PopupBackgroundBrush", dark ? "#F00C101A" : "#FFFFFFFF");
        SetBrush("PopupBorderBrush", dark ? "#FF2A2A3A" : "#FFB7C1D2");
        SetBrush("ComboItemHoverBrush", dark ? "#332F245E" : "#FFE8EFFB");
        SetBrush("ComboItemSelectedBrush", dark ? "#4A7A5CFF" : "#FF2F5AE8");
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

    private static Brush GetApplicationBrush(string key, Brush fallback) =>
        Application.Current?.Resources[key] as Brush ?? fallback;

    private static void SetApplicationFont(string fontId)
    {
        var source = fontId switch
        {
            "Inter" => "pack://application:,,,/WitherChat;component/Assets/Fonts/#Inter",
            "SegoeUI" => "Segoe UI",
            "Aptos" => "Aptos, Segoe UI Variable, Segoe UI",
            "Bahnschrift" => "Bahnschrift, Segoe UI Variable, Segoe UI",
            "Calibri" => "Calibri, Segoe UI",
            "Candara" => "Candara, Segoe UI",
            "Trebuchet" => "Trebuchet MS, Segoe UI",
            _ => "Segoe UI Variable, Segoe UI"
        };
        Application.Current.Resources["AppFontFamily"] = new FontFamily(source);
    }

    private static void SetBrush(string key, string color)
    {
        Application.Current.Resources[key] = CreateFrozenBrush(color);
    }

    private static void SetAccentBrush(bool dark)
    {
        if (!dark)
        {
            Application.Current.Resources["AccentGradientBrush"] = CreateFrozenBrush("#FF2F5AE8");
            return;
        }

        var brush = new LinearGradientBrush
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
        brush.Freeze();
        Application.Current.Resources["AccentGradientBrush"] = brush;
    }

    private static void CopySettings(AppSettings source, AppSettings target)
    {
        source.Normalize();
        target.UseCustomClientId = source.UseCustomClientId;
        target.ClientId = source.ClientId;
        target.RedirectUri = source.RedirectUri;
        target.ConnectionMode = source.ConnectionMode;
        target.LastReadOnlyChannel = source.LastReadOnlyChannel;
        target.SavedChannelLogins = source.SavedChannelLogins.ToList();
        target.LastActiveChannelLogin = source.LastActiveChannelLogin;
        target.ChannelSettingsMigrationVersion = source.ChannelSettingsMigrationVersion;
        target.FontSize = source.FontSize;
        target.MessageLimit = source.MessageLimit;
        target.ShowTimestamps = source.ShowTimestamps;
        target.EnableTwitchEmotes = source.EnableTwitchEmotes;
        target.EnableBttvEmotes = source.EnableBttvEmotes;
        target.EnableSevenTvEmotes = source.EnableSevenTvEmotes;
        target.ShowChannelPointRedemptions = source.ShowChannelPointRedemptions;
        target.MessageVisualTheme = source.MessageVisualTheme;
        target.EnableBadges = source.EnableBadges;
        target.Theme = source.Theme;
        target.UiFontFamily = source.UiFontFamily;
        target.Language = source.Language;
        target.WindowControlsPosition = source.WindowControlsPosition;
        target.WindowControlsStyle = source.WindowControlsStyle;
        target.AlwaysOnTop = source.AlwaysOnTop;
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
        target.OverlayTextOutline = source.OverlayTextOutline;
        target.OverlayDarkBackground = source.OverlayDarkBackground;
        target.OverlayBackgroundOpacity = source.OverlayBackgroundOpacity;
        target.OverlayAlign = source.OverlayAlign;
        target.EnableChatLogging = source.EnableChatLogging;
        target.ChatLogsFolder = source.ChatLogsFolder;
        target.SaveChatLogTxt = source.SaveChatLogTxt;
        target.LogChatBadges = source.LogChatBadges;
        target.LogChannelPointRedemptions = source.LogChannelPointRedemptions;
        target.MaxLogViewerMessages = source.MaxLogViewerMessages;
    }

    private static bool HasChatLogSettingsChanged(AppSettings previous, AppSettings current)
    {
        return previous.EnableChatLogging != current.EnableChatLogging ||
               !string.Equals(previous.ChatLogsFolder, current.ChatLogsFolder, StringComparison.OrdinalIgnoreCase) ||
               previous.SaveChatLogTxt != current.SaveChatLogTxt ||
               previous.LogChatBadges != current.LogChatBadges ||
               previous.LogChannelPointRedemptions != current.LogChannelPointRedemptions;
    }

    private void RaiseCommandState()
    {
        (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SignInCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelSignInCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ReconnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenChatLogsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenModerationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (LogoutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SendMessageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddChannelCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RemoveChannelCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private sealed record PendingChatMessage(
        string ChannelLogin,
        bool PrimaryChannel,
        ChatMessageModel Message,
        bool LogMessage);

    private sealed record CachedUserProfile(
        TwitchUser User,
        DateTimeOffset FetchedAt,
        DateTimeOffset LastAccessAt);

    private sealed class PendingChannelPointsMetadata
    {
        public ChannelPointsMetadataSnapshot? ChatMetadata { get; set; }
        public ChannelPointsMetadataSnapshot? FullRedemption { get; set; }
    }

    private sealed record ChannelPointsMetadataSnapshot(
        string MessageType,
        string CustomRewardId,
        string RedemptionId,
        string RewardId,
        string RewardTitle,
        int? RewardCost,
        string RewardUserInput,
        string RewardType)
    {
        public static ChannelPointsMetadataSnapshot From(ChatMessageModel message) => new(
            message.MessageType,
            message.CustomRewardId,
            message.RedemptionId,
            message.RewardId,
            message.RewardTitle,
            message.RewardCost,
            message.RewardUserInput,
            message.RewardType);
    }
}
