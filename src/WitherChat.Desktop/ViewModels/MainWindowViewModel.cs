using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WitherChat.Core;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Platforms;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private const int MaximumBatchSize = 96;
    private const int MaximumPendingMessages = 20_000;
    private const int LogPresentationBatchSize = 80;
    private static readonly TimeSpan SessionValidationInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan SessionValidationRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinimumDonationDisplayDuration = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan MaximumDonationDisplayDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DonationHistoryRefreshTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MessageBatchTimeBudget = TimeSpan.FromMilliseconds(6);
    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#57E3B0"));
    private static readonly IBrush ConnectingBrush = new SolidColorBrush(Color.Parse("#F6C85F"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF5C70"));
    private static readonly IBrush DisconnectedBrush = new SolidColorBrush(Color.Parse("#7E899F"));

    private readonly IWitherChatClient _chatClient;
    private readonly TwitchAuthService _authService;
    private readonly TwitchAuthSessionStore _authSessionStore;
    private readonly TwitchChatApiClient _chatApiClient;
    private readonly TwitchEventSubClient _eventSubClient;
    private readonly YouTubeAuthService? _youTubeAuthService;
    private readonly YouTubeAuthSessionStore? _youTubeAuthSessionStore;
    private readonly YouTubeLiveChatClient? _youTubeLiveChatClient;
    private readonly DonationAlertsAuthService? _donationAlertsAuthService;
    private readonly DonationAlertsAuthSessionStore? _donationAlertsAuthSessionStore;
    private readonly DonationAlertsClient? _donationAlertsClient;
    private readonly IDonationAlertsObsController? _donationAlertsObsController;
    private readonly DonationPlaybackCoordinator? _donationPlaybackCoordinator;
    private readonly SettingsStore _settingsStore;
    private readonly ChatLogWriter _chatLogWriter;
    private readonly string _defaultChatLogDirectory;
    private readonly ObsOverlayServer _obsOverlayServer;
    private readonly ModerationCacheStore _moderationCacheStore;
    private readonly StreamMomentStore? _streamMomentStore;
    private readonly ChatImageCache _imageCache = new();
    private readonly ThirdPartyEmoteCatalogService _thirdPartyEmoteCatalogService = new();
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, TwitchBadgeDefinition>> _badgeCatalogs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ThirdPartyEmote>> _thirdPartyCatalogs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _thirdPartyCatalogBroadcasterIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _thirdPartyRetryAfter =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _thirdPartyFailureCounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _thirdPartyRefreshRequested = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingCatalogApplicationChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SharedChatState> _sharedChatStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _channelChatClearedAt =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ConcurrentQueue<ChatMessage> _pendingMessages = new();
    private readonly Queue<DonationAlert> _pendingDonations = new();
    private readonly HashSet<string> _donationHistoryIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _liveDonationIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _streamEventIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _seenChatters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<SuspiciousMessageSample>> _suspiciousMessageSamples =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<Uri?>> _messageProfileImageLoads =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ChatMessageItemViewModel> _deferredMessages = [];
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly SemaphoreSlim _badgeRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _thirdPartyRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _streamRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _pinnedRefreshLock = new(1, 1);
    private readonly object _donationHistoryRefreshGate = new();
    private readonly object _settingsSaveTasksLock = new();
    private readonly HashSet<Task> _settingsSaveTasks = [];
    private readonly DispatcherTimer _messageBatchTimer;
    private readonly DispatcherTimer _sessionValidationTimer;
    private readonly DispatcherTimer _streamStatusTimer;
    private readonly DispatcherTimer _pinnedMessageTimer;
    private readonly DispatcherTimer _filterRefreshTimer;
    private readonly DispatcherTimer _donationDisplayTimer;
    private CancellationTokenSource? _authorizationCancellation;
    private CancellationTokenSource? _youTubeAuthorizationCancellation;
    private CancellationTokenSource? _donationAlertsAuthorizationCancellation;
    private Task? _donationHistoryRefreshTask;
    private bool _donationHistoryRefreshRequested;
    private long _donationHistoryRefreshGeneration;
    private CancellationTokenSource? _channelSearchCancellation;
    private CancellationTokenSource? _followedChannelsCancellation;
    private string _validatedChannelDraft = string.Empty;
    private string _validatedConnectPanelChannel = string.Empty;
    private ChannelSearchResult? _validatedChannelDraftResult;
    private ChannelSearchResult? _validatedConnectPanelResult;
    private IReadOnlyList<ChannelSearchResultViewModel> _allFollowedChannels = [];
    private TaskCompletionSource? _authorizationCompletion;
    private TaskCompletionSource? _youTubeAuthorizationCompletion;
    private TaskCompletionSource? _donationAlertsAuthorizationCompletion;
    private TwitchAuthSession? _authSession;
    private long _moderationContextGeneration;
    private long _authenticationGeneration;
    private YouTubeAuthSession? _youTubeAuthSession;
    private string _youTubeModerationLiveChatId = string.Empty;
    private DonationAlertsAuthSession? _donationAlertsAuthSession;
    private WitherChatSettings? _settingsOpenSnapshot;
    private bool _suppressSettingsPersistence;
    private string _deferredPinnedMessageAuthor = string.Empty;
    private string _deferredPinnedMessageText = string.Empty;
    private string _normalizedMessageSearch = string.Empty;
    private string _normalizedUserFilter = string.Empty;
    private string _moderationBroadcasterId = string.Empty;
    private bool _disposed;
    private bool _isMessageBatchProcessingSuspended;
    private bool _sessionValidationInProgress;
    private int _savedChannelStatusRefreshInProgress;
    private int _clipCreationGate;
    private bool _donationAlertsHistoryPermissionRequired;
    private DateTimeOffset _donationDisplayEndsAtUtc;
    private TimeSpan _donationDisplayDuration;
    private bool _showInitialMediaLoading = true;
    private bool _hasCompletedOnboarding;
    private SettingsSection? _contextTutorialOriginalSettingsSection;
    private int? _contextTutorialOriginalModerationSection;
    private int? _contextTutorialOriginalModerationPlatform;
    private int _pendingMessageCount;
#if DEBUG
    private bool _suppressLanguageSave;
#endif

    public MainWindowViewModel(
        IWitherChatClient chatClient,
        TwitchAuthService authService,
        TwitchAuthSessionStore authSessionStore,
        TwitchChatApiClient chatApiClient,
        TwitchEventSubClient eventSubClient,
        SettingsStore settingsStore,
        ChatLogWriter chatLogWriter,
        ObsOverlayServer obsOverlayServer,
        ModerationCacheStore moderationCacheStore,
        WitherChatSettings settings,
        IPlatformDescriptor platform,
        YouTubeAuthService? youTubeAuthService = null,
        YouTubeAuthSessionStore? youTubeAuthSessionStore = null,
        YouTubeLiveChatClient? youTubeLiveChatClient = null,
        DonationAlertsAuthService? donationAlertsAuthService = null,
        DonationAlertsAuthSessionStore? donationAlertsAuthSessionStore = null,
        DonationAlertsClient? donationAlertsClient = null,
        IDonationAlertsObsController? donationAlertsObsController = null,
        StreamMomentStore? streamMomentStore = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(platform);
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _authSessionStore = authSessionStore ?? throw new ArgumentNullException(nameof(authSessionStore));
        _chatApiClient = chatApiClient ?? throw new ArgumentNullException(nameof(chatApiClient));
        _eventSubClient = eventSubClient ?? throw new ArgumentNullException(nameof(eventSubClient));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _chatLogWriter = chatLogWriter ?? throw new ArgumentNullException(nameof(chatLogWriter));
        _chatLogWriter.StatusChanged += OnChatLogWriterStatusChanged;
        _defaultChatLogDirectory = chatLogWriter.LogDirectory;
        _obsOverlayServer = obsOverlayServer ?? throw new ArgumentNullException(nameof(obsOverlayServer));
        _moderationCacheStore = moderationCacheStore ?? throw new ArgumentNullException(nameof(moderationCacheStore));
        _youTubeAuthService = youTubeAuthService;
        _youTubeAuthSessionStore = youTubeAuthSessionStore;
        _youTubeLiveChatClient = youTubeLiveChatClient;
        _donationAlertsAuthService = donationAlertsAuthService;
        _donationAlertsAuthSessionStore = donationAlertsAuthSessionStore;
        _donationAlertsClient = donationAlertsClient;
        _donationAlertsObsController = donationAlertsObsController;
        _streamMomentStore = streamMomentStore;
        if (_streamMomentStore is not null)
        {
            Moments.AppendBatch(
                _streamMomentStore.Load()
                    .OrderByDescending(value => value.SavedAtUtc)
                    .Select(value => new StreamMomentItemViewModel(value))
                    .ToArray(),
                5000);
        }
        _channel = settings.Channel;
        var savedChannels = settings.SavedChannels.Count > 0
            ? settings.SavedChannels
            : string.IsNullOrWhiteSpace(settings.Channel) ? [] : [settings.Channel];
        SavedChannels.AppendBatch(
            savedChannels.Select(channel => new ChannelSessionViewModel(channel)).ToArray(),
            3);
        _selectedSavedChannel = SavedChannels.FirstOrDefault(channel =>
            string.Equals(channel.Login, settings.Channel, StringComparison.OrdinalIgnoreCase));
        if (_selectedSavedChannel is not null)
        {
            _selectedSavedChannel.IsActive = true;
        }
        _theme = settings.Theme;
        _language = settings.Language;
        Texts.SetLanguage(settings.Language);
        RefreshLocalizedOptions();
        _alwaysOnTop = settings.AlwaysOnTop;
        _toastNotifications = settings.ToastNotifications;
        _closeToTray = settings.CloseToTray;
        _messageLimit = settings.MessageLimit;
        _viewerCountRefreshIntervalSeconds = settings.ViewerCountRefreshIntervalSeconds;
        _chatFontSize = settings.ChatFontSize;
        _showTimestamps = settings.ShowTimestamps;
        _showBadges = settings.ShowBadges;
        _enableTwitchEmotes = settings.EnableTwitchEmotes;
        _enableBttvEmotes = settings.EnableBttvEmotes;
        _enableSevenTvEmotes = settings.EnableSevenTvEmotes;
        _showChannelPointRedemptions = settings.ShowChannelPointRedemptions;
        _isSplitChatView = settings.SplitChatView;
        _messageVisualTheme = settings.MessageVisualTheme;
        _uiFontFamily = settings.UiFontFamily;
        _windowControlsOnRight = settings.WindowControlsOnRight;
        _windowControlsStyle = settings.WindowControlsStyle;
        _reduceMotion = settings.ReduceMotion;
        _hasCompletedOnboarding = settings.HasCompletedOnboarding;
        _useCustomClientId = settings.UseCustomClientId;
        _clientId = settings.ClientId;
        _redirectUri = settings.RedirectUri;
        _donationAlertsAutoOpenWindow = settings.DonationAlertsAutoOpenWindow;
        _enableObsOverlay = settings.EnableObsOverlay;
        _overlayPort = settings.OverlayPort;
        _overlayMaxMessages = settings.OverlayMaxMessages;
        _overlayFontSize = settings.OverlayFontSize;
        _overlayShowTimestamps = settings.OverlayShowTimestamps;
        _overlayShowBadges = settings.OverlayShowBadges;
        _overlayShowEmotes = settings.OverlayShowEmotes;
        _overlayFadeOutSeconds = settings.OverlayFadeOutSeconds;
        _overlayTextShadow = settings.OverlayTextShadow;
        _overlayTextOutline = settings.OverlayTextOutline;
        _overlayDarkBackground = settings.OverlayDarkBackground;
        _overlayBackgroundOpacity = settings.OverlayBackgroundOpacity;
        _overlayAlign = settings.OverlayAlign;
        _enableChatLogging = settings.EnableChatLogging;
        _saveChatLogTxt = settings.SaveChatLogTxt;
        _logChatBadges = settings.LogChatBadges;
        _logChannelPointRedemptions = settings.LogChannelPointRedemptions;
        _chatLogsFolder = settings.ChatLogsFolder;
        _maxLogViewerMessages = settings.MaxLogViewerMessages;
        ApplyChatLoggingSettings();
        TryApplyOAuthConfiguration();
        PlatformLabel = platform.DisplayName;
        _statusDetail = Texts.InitialStatus;
        _authStatus = _authSessionStore.IsPersistent ? Texts.SessionTemporary : Texts.SecureStorageUnavailable;
        _donationAlertsStatus = Texts.DonationAlertsNotConnected;

        _chatClient.MessageReceived += OnMessageReceived;
        _chatClient.StatusChanged += OnStatusChanged;
        _eventSubClient.MessageReceived += OnMessageReceived;
        _eventSubClient.StreamEventReceived += OnStreamEventReceived;
        _eventSubClient.AutoModMessageHeld += OnAutoModMessageHeld;
        _eventSubClient.AutoModMessageResolved += OnAutoModMessageResolved;
        _eventSubClient.UserBanned += OnEventSubUserBanned;
        _eventSubClient.UserUnbanned += OnEventSubUserUnbanned;
        _eventSubClient.UnbanRequestChanged += OnEventSubUnbanRequestChanged;
        _eventSubClient.SharedChatStateChanged += OnSharedChatStateChanged;
        _eventSubClient.ChatMessageDeleted += OnEventSubChatMessageDeleted;
        _eventSubClient.UserMessagesCleared += OnEventSubUserMessagesCleared;
        _eventSubClient.ChatCleared += OnEventSubChatCleared;
        if (_youTubeLiveChatClient is not null)
        {
            _youTubeLiveChatClient.MessageReceived += OnMessageReceived;
            _youTubeLiveChatClient.StreamEventReceived += OnStreamEventReceived;
            _youTubeLiveChatClient.StatusChanged += OnYouTubeStatusChanged;
            _youTubeLiveChatClient.SessionUpdated += OnYouTubeSessionUpdated;
            _youTubeLiveChatClient.MessageDeleted += OnYouTubeMessageDeleted;
        }
        if (_donationAlertsClient is not null)
        {
            _donationAlertsClient.DonationReceived += OnDonationReceived;
            _donationAlertsClient.StatusChanged += OnDonationAlertsStatusChanged;
        }
        if (_donationAlertsObsController is not null)
        {
            _donationPlaybackCoordinator = new DonationPlaybackCoordinator(
                _donationAlertsObsController,
                () => ReduceMotion);
            _donationPlaybackCoordinator.StateChanged += OnDonationPlaybackStateChanged;
        }
        _imageCache.LoadingStateChanged += OnImageCacheLoadingStateChanged;
        BannedUsers.CollectionChanged += OnModerationItemsChanged;
        UnbanRequests.CollectionChanged += OnModerationItemsChanged;
        PendingAutoModMessages.CollectionChanged += OnModerationItemsChanged;
        _messageBatchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _messageBatchTimer.Tick += OnMessageBatchTimerTick;
        _messageBatchTimer.Start();
        _sessionValidationTimer = new DispatcherTimer
        {
            Interval = SessionValidationInterval
        };
        _sessionValidationTimer.Tick += OnSessionValidationTimerTick;
        _sessionValidationTimer.Start();
        _streamStatusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_viewerCountRefreshIntervalSeconds)
        };
        _streamStatusTimer.Tick += OnStreamStatusTimerTick;
        _streamStatusTimer.Start();
        _pinnedMessageTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _pinnedMessageTimer.Tick += OnPinnedMessageTimerTick;
        _pinnedMessageTimer.Start();
        _filterRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _filterRefreshTimer.Tick += OnFilterRefreshTimerTick;
        _donationDisplayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _donationDisplayTimer.Tick += OnDonationDisplayTimerTick;
    }

    public event EventHandler? MessagesChanged;
    public event EventHandler? ScrollToLatestRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<ValueEventArgs<string>>? ThemeChanged;
    public event EventHandler<ValueEventArgs<string>>? FontFamilyChanged;
    public event EventHandler? LanguageChanged;
    public event EventHandler<ValueEventArgs<Uri>>? OpenUriRequested;
    public event EventHandler<ValueEventArgs<string>>? OpenLogDirectoryRequested;
    public event EventHandler<ValueEventArgs<string>>? CopyTextRequested;
    public event EventHandler? DonationWindowRequested;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Startup must recover from an invalid stored Twitch session.")]
    public async Task InitializeAsync()
    {
        var authenticationGeneration = Volatile.Read(ref _authenticationGeneration);
        _showInitialMediaLoading = true;
        try
        {
            await ApplyOverlaySettingsAsync();
            await InitializeYouTubeAsync().ConfigureAwait(true);
            await InitializeDonationAlertsAsync().ConfigureAwait(true);
            var storedSession = _authSessionStore.Load();
            if (storedSession is not null)
            {
                try
                {
                    var session = await _authService.ValidateAsync(storedSession, _lifetimeCancellation.Token);
                    if (authenticationGeneration != Volatile.Read(ref _authenticationGeneration) || _disposed)
                        throw new AuthenticationContextChangedException(_lifetimeCancellation.Token);
                    ApplyAuthenticatedSession(session);
                    authenticationGeneration = Volatile.Read(ref _authenticationGeneration);
                    _authSessionStore.Save(session);
                    _sessionValidationTimer.Interval = SessionValidationInterval;
                    UpdateAuthenticatedStorageStatus();
                    await RefreshAccountProfileSafeAsync(session);
                    await RefreshBadgeCatalogSafeAsync(session, Channel);
                    await RefreshStreamStatusSafeAsync(session, Channel);
                }
                catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
                {
                    return;
                }
                catch (OperationCanceledException exception)
                {
                    if (_disposed || authenticationGeneration != Volatile.Read(ref _authenticationGeneration)) return;
                    _sessionValidationTimer.Interval = SessionValidationRetryInterval;
                    AuthStatus = Texts.SessionValidationDeferred(AppDiagnostics.GetUserMessage(exception));
                }
                catch (Exception exception) when (IsTerminalSessionFailure(exception))
                {
                    if (_disposed || authenticationGeneration != Volatile.Read(ref _authenticationGeneration)) return;
                    _authSessionStore.Clear();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (_disposed || authenticationGeneration != Volatile.Read(ref _authenticationGeneration)) return;
                    _sessionValidationTimer.Interval = SessionValidationRetryInterval;
                    AuthStatus = Texts.SessionValidationDeferred(AppDiagnostics.GetUserMessage(exception));
                }
            }

            if (!_disposed && IsAccountConnected && string.IsNullOrWhiteSpace(Channel))
            {
                Channel = AccountLogin;
            }

            if (!_disposed)
            {
                await RefreshSavedChannelMetadataSafeAsync().ConfigureAwait(true);
            }

            if (!_disposed)
            {
                foreach (var savedChannel in SavedChannels.Where(channel =>
                             channel.BroadcasterId.Length > 0))
                {
                    await RefreshThirdPartyCatalogSafeAsync(
                        savedChannel.Login,
                        savedChannel.BroadcasterId).ConfigureAwait(true);
                }
            }

            if (!_disposed && _authSession is { } activeSession)
            {
                foreach (var savedChannel in SavedChannels.Where(channel =>
                             !_badgeCatalogs.ContainsKey(channel.Login)))
                {
                    await RefreshBadgeCatalogSafeAsync(activeSession, savedChannel.Login).ConfigureAwait(true);
                }
            }

            if (!_disposed && HasActiveChannel)
            {
                await ConnectAsync();
                foreach (var savedChannel in SavedChannels.Where(item =>
                             !string.Equals(item.Login, Channel, StringComparison.OrdinalIgnoreCase)))
                {
                    await _chatClient.JoinChannelAsync(savedChannel.Login, _lifetimeCancellation.Token);
                }
            }
        }
        finally
        {
            _showInitialMediaLoading = false;
            IsChatMediaLoading = false;
        }
    }

    public LiveMessageCollection<ChatMessageItemViewModel> Messages { get; } = [];
    public LiveMessageCollection<ChatMessageItemViewModel> VisibleMessages { get; } = [];
    public LiveMessageCollection<ChatMessageItemViewModel> TwitchVisibleMessages { get; } = [];
    public LiveMessageCollection<ChatMessageItemViewModel> YouTubeVisibleMessages { get; } = [];
    public LiveMessageCollection<ChatLogFileViewModel> ChatLogFiles { get; } = [];
    public LiveMessageCollection<ChatLogFileViewModel> VisibleChatLogFiles { get; } = [];
    public LiveMessageCollection<string> ChatLogChannels { get; } = [];
    public LiveMessageCollection<ChatLogEntryViewModel> ChatLogEntries { get; } = [];
    public LiveMessageCollection<ChannelSessionViewModel> SavedChannels { get; } = [];
    public LiveMessageCollection<ChannelSearchResultViewModel> ChannelSearchResults { get; } = [];
    public LiveMessageCollection<ChannelSearchResultViewModel> FollowedChannels { get; } = [];
    public LiveMessageCollection<BannedUserViewModel> BannedUsers { get; } = [];
    public LiveMessageCollection<YouTubeChatBanViewModel> YouTubeBans { get; } = [];
    public LiveMessageCollection<UnbanRequestViewModel> UnbanRequests { get; } = [];
    public LiveMessageCollection<HeldAutoModMessageViewModel> PendingAutoModMessages { get; } = [];
    public LiveMessageCollection<RecentUserMessageViewModel> RecentUserMessages { get; } = [];
    public LiveMessageCollection<DonationHistoryItemViewModel> DonationHistory { get; } = [];
    public LiveMessageCollection<StreamEventItemViewModel> StreamEvents { get; } = [];
    public LiveMessageCollection<StreamEventItemViewModel> VisibleStreamEvents { get; } = [];
    public LiveMessageCollection<StreamMomentItemViewModel> Moments { get; } = [];
    public string? MomentStorageDirectory => _streamMomentStore?.DirectoryPath;
    public bool HasMomentsStatus => !string.IsNullOrWhiteSpace(MomentsStatus);
    private IReadOnlyList<ChatLogEntryViewModel> _allChatLogEntries = [];
    public UiText Texts { get; } = new();
    public IReadOnlyList<SelectionOptionViewModel> ThemeOptions { get; private set; } = [];
    public IReadOnlyList<SelectionOptionViewModel> LanguageOptions { get; private set; } = [];
    public IReadOnlyList<SelectionOptionViewModel> FontOptions { get; private set; } = [];
    public IReadOnlyList<SelectionOptionViewModel> WindowControlsPositionOptions { get; private set; } = [];
    public IReadOnlyList<SelectionOptionViewModel> WindowControlsStyleOptions { get; private set; } = [];
    public IReadOnlyList<SelectionOptionViewModel> MessageVisualThemeOptions { get; private set; } = [];
    public IReadOnlyList<SelectionOptionViewModel> OverlayAlignmentOptions { get; private set; } = [];
    public IReadOnlyList<SelectionOptionViewModel> LogRoleOptions { get; private set; } = [];
    public string VersionLabel => Texts.Version + " " + AppVersion.Current;
    public string ProductVersionLabel => "WitherChat " + AppVersion.Current;
    public string ChatLogDirectory => _chatLogWriter.LogDirectory;
    public string OverlayUrl => $"http://localhost:{OverlayPort}/overlay/chat";
    public string BoostyUrl => SupportLinks.Boosty;
    public string DonationAlertsUrl => SupportLinks.DonationAlerts;
    public string TwitchSupportUrl => SupportLinks.Twitch;
    public string YouTubeUrl => SupportLinks.YouTube;
    public string SteamUrl => SupportLinks.Steam;
    public string TelegramUrl => SupportLinks.Telegram;
    public string UsdtTrc20Address => SupportLinks.UsdtTrc20;
    public string UsdtTonAddress => SupportLinks.UsdtTon;
    public string UsdtBscAddress => SupportLinks.UsdtBsc;
    public string ChatEmptyText => "WitherChat · " + VersionLabel;
    public bool HasStreamEvents => StreamEvents.Count > 0;
    public bool HasVisibleStreamEvents => VisibleStreamEvents.Count > 0;
    public bool HasStreamMoments => Moments.Count > 0;
    public bool HasHeaderOverflowActivity => HasStreamEvents || HasStreamMoments;
    public bool IsAllStreamEventsSelected => StreamEventFilter == 0;
    public bool IsTwitchStreamEventsSelected => StreamEventFilter == 1;
    public bool IsYouTubeStreamEventsSelected => StreamEventFilter == 2;
    public bool IsPaidStreamEventsSelected => StreamEventFilter == 3;
    public bool IsSmartChatAllSelected => SelectedChatViewMode == ChatViewMode.All;
    public bool IsSmartChatQuestionsSelected => SelectedChatViewMode == ChatViewMode.Questions;
    public bool IsSmartChatMentionsSelected => SelectedChatViewMode == ChatViewMode.Mentions;
    public bool IsSmartChatPaidSelected => SelectedChatViewMode == ChatViewMode.Paid;
    public bool IsSmartChatFirstSelected => SelectedChatViewMode == ChatViewMode.FirstMessages;
    public bool IsSmartChatSuspiciousSelected => SelectedChatViewMode == ChatViewMode.Suspicious;
    public bool IsSmartChatRolesSelected => SelectedChatViewMode == ChatViewMode.Roles;
    public bool CanApplyProtection => CanModerate && _authSession is not null && !IsProtectionBusy &&
                                      !string.IsNullOrWhiteSpace(Channel);
    public string ProtectionSuppressedLabel => Texts.ProtectionSuppressed(ProtectionSuppressedCount);
    public string PlatformLabel { get; }
    public string HeaderTitle => IsAccountConnected
        ? string.IsNullOrWhiteSpace(AccountDisplayName) ? AccountLogin : AccountDisplayName
        : string.IsNullOrWhiteSpace(Channel)
            ? Texts.Guest
            : string.IsNullOrWhiteSpace(SelectedSavedChannel?.DisplayName)
                ? Channel
                : SelectedSavedChannel.DisplayName;
    public string HeaderSubtitle => IsAccountConnected ? "@" + AccountLogin : Texts.TwitchNotConnected;
    public ChatImageResource? HeaderProfileImageResource => IsAccountConnected
        ? AccountProfileImageResource
        : SelectedSavedChannel?.ProfileImageResource;
    public string ActiveChannelLabel => !IsAccountConnected || string.IsNullOrWhiteSpace(Channel)
        ? string.Empty
        : Texts.CurrentChannel + ": @" + Channel;
    public string SharedChatStatusLabel => _sharedChatStates.TryGetValue(Channel, out var state) && state.IsActive
        ? Texts.SharedChatParticipants(state.ParticipantCount)
        : string.Empty;
    public bool IsSharedChatActive => SharedChatStatusLabel.Length > 0;
    public string AvatarInitial
    {
        get
        {
            var value = IsAccountConnected
                ? string.IsNullOrWhiteSpace(AccountDisplayName) ? AccountLogin : AccountDisplayName
                : Channel;
            return string.IsNullOrWhiteSpace(value) ? "W" : value.Trim()[0].ToString().ToUpperInvariant();
        }
    }
    public string ApiConnectionLabel => IsAccountConnected ? Texts.ApiConnected : Texts.ApiDisconnected;
    public IBrush ApiConnectionBrush => IsAccountConnected ? ConnectedBrush : DisconnectedBrush;
    public string StreamStatusText => !IsStreamStatusKnown
        ? Texts.StreamStatusUnavailable
        : IsStreamLive ? Texts.Live : Texts.Offline;
    public string StreamViewerText => IsStreamStatusKnown && IsStreamLive
        ? Texts.ViewerCount(StreamViewerCount)
        : string.Empty;
    public IBrush StreamIndicatorBrush => !IsStreamStatusKnown
        ? DisconnectedBrush
        : IsStreamLive ? ErrorBrush : DisconnectedBrush;
    public string CloseWindowTip => CloseToTray ? Texts.HideToTray : Texts.ExitWitherChat;
    public string HeaderToggleGlyph => IsHeaderExpanded ? "^" : "v";
    public string HeaderToggleTip => IsHeaderExpanded ? Texts.HideHeader : Texts.ShowHeader;
    public string CompactModeTip => IsCompactMode ? Texts.RestoreFullMode : Texts.CompactMode;
    public bool ShowHeaderPanel => IsHeaderExpanded && !IsCompactMode;
    public bool ShowHeaderToggle => !IsCompactMode;
    public bool ShowComposerPanel => IsComposerExpanded && !IsCompactMode;
    public bool ShowComposerToggle => HasActiveChannel && !IsCompactMode;
    public bool ShowFullChatMetadata => !IsCompactMode;
    public bool ShowFullChatTimestamps => ShowTimestamps && !IsCompactMode;
    public bool ShowFullChatBadges => ShowBadges && !IsCompactMode;
    public bool ShowCompactChatBadges => ShowBadges && IsCompactMode;
    public Thickness ChatLayoutMargin => IsCompactMode
        ? new Thickness(4, 46, 4, 4)
        : new Thickness(16, 54, 16, 16);
    public VerticalAlignment ChatMetadataVerticalAlignment => IsCompactMode
        ? VerticalAlignment.Top
        : VerticalAlignment.Center;
    public double WelcomeCardWidth => IsCompactMode ? 328 : 560;
    public HorizontalAlignment WindowControlsAlignment => WindowControlsOnRight
        ? HorizontalAlignment.Right
        : HorizontalAlignment.Left;
    public int WindowControlsColumn => WindowControlsOnRight ? 1 : 0;
    public int CompactButtonColumn => WindowControlsOnRight ? 0 : 1;
    public FlowDirection WindowControlsFlowDirection => WindowControlsOnRight
        ? FlowDirection.RightToLeft
        : FlowDirection.LeftToRight;
    public Thickness CompactButtonMargin => WindowControlsOnRight
        ? new Thickness(0, 0, 9, 0)
        : new Thickness(9, 0, 0, 0);
    public bool UseMacWindowControls =>
        !string.Equals(WindowControlsStyle, "Windows", StringComparison.OrdinalIgnoreCase);
    public bool UseWindowsWindowControls => !UseMacWindowControls;
    public string WindowMaximizeTip => IsWindowMaximized ? Texts.RestoreWindow : Texts.Maximize;
    public string ComposerToggleTip => IsComposerExpanded ? Texts.Collapse : Texts.Expand;
    public int SelectedSettingsSectionIndex
    {
        get => (int)SelectedSettingsSection;
        set
        {
            if (Enum.IsDefined(typeof(SettingsSection), value))
                SelectedSettingsSection = (SettingsSection)value;
        }
    }
    public bool IsProgramSettingsSelected => SelectedSettingsSection == SettingsSection.Program;
    public bool IsChatSettingsSelected => SelectedSettingsSection == SettingsSection.Chat;
    public bool IsChatLogsSettingsSelected => SelectedSettingsSection == SettingsSection.ChatLogs;
    public bool IsOverlaySettingsSelected => SelectedSettingsSection == SettingsSection.Overlay;
    public bool IsAccountSettingsSelected => SelectedSettingsSection == SettingsSection.Account;
    public bool IsDonateSettingsSelected => SelectedSettingsSection == SettingsSection.Donate;
    public bool IsAdvancedSettingsSelected => SelectedSettingsSection == SettingsSection.Advanced;
    public int OnboardingTotalSteps => IsContextTutorial
        ? TutorialCatalog.GetStepCount(ActiveTutorialTopic)
        : TutorialCatalog.QuickStartStepCount;
    public int OnboardingStepNumber => OnboardingStep + 1;
    public string OnboardingProgressText => $"{OnboardingStepNumber} / {OnboardingTotalSteps}";
    public string OnboardingTitle => IsContextTutorial
        ? Texts.ContextTutorialTitle(ActiveTutorialTopic, OnboardingStep)
        : Texts.OnboardingTitle(OnboardingStep);
    public string OnboardingDescription => IsContextTutorial
        ? Texts.ContextTutorialDescription(ActiveTutorialTopic, OnboardingStep)
        : Texts.OnboardingDescription(OnboardingStep);
    public string OnboardingHint => IsContextTutorial
        ? Texts.ContextTutorialHint(ActiveTutorialTopic, OnboardingStep)
        : Texts.OnboardingHint(OnboardingStep);
    public string OnboardingNextLabel =>
        OnboardingStep == OnboardingTotalSteps - 1
            ? IsContextTutorial ? Texts.ContextTutorialFinish : Texts.OnboardingFinish
            : Texts.OnboardingNext;
    public string TutorialEyebrow => IsContextTutorial
        ? Texts.ContextTutorialEyebrow
        : Texts.OnboardingEyebrow;
    public string TutorialCloseLabel => IsContextTutorial
        ? Texts.Close
        : Texts.OnboardingSkip;
    public bool CanGoBackInOnboarding => OnboardingStep > 0;
    public bool IsContextTutorial => ActiveTutorialTopic != TutorialTopic.QuickStart;
    public bool IsDonationTutorial => ActiveTutorialTopic is
        TutorialTopic.Donations or TutorialTopic.DonationsSetup;
    public bool IsMainWindowTutorial => !IsDonationTutorial;
    public bool ShowMainWindowTutorial => IsOnboardingOpen && IsMainWindowTutorial;
    public bool ShowDonationTutorial => IsOnboardingOpen && IsDonationTutorial;
    public bool IsOnboardingWelcomeStep => !IsContextTutorial && OnboardingStep == 0;
    public bool IsOnboardingHeaderStep => !IsContextTutorial && OnboardingStep == 1;
    public bool IsOnboardingChannelsStep => !IsContextTutorial && OnboardingStep == 2;
    public bool IsOnboardingChatStep => !IsContextTutorial && OnboardingStep == 3;
    public bool IsOnboardingComposerStep => !IsContextTutorial && OnboardingStep == 4;
    public bool IsOnboardingToolsStep => !IsContextTutorial && OnboardingStep == 5;
    public bool IsOnboardingLogsStep => !IsContextTutorial && OnboardingStep == 6;
    public bool IsOnboardingOverlayStep => !IsContextTutorial && OnboardingStep == 7;
    public bool IsOnboardingSettingsStep => !IsContextTutorial && OnboardingStep == 8;
    public bool IsOnboardingCompactStep => !IsContextTutorial && OnboardingStep == 9;
    public bool ShowOnboardingComposerPreview => IsOnboardingComposerStep && !HasActiveChannel;
    public bool ShowOnboardingChannelPreview => IsOnboardingChannelsStep && SavedChannels.Count == 0;
    public bool HasActiveChannel => !string.IsNullOrWhiteSpace(Channel) || IsYouTubeConnected;
    public bool ShowMessageList => HasActiveChannel && !IsMessageRenderingSuspended;
    public bool IsCombinedChatView => !IsSplitChatView;
    public bool HasDualChatSources => !string.IsNullOrWhiteSpace(Channel) && IsYouTubeConnected;
    public bool ShowSplitMessageLists => ShowMessageList && IsSplitChatView && !IsCompactMode && HasDualChatSources;
    public bool ShowCombinedMessageList => ShowMessageList && !ShowSplitMessageLists;
    public bool HasTwitchVisibleMessages => TwitchVisibleMessages.Count > 0;
    public bool HasYouTubeVisibleMessages => YouTubeVisibleMessages.Count > 0;
    public bool ShowChatEmptyState => HasActiveChannel && VisibleMessages.Count == 0 && !ShowSplitMessageLists;
    public bool ShowWelcomeState => !HasActiveChannel;
    public bool ShowFullChatMediaLoading => IsChatMediaLoading && !IsCompactMode;
    public bool ShowCompactChatMediaLoading => IsChatMediaLoading && IsCompactMode;
    public bool ShowMessageComposer => IsAccountConnected && !string.IsNullOrWhiteSpace(Channel);
    public bool ShowReadOnlyComposerNotice => HasActiveChannel && !ShowMessageComposer;
    public string ReadOnlyComposerNotice => IsYouTubeConnected && string.IsNullOrWhiteSpace(Channel)
        ? Texts.YouTubeReadOnlyComposerText
        : Texts.ReadOnlyComposerText;
    public bool HasMessageFilters => !string.IsNullOrWhiteSpace(MessageSearchText) ||
                                     !string.IsNullOrWhiteSpace(UserFilter);
    public SelectionOptionViewModel? SelectedThemeOption
    {
        get => ThemeOptions.FirstOrDefault(option => string.Equals(option.Value, Theme, StringComparison.Ordinal));
        set
        {
            if (value is not null)
            {
                Theme = value.Value;
            }
        }
    }

    public SelectionOptionViewModel? SelectedLanguageOption
    {
        get => LanguageOptions.FirstOrDefault(option => string.Equals(option.Value, Language, StringComparison.Ordinal));
        set
        {
            if (value is not null)
            {
                Language = value.Value;
            }
        }
    }
    public SelectionOptionViewModel? SelectedFontOption
    {
        get => FontOptions.FirstOrDefault(option =>
            string.Equals(option.Value, UiFontFamily, StringComparison.Ordinal));
        set
        {
            if (value is not null)
            {
                UiFontFamily = value.Value;
            }
        }
    }
    public SelectionOptionViewModel? SelectedWindowControlsPositionOption
    {
        get => WindowControlsPositionOptions.FirstOrDefault(option =>
            string.Equals(option.Value, WindowControlsOnRight ? "Right" : "Left", StringComparison.Ordinal));
        set
        {
            if (value is not null)
            {
                WindowControlsOnRight = string.Equals(value.Value, "Right", StringComparison.Ordinal);
            }
        }
    }
    public SelectionOptionViewModel? SelectedWindowControlsStyleOption
    {
        get => WindowControlsStyleOptions.FirstOrDefault(option =>
            string.Equals(option.Value, WindowControlsStyle, StringComparison.Ordinal));
        set
        {
            if (value is not null)
            {
                WindowControlsStyle = value.Value;
            }
        }
    }
    public SelectionOptionViewModel? SelectedMessageVisualThemeOption
    {
        get => MessageVisualThemeOptions.FirstOrDefault(option =>
            string.Equals(option.Value, MessageVisualTheme, StringComparison.Ordinal));
        set
        {
            if (value is not null)
            {
                MessageVisualTheme = value.Value;
            }
        }
    }
    public SelectionOptionViewModel? SelectedOverlayAlignmentOption
    {
        get => OverlayAlignmentOptions.FirstOrDefault(option =>
            string.Equals(option.Value, OverlayAlign, StringComparison.Ordinal));
        set
        {
            if (value is not null)
            {
                OverlayAlign = value.Value;
            }
        }
    }
    public SelectionOptionViewModel? SelectedLogRoleOption
    {
        get => LogRoleOptions.FirstOrDefault(option =>
            string.Equals(option.Value, LogRoleFilter, StringComparison.Ordinal));
        set
        {
            if (value is not null)
            {
                LogRoleFilter = value.Value;
            }
        }
    }
    public string ChatFontSizeText
    {
        get => FormatBoundedNumber(ChatFontSize);
        set
        {
            ChatFontSize = ParseBoundedDouble(value, ChatFontSize, 11, 28);
            OnPropertyChanged();
        }
    }
    public string MessageLimitText
    {
        get => MessageLimit.ToString(CultureInfo.CurrentCulture);
        set
        {
            MessageLimit = ParseBoundedInt(value, MessageLimit, 250, 10_000);
            OnPropertyChanged();
        }
    }
    public string ViewerCountRefreshIntervalText
    {
        get => ViewerCountRefreshIntervalSeconds.ToString(CultureInfo.CurrentCulture);
        set
        {
            ViewerCountRefreshIntervalSeconds = ParseBoundedInt(
                value,
                ViewerCountRefreshIntervalSeconds,
                WitherChatSettings.MinimumViewerCountRefreshIntervalSeconds,
                WitherChatSettings.MaximumViewerCountRefreshIntervalSeconds);
            OnPropertyChanged();
        }
    }
    public string OverlayPortText
    {
        get => OverlayPort.ToString(CultureInfo.CurrentCulture);
        set
        {
            OverlayPort = ParseBoundedInt(value, OverlayPort, 1024, 65535);
            OnPropertyChanged();
        }
    }
    public string OverlayMaxMessagesText
    {
        get => OverlayMaxMessages.ToString(CultureInfo.CurrentCulture);
        set
        {
            OverlayMaxMessages = ParseBoundedInt(value, OverlayMaxMessages, 1, 100);
            OnPropertyChanged();
        }
    }
    public string OverlayFontSizeText
    {
        get => FormatBoundedNumber(OverlayFontSize);
        set
        {
            OverlayFontSize = ParseBoundedDouble(value, OverlayFontSize, 10, 72);
            OnPropertyChanged();
        }
    }
    public string OverlayFadeOutSecondsText
    {
        get => OverlayFadeOutSeconds.ToString(CultureInfo.CurrentCulture);
        set
        {
            OverlayFadeOutSeconds = ParseBoundedInt(value, OverlayFadeOutSeconds, 0, 600);
            OnPropertyChanged();
        }
    }
    public string OverlayBackgroundOpacityText
    {
        get => FormatBoundedNumber(OverlayBackgroundOpacity);
        set
        {
            OverlayBackgroundOpacity = ParseBoundedDouble(value, OverlayBackgroundOpacity, 0, 1);
            OnPropertyChanged();
        }
    }
    public string OverlayBackgroundOpacityPercent =>
        Math.Round(OverlayBackgroundOpacity * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
    private static int ParseBoundedInt(string? value, int current, int minimum, int maximum)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return current;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed))
        {
            return (int)Math.Clamp(parsed, minimum, maximum);
        }

        var unsigned = text.TrimStart('+', '-');
        if (unsigned.Length > 0 && unsigned.All(char.IsDigit))
        {
            return text.StartsWith("-", StringComparison.Ordinal) ? minimum : maximum;
        }

        return current;
    }

    private static double ParseBoundedDouble(
        string? value,
        double current,
        double minimum,
        double maximum)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return current;
        }

        if ((!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) &&
             !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) ||
            double.IsNaN(parsed))
        {
            return current;
        }

        if (double.IsPositiveInfinity(parsed))
        {
            return maximum;
        }
        if (double.IsNegativeInfinity(parsed))
        {
            return minimum;
        }
        return Math.Clamp(parsed, minimum, maximum);
    }

    private static string FormatBoundedNumber(double value) =>
        value.ToString("0.##", CultureInfo.CurrentCulture);

    public string SelectedLogTitle => SelectedLogFile?.Title ?? string.Empty;
    public string SelectedLogMeta => SelectedLogFile is null
        ? string.Empty
        : SelectedLogFile.DateText + " · " + SelectedLogFile.SizeText;
    public bool IsConnected => ConnectionState == ChatConnectionState.Connected;
    public bool IsBusy => ConnectionState is ChatConnectionState.Connecting or ChatConnectionState.Reconnecting;
    public bool CanConnect => !_disposed && !IsBusy && !string.IsNullOrWhiteSpace(Channel);
    public bool CanDisconnect => ConnectionState != ChatConnectionState.Disconnected;
    public bool CanHeaderDisconnect => IsAccountConnected || CanDisconnect;
    public string HeaderDisconnectTip => IsAccountConnected ? Texts.SignOut : Texts.Disconnect;
    public bool IsAccountConnected => _authSession is not null;
    public bool HasTwitchClipPermission => _authSession?.Scopes.Contains(
        TwitchApplication.ClipsScope,
        StringComparer.Ordinal) == true;
    public bool CanCreateTwitchClip => IsAccountConnected &&
                                       !string.IsNullOrWhiteSpace(Channel) &&
                                       IsStreamLive &&
                                       !IsCreatingTwitchClip;
    public bool HasCreatedTwitchClip => CreatedTwitchClipEditUri is not null &&
                                        CreatedTwitchClipShareUri is not null;
    public IBrush TwitchClipNotificationBrush => IsCreatingTwitchClip
        ? ConnectingBrush
        : IsTwitchClipCreationSuccessful ? ConnectedBrush : ErrorBrush;
    public string TwitchClipButtonTip => !IsAccountConnected
        ? Texts.TwitchClipSignInRequired
        : !IsStreamLive
            ? Texts.TwitchClipLiveRequired
            : !HasTwitchClipPermission
                ? Texts.TwitchClipReconnectRequired
                : IsCreatingTwitchClip
                    ? Texts.TwitchClipCreating
                    : Texts.CreateTwitchClip;
    public bool IsYouTubeConnected => _youTubeAuthSession is not null;
    public bool HasYouTubeModerationPermission =>
        YouTubeAuthService.HasModerationScope(_youTubeAuthSession);
    public bool CanModerateYouTube =>
        HasYouTubeModerationPermission && IsYouTubeLiveConnected && _youTubeLiveChatClient is not null;
    public bool RequiresYouTubeModerationReconnect =>
        IsYouTubeConnected && !HasYouTubeModerationPermission && !IsYouTubeAuthorizing;
    public bool CanModerateAny => CanModerate || CanModerateYouTube;
    public bool CanConnectYouTube => _youTubeAuthService is not null &&
                                     _youTubeAuthSessionStore is not null &&
                                     _youTubeLiveChatClient is not null &&
                                     !IsYouTubeAuthorizing &&
                                     !IsYouTubeConnected;
    public string YouTubeAccountLabel => IsYouTubeConnected
        ? YouTubeAccountName
        : Texts.YouTubeNotConnected;
    public string YouTubeChannelUrl => _youTubeAuthSession is { ChannelId.Length: > 0 } session
        ? "https://www.youtube.com/channel/" + session.ChannelId
        : string.Empty;
    public IBrush YouTubeStatusBrush => IsYouTubeLiveConnected
        ? ConnectedBrush
        : IsYouTubeConnecting ? ConnectingBrush : DisconnectedBrush;
    public bool IsDonationAlertsConnected => _donationAlertsAuthSession is not null;
    public bool CanConnectDonationAlerts => _donationAlertsAuthService is not null &&
                                            _donationAlertsAuthSessionStore is not null &&
                                            _donationAlertsClient is not null &&
                                            !IsDonationAlertsAuthorizing &&
                                            !IsDonationAlertsConnected;
    public string DonationAlertsAccountLabel => IsDonationAlertsConnected
        ? Texts.DonationAlertsAccountConnected(DonationAlertsAccountName)
        : Texts.DonationAlertsNotConnected;
    public IBrush DonationAlertsStatusBrush => IsDonationAlertsRealtimeConnected
        ? ConnectedBrush
        : IsDonationAlertsReconnecting || IsDonationAlertsAuthorizing
            ? ConnectingBrush
            : DisconnectedBrush;
    public bool HasCurrentDonation => CurrentDonation is not null;
    public bool IsDonationWaitingForServerStart =>
        DonationPlaybackState == DonationPlaybackState.WaitingForServerStart;
    public string DonationPlaybackStatusText => IsDonationWaitingForServerStart
        ? Texts.DonationWaitingForServerStart
        : string.Empty;
    public bool CanHideDonation => (IsCurrentDonationControlled
                                        ? _donationPlaybackCoordinator?.Snapshot.CanHide == true
                                        : HasCurrentDonation) &&
                                   !IsDonationControlBusy;
    public string CurrentDonationUsername => CurrentDonation is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(CurrentDonation.Username)
            ? Texts.DonationAnonymous
            : CurrentDonation.Username;
    public string CurrentDonationMessage => CurrentDonation is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(CurrentDonation.Message)
            ? Texts.DonationNoMessage
            : CurrentDonation.Message;
    public string CurrentDonationAmountText => CurrentDonation is null
        ? string.Empty
        : CurrentDonation.Amount.ToString("0.##", CultureInfo.CurrentCulture) + " " + CurrentDonation.Currency;
    public string CurrentDonationRemainingLabel =>
        CurrentDonation is not null && IsCurrentDonationControlled
            ? Texts.DonationPlaybackControlledByDonationAlerts
            : Texts.DonationDisplayRemaining(CurrentDonationSecondsRemaining);
    public bool HasPendingDonations => PendingDonationCount > 0;
    public string DonationQueueLabel => PendingDonationCount > 0
        ? Texts.DonationQueue(PendingDonationCount)
        : string.Empty;
    public bool HasDonationHistory => DonationHistory.Count > 0;
    public bool ShowDonationHistoryPanel => IsDonationAlertsConnected ||
                                            HasDonationHistory ||
                                            HasCurrentDonation;
    public bool ShowDonationConnectPanel => !ShowDonationHistoryPanel;
    public bool ShowDonationAlertsStatusDetail =>
        !string.IsNullOrWhiteSpace(DonationAlertsStatus) &&
        !string.Equals(DonationAlertsStatus, Texts.DonationAlertsNotConnected, StringComparison.Ordinal);
    public bool ShowDonationHistoryEmptyState => IsDonationAlertsConnected &&
                                                  !IsDonationHistoryLoading &&
                                                  !HasDonationHistory &&
                                                  !HasDonationHistoryError;
    public bool HasDonationHistoryError => !string.IsNullOrWhiteSpace(DonationHistoryError);
    public bool CanRefreshDonationHistory => IsDonationAlertsConnected &&
                                             _donationAlertsClient is not null;
    public string DonationHistoryCountLabel => Texts.DonationHistoryCount(DonationHistory.Count);
    public bool IsDonationAlertsObsControlConfigured =>
        _donationAlertsObsController is { IsConfigured: true, IsConnected: true };
    public string DonationAlertsObsControlStatus => IsDonationAlertsObsControlConfigured
        ? Texts.DonationAlertsObsControlReady(_donationAlertsObsController?.SourceName ?? string.Empty)
        : Texts.DonationAlertsObsControlMissing;
    public IBrush DonationAlertsObsControlBrush => IsDonationAlertsObsControlConfigured
        ? ConnectedBrush
        : ErrorBrush;
    public bool HasDonationControlError => !string.IsNullOrWhiteSpace(DonationControlError);
    public bool CanAuthorize => !IsAuthorizing && !IsAccountConnected;
    public bool CanWatchChannel =>
        !IsBusy &&
        !IsChannelSearchBusy &&
        IsValidChannelLogin(NormalizeChannel(ConnectPanelChannel)) &&
        string.Equals(
            NormalizeChannel(ConnectPanelChannel),
            _validatedConnectPanelChannel,
            StringComparison.OrdinalIgnoreCase);
    public bool CanSend => IsAccountConnected &&
                           IsConnected &&
                           !IsSending &&
                           !string.IsNullOrWhiteSpace(ComposerText) &&
                           ComposerText.Length <= 500;
    public string AccountLabel => IsAccountConnected ? "@" + AccountLogin : Texts.SignIn;
    public string AccountChannelUrl => string.IsNullOrWhiteSpace(AccountLogin)
        ? string.Empty
        : "https://www.twitch.tv/" + AccountLogin;
    public string WelcomeActionText => IsAccountConnected ? Texts.ChooseChannel : Texts.ConnectTwitch;
    public string ConnectionLabel => ConnectionState switch
    {
        ChatConnectionState.Connected => Texts.ChatConnected,
        ChatConnectionState.Connecting => Texts.Connecting,
        ChatConnectionState.Reconnecting => Texts.Reconnecting,
        ChatConnectionState.Error => Texts.ConnectionError,
        _ => Texts.ChatDisconnected
    };
    public IBrush ConnectionBrush => ConnectionState switch
    {
        ChatConnectionState.Connected => ConnectedBrush,
        ChatConnectionState.Connecting or ChatConnectionState.Reconnecting => ConnectingBrush,
        ChatConnectionState.Error => ErrorBrush,
        _ => DisconnectedBrush
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(HeaderTitle))]
    [NotifyPropertyChangedFor(nameof(HeaderSubtitle))]
    [NotifyPropertyChangedFor(nameof(ActiveChannelLabel))]
    [NotifyPropertyChangedFor(nameof(ModerationChannelLabel))]
    [NotifyPropertyChangedFor(nameof(CanUnbanUsers))]
    [NotifyPropertyChangedFor(nameof(CanUnbanByLogin))]
    [NotifyPropertyChangedFor(nameof(CanBanByLogin))]
    [NotifyCanExecuteChangedFor(nameof(UnbanByLoginCommand))]
    [NotifyCanExecuteChangedFor(nameof(BanByLoginCommand))]
    [NotifyPropertyChangedFor(nameof(AvatarInitial))]
    [NotifyPropertyChangedFor(nameof(HasActiveChannel))]
    [NotifyPropertyChangedFor(nameof(ShowChatEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowWelcomeState))]
    [NotifyPropertyChangedFor(nameof(ShowMessageComposer))]
    [NotifyPropertyChangedFor(nameof(ShowReadOnlyComposerNotice))]
    [NotifyPropertyChangedFor(nameof(ShowPinnedMessageCard))]
    [NotifyPropertyChangedFor(nameof(ShowJumpToLatestButton))]
    [NotifyPropertyChangedFor(nameof(PinnedCardAuthor))]
    [NotifyPropertyChangedFor(nameof(PinnedCardText))]
    [NotifyPropertyChangedFor(nameof(ShowOnboardingComposerPreview))]
    [NotifyPropertyChangedFor(nameof(ShowMessageList))]
    [NotifyPropertyChangedFor(nameof(ShowCombinedMessageList))]
    [NotifyPropertyChangedFor(nameof(ShowSplitMessageLists))]
    [NotifyPropertyChangedFor(nameof(HasDualChatSources))]
    private string _channel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    [NotifyPropertyChangedFor(nameof(CanHeaderDisconnect))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    [NotifyPropertyChangedFor(nameof(ConnectionBrush))]
    private ChatConnectionState _connectionState;

    [ObservableProperty]
    private string _statusDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFullChatMediaLoading))]
    [NotifyPropertyChangedFor(nameof(ShowCompactChatMediaLoading))]
    private bool _isChatMediaLoading;

    [ObservableProperty]
    private string _theme = "Dark";

    [ObservableProperty]
    private string _language = "ru";

    [ObservableProperty]
    private bool _alwaysOnTop;

    [ObservableProperty]
    private bool _toastNotifications = true;

    [ObservableProperty]
    private bool _closeToTray = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageLimitText))]
    private int _messageLimit = 500;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewerCountRefreshIntervalText))]
    private int _viewerCountRefreshIntervalSeconds =
        WitherChatSettings.DefaultViewerCountRefreshIntervalSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChatFontSizeText))]
    private double _chatFontSize = 17;

    [ObservableProperty]
    private bool _showTimestamps = true;

    [ObservableProperty]
    private bool _showBadges = true;

    [ObservableProperty]
    private bool _enableTwitchEmotes = true;

    [ObservableProperty]
    private bool _enableBttvEmotes = true;

    [ObservableProperty]
    private bool _enableSevenTvEmotes = true;

    [ObservableProperty]
    private bool _showChannelPointRedemptions = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCombinedChatView))]
    [NotifyPropertyChangedFor(nameof(ShowCombinedMessageList))]
    [NotifyPropertyChangedFor(nameof(ShowSplitMessageLists))]
    [NotifyPropertyChangedFor(nameof(ShowChatEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowJumpToLatestButton))]
    private bool _isSplitChatView;

    [ObservableProperty]
    private string _messageVisualTheme = "TornBlack";

    [ObservableProperty]
    private string _uiFontFamily = "SegoeUIVariable";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowControlsAlignment))]
    private bool _windowControlsOnRight;

    [ObservableProperty]
    private string _windowControlsStyle = "Mac";

    [ObservableProperty]
    private bool _reduceMotion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowMaximizeTip))]
    private bool _isWindowMaximized;

    [ObservableProperty]
    private bool _useCustomClientId;

    [ObservableProperty]
    private string _clientId = string.Empty;

    [ObservableProperty]
    private string _redirectUri = TwitchApplication.RedirectUri;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnectYouTube))]
    [NotifyPropertyChangedFor(nameof(RequiresYouTubeModerationReconnect))]
    private bool _isYouTubeAuthorizing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YouTubeStatusBrush))]
    private bool _isYouTubeConnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveChannel))]
    [NotifyPropertyChangedFor(nameof(ShowMessageList))]
    [NotifyPropertyChangedFor(nameof(ShowChatEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowWelcomeState))]
    [NotifyPropertyChangedFor(nameof(ShowReadOnlyComposerNotice))]
    [NotifyPropertyChangedFor(nameof(ReadOnlyComposerNotice))]
    [NotifyPropertyChangedFor(nameof(YouTubeStatusBrush))]
    [NotifyPropertyChangedFor(nameof(ShowCombinedMessageList))]
    [NotifyPropertyChangedFor(nameof(ShowSplitMessageLists))]
    [NotifyPropertyChangedFor(nameof(HasDualChatSources))]
    [NotifyPropertyChangedFor(nameof(CanModerateYouTube))]
    [NotifyPropertyChangedFor(nameof(CanModerateAny))]
    private bool _isYouTubeLiveConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YouTubeAccountLabel))]
    private string _youTubeAccountName = string.Empty;

    [ObservableProperty]
    private string _youTubeStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModerationChannelLabel))]
    private string _youTubeBroadcastTitle = string.Empty;

    [ObservableProperty]
    private bool _donationAlertsAutoOpenWindow = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnectDonationAlerts))]
    [NotifyPropertyChangedFor(nameof(DonationAlertsStatusBrush))]
    private bool _isDonationAlertsAuthorizing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DonationAlertsStatusBrush))]
    private bool _isDonationAlertsRealtimeConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DonationAlertsStatusBrush))]
    private bool _isDonationAlertsReconnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DonationAlertsAccountLabel))]
    private string _donationAlertsAccountName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDonationAlertsStatusDetail))]
    private string _donationAlertsStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentDonation))]
    [NotifyPropertyChangedFor(nameof(ShowDonationHistoryPanel))]
    [NotifyPropertyChangedFor(nameof(ShowDonationConnectPanel))]
    [NotifyPropertyChangedFor(nameof(CurrentDonationUsername))]
    [NotifyPropertyChangedFor(nameof(CurrentDonationMessage))]
    [NotifyPropertyChangedFor(nameof(CurrentDonationAmountText))]
    [NotifyPropertyChangedFor(nameof(CurrentDonationRemainingLabel))]
    [NotifyPropertyChangedFor(nameof(CanHideDonation))]
    private DonationAlert? _currentDonation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingDonations))]
    [NotifyPropertyChangedFor(nameof(DonationQueueLabel))]
    private int _pendingDonationCount;

    [ObservableProperty]
    private double _donationDisplayProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentDonationRemainingLabel))]
    private int _currentDonationSecondsRemaining;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanHideDonation))]
    private bool _isDonationControlBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanHideDonation))]
    [NotifyPropertyChangedFor(nameof(IsDonationWaitingForServerStart))]
    [NotifyPropertyChangedFor(nameof(DonationPlaybackStatusText))]
    private DonationPlaybackState _donationPlaybackState = DonationPlaybackState.Idle;

    [ObservableProperty]
    private double _donationPresentationOpacity = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDonationControlError))]
    private string _donationControlError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDonationHistoryEmptyState))]
    [NotifyCanExecuteChangedFor(nameof(RefreshDonationHistoryCommand))]
    private bool _isDonationHistoryLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDonationHistoryError))]
    [NotifyPropertyChangedFor(nameof(ShowDonationHistoryEmptyState))]
    private string _donationHistoryError = string.Empty;

    [ObservableProperty]
    private bool _enableObsOverlay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayUrl))]
    [NotifyPropertyChangedFor(nameof(OverlayPortText))]
    private int _overlayPort = 17655;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayMaxMessagesText))]
    private int _overlayMaxMessages = 12;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayFontSizeText))]
    private double _overlayFontSize = 22;

    [ObservableProperty]
    private bool _overlayShowTimestamps = true;

    [ObservableProperty]
    private bool _overlayShowBadges = true;

    [ObservableProperty]
    private bool _overlayShowEmotes = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayFadeOutSecondsText))]
    private int _overlayFadeOutSeconds;

    [ObservableProperty]
    private bool _overlayTextShadow = true;

    [ObservableProperty]
    private bool _overlayTextOutline = true;

    [ObservableProperty]
    private bool _overlayDarkBackground = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayBackgroundOpacityText))]
    [NotifyPropertyChangedFor(nameof(OverlayBackgroundOpacityPercent))]
    private double _overlayBackgroundOpacity;

    [ObservableProperty]
    private string _overlayStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedOverlayAlignmentOption))]
    private string _overlayAlign = "left";

    [ObservableProperty]
    private bool _enableChatLogging = true;

    [ObservableProperty]
    private bool _saveChatLogTxt = true;

    [ObservableProperty]
    private bool _logChatBadges = true;

    [ObservableProperty]
    private bool _logChannelPointRedemptions = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChatLogDirectory))]
    [NotifyPropertyChangedFor(nameof(ChatLogsFolderDisplay))]
    private string _chatLogsFolder = string.Empty;

    [ObservableProperty]
    private int _maxLogViewerMessages = 3000;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMainWindowTutorial))]
    [NotifyPropertyChangedFor(nameof(ShowDonationTutorial))]
    private bool _isOnboardingOpen;

    [ObservableProperty]
    private int _onboardingStep;

    [ObservableProperty]
    private TutorialTopic _activeTutorialTopic = TutorialTopic.QuickStart;

    [ObservableProperty]
    private bool _isComposerExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChatLogFiles))]
    private bool _isLogViewerOpen;

    [ObservableProperty]
    private bool _isStreamEventsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllStreamEventsSelected))]
    [NotifyPropertyChangedFor(nameof(IsTwitchStreamEventsSelected))]
    [NotifyPropertyChangedFor(nameof(IsYouTubeStreamEventsSelected))]
    [NotifyPropertyChangedFor(nameof(IsPaidStreamEventsSelected))]
    private int _streamEventFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSmartChatAllSelected))]
    [NotifyPropertyChangedFor(nameof(IsSmartChatQuestionsSelected))]
    [NotifyPropertyChangedFor(nameof(IsSmartChatMentionsSelected))]
    [NotifyPropertyChangedFor(nameof(IsSmartChatPaidSelected))]
    [NotifyPropertyChangedFor(nameof(IsSmartChatFirstSelected))]
    [NotifyPropertyChangedFor(nameof(IsSmartChatSuspiciousSelected))]
    [NotifyPropertyChangedFor(nameof(IsSmartChatRolesSelected))]
    private ChatViewMode _selectedChatViewMode;

    [ObservableProperty]
    private bool _isProtectionPanelOpen;

    [ObservableProperty]
    private bool _isMomentsPanelOpen;

    [ObservableProperty]
    private bool _isMomentEditorOpen;

    [ObservableProperty]
    private string _momentNote = string.Empty;

    [ObservableProperty]
    private ChatMessageItemViewModel? _momentTarget;
    private string? _momentsFailureDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMomentsStatus))]
    private string _momentsStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSaveMomentCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteMomentCommand))]
    [NotifyCanExecuteChangedFor(nameof(BeginSaveMomentCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelSaveMomentCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseMomentsPanelCommand))]
    private bool _isMomentsSaving;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyProtection))]
    private bool _isProtectionBusy;

    [ObservableProperty]
    private bool _protectionSlowMode;

    [ObservableProperty]
    private int _protectionSlowSeconds = 10;

    [ObservableProperty]
    private bool _protectionSubscriberMode;

    [ObservableProperty]
    private bool _protectionFollowerMode;

    [ObservableProperty]
    private int _protectionFollowerMinutes = 10;

    [ObservableProperty]
    private bool _isChatDisplayPaused;

    [ObservableProperty]
    private bool _isObsChatSuppressed;

    [ObservableProperty]
    private bool _isClearChatConfirmationOpen;

    [ObservableProperty]
    private string _protectionStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProtectionSuppressedLabel))]
    private int _protectionSuppressedCount;

    [ObservableProperty]
    private string? _selectedLogChannel;

    [ObservableProperty]
    private ChatLogFileViewModel? _selectedLogFile;

    [ObservableProperty]
    private string _logSearchText = string.Empty;

    [ObservableProperty]
    private string _logUserFilter = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLogRoleOption))]
    private string _logRoleFilter = string.Empty;

    [ObservableProperty]
    private bool _isDeleteLogConfirmationOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveChannel))]
    [NotifyPropertyChangedFor(nameof(HeaderTitle))]
    [NotifyPropertyChangedFor(nameof(HeaderProfileImageResource))]
    [NotifyPropertyChangedFor(nameof(AvatarInitial))]
    [NotifyCanExecuteChangedFor(nameof(RemoveChannelCommand))]
    private ChannelSessionViewModel? _selectedSavedChannel;

    [ObservableProperty]
    private bool _isAddingChannel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmAddChannel))]
    private string _channelDraft = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmAddChannel))]
    [NotifyPropertyChangedFor(nameof(CanWatchChannel))]
    private bool _isChannelSearchBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChannelSearchStatus))]
    private string _channelSearchStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChannelOperationError))]
    private string _channelOperationError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveChannel))]
    [NotifyCanExecuteChangedFor(nameof(RemoveChannelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSavedChannelCommand))]
    private bool _isChannelRemovalBusy;

    [ObservableProperty]
    private bool _isChannelEditorOpen;

    [ObservableProperty]
    private bool _isConnectPanelOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualChannelTabSelected))]
    private bool _isFollowedChannelsTabSelected;

    [ObservableProperty]
    private bool _isFollowedChannelsLoading;

    [ObservableProperty]
    private string _followedChannelsSearch = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFollowedChannelsStatus))]
    private string _followedChannelsStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWatchChannel))]
    private string _connectPanelChannel = string.Empty;

    [ObservableProperty]
    private string _connectPanelError = string.Empty;

    [ObservableProperty]
    private bool _isFiltersVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessageFilters))]
    private string _messageSearchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessageFilters))]
    private string _userFilter = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgramSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsChatSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsChatLogsSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsOverlaySettingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsAccountSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsDonateSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsAdvancedSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(SelectedSettingsSectionIndex))]
    private SettingsSection _selectedSettingsSection = SettingsSection.Program;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(HeaderToggleTip))]
    private bool _isHeaderExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFullChatMediaLoading))]
    [NotifyPropertyChangedFor(nameof(ShowCompactChatMediaLoading))]
    [NotifyPropertyChangedFor(nameof(ShowJumpToLatestButton))]
    private bool _isCompactMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMessageList))]
    [NotifyPropertyChangedFor(nameof(ShowCombinedMessageList))]
    [NotifyPropertyChangedFor(nameof(ShowSplitMessageLists))]
    private bool _isMessageRenderingSuspended;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJumpToLatestButton))]
    private bool _isFollowingLatest = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnreadMessages))]
    private int _unreadCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPinnedMessage))]
    [NotifyPropertyChangedFor(nameof(ShowPinnedMessageCard))]
    [NotifyPropertyChangedFor(nameof(PinnedCardAuthor))]
    [NotifyPropertyChangedFor(nameof(PinnedCardText))]
    private string _pinnedMessageText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinnedCardAuthor))]
    private string _pinnedMessageAuthor = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAuthorize))]
    [NotifyPropertyChangedFor(nameof(CanReauthorizeFollowedChannels))]
    private bool _isAuthorizing;

    [ObservableProperty]
    private string _authStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountLabel))]
    [NotifyPropertyChangedFor(nameof(ApiConnectionLabel))]
    [NotifyPropertyChangedFor(nameof(ApiConnectionBrush))]
    [NotifyPropertyChangedFor(nameof(HeaderTitle))]
    [NotifyPropertyChangedFor(nameof(HeaderSubtitle))]
    [NotifyPropertyChangedFor(nameof(HeaderProfileImageResource))]
    [NotifyPropertyChangedFor(nameof(AvatarInitial))]
    [NotifyPropertyChangedFor(nameof(AccountChannelUrl))]
    [NotifyPropertyChangedFor(nameof(CanBanByLogin))]
    [NotifyCanExecuteChangedFor(nameof(BanByLoginCommand))]
    private string _accountLogin = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderTitle))]
    [NotifyPropertyChangedFor(nameof(AvatarInitial))]
    private string _accountDisplayName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderProfileImageResource))]
    private ChatImageResource? _accountProfileImageResource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamStatusText))]
    [NotifyPropertyChangedFor(nameof(StreamViewerText))]
    [NotifyPropertyChangedFor(nameof(StreamIndicatorBrush))]
    private bool _isStreamStatusKnown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamStatusText))]
    [NotifyPropertyChangedFor(nameof(StreamViewerText))]
    [NotifyPropertyChangedFor(nameof(StreamIndicatorBrush))]
    [NotifyPropertyChangedFor(nameof(CanCreateTwitchClip))]
    [NotifyPropertyChangedFor(nameof(TwitchClipButtonTip))]
    [NotifyCanExecuteChangedFor(nameof(CreateTwitchClipCommand))]
    private bool _isStreamLive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateTwitchClip))]
    [NotifyPropertyChangedFor(nameof(TwitchClipButtonTip))]
    [NotifyPropertyChangedFor(nameof(TwitchClipNotificationBrush))]
    [NotifyCanExecuteChangedFor(nameof(CreateTwitchClipCommand))]
    private bool _isCreatingTwitchClip;

    [ObservableProperty]
    private bool _isTwitchClipNotificationVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TwitchClipNotificationBrush))]
    private bool _isTwitchClipCreationSuccessful;

    [ObservableProperty]
    private string _twitchClipStatusTitle = string.Empty;

    [ObservableProperty]
    private string _twitchClipStatusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCreatedTwitchClip))]
    private Uri? _createdTwitchClipEditUri;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCreatedTwitchClip))]
    private Uri? _createdTwitchClipShareUri;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamViewerText))]
    private int _streamViewerCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private string _composerText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private bool _isSending;

    public bool HasUnreadMessages => UnreadCount > 0;
    public bool ShowJumpToLatestButton => HasActiveChannel && !IsFollowingLatest && !ShowSplitMessageLists;
    public bool HasPinnedMessage => PinnedMessageText.Length > 0;
    public bool ShowPinnedMessageCard => HasPinnedMessage || !HasActiveChannel;
    public bool HasChannelSearchResults => ChannelSearchResults.Count > 0;
    public bool HasFollowedChannels => FollowedChannels.Count > 0;
    public bool IsManualChannelTabSelected => !IsFollowedChannelsTabSelected;
    public bool HasFollowedChannelsPermission => _authSession?.Scopes.Contains(
        TwitchApplication.FollowedChannelsScope,
        StringComparer.Ordinal) == true;
    public bool CanReauthorizeFollowedChannels =>
        IsAccountConnected && !HasFollowedChannelsPermission && !IsAuthorizing;
    public bool ShowFollowedChannelsPermissionRequest =>
        IsAccountConnected && !HasFollowedChannelsPermission;
    public bool ShowFollowedChannelsSignInRequest => !IsAccountConnected;
    public bool ShowFollowedChannelsContent =>
        IsAccountConnected && HasFollowedChannelsPermission;
    public bool HasFollowedChannelsStatus => FollowedChannelsStatus.Length > 0;
    public bool ShowNoRecentMessages => RecentUserMessages.Count == 0;
    public string PinnedCardAuthor => HasPinnedMessage ? PinnedMessageAuthor : "WitherChat";
    public string PinnedCardText => HasPinnedMessage ? PinnedMessageText : Texts.PinnedMessagePreview;
    public bool HasChatLogFiles => ChatLogFiles.Count > 0;
    public string ChatLogsFolderDisplay
    {
        get => string.IsNullOrWhiteSpace(ChatLogsFolder) ? ChatLogDirectory : ChatLogsFolder;
        set => ChatLogsFolder = value?.Trim() ?? string.Empty;
    }
    public bool CanRemoveChannel => CanRemoveSavedChannel(SelectedSavedChannel);
    public bool HasChannelOperationError => ChannelOperationError.Length > 0;
    public bool CanAddSavedChannel => SavedChannels.Count < 3;
    public bool CanConfirmAddChannel =>
        CanAddSavedChannel &&
        !IsChannelSearchBusy &&
        IsValidChannelLogin(NormalizeChannel(ChannelDraft)) &&
        string.Equals(
            NormalizeChannel(ChannelDraft),
            _validatedChannelDraft,
            StringComparison.OrdinalIgnoreCase);
    public bool HasChannelSearchStatus => ChannelSearchStatus.Length > 0;
    public string ModerationTargetLabel => ModerationTarget is null
        ? string.Empty
        : ModerationTarget.Message.IsYouTubeMessage
            ? ModerationTarget.UserLabel + " · YouTube"
            : "@" + ModerationTarget.Message.UserLogin;
    public string ModerationConfirmLabel => ModerationDurationMinutes <= 0 ? Texts.ConfirmBan : Texts.ConfirmTimeout;
    public string ModerationDurationLabel => ModerationDurationMinutes <= 0
        ? Texts.PermanentBan
        : Texts.Minutes(ModerationDurationMinutes);
    public bool CanConfirmModeration =>
        ModerationTarget is not null && !IsModerationBusy && CanModerateMessage(ModerationTarget);
    public bool ShowModerationReason => ModerationTarget?.Message.IsYouTubeMessage != true;
    public bool IsTwitchModerationSelected => ModerationPlatform == 0;
    public bool IsYouTubeModerationSelected => ModerationPlatform == 1;
    public bool HasYouTubeBans => YouTubeBans.Count > 0;
    public bool HasModerationPanelStatus => !string.IsNullOrWhiteSpace(ModerationPanelStatus);
    public string ModerationChannelLabel => IsYouTubeModerationSelected
        ? Texts.YouTube + " · " + YouTubeBroadcastTitle
        : string.IsNullOrWhiteSpace(Channel)
            ? Texts.ModerationChannelRequired
            : Texts.CurrentChannel + ": @" + Channel;
    public bool ShowNoAutoModMessages =>
        !IsModerationPanelBusy && !HasModerationPanelStatus && PendingAutoModMessages.Count == 0;
    public bool ShowNoBannedUsers =>
        !IsModerationPanelBusy && !HasModerationPanelStatus && BannedUsers.Count == 0;
    public bool ShowNoUnbanRequests =>
        !IsModerationPanelBusy && !HasModerationPanelStatus && !VisibleUnbanRequests.Any();
    public string EmptyUnbanRequestsLabel => UnbanRequestFilter switch
    {
        1 => Texts.NoApprovedUnbanRequests,
        2 => Texts.NoDeniedUnbanRequests,
        _ => Texts.NoPendingUnbanRequests
    };
    public bool IsAutoModSelected => ModerationPanelSection == 0;
    public bool IsBannedUsersSelected => ModerationPanelSection == 1;
    public bool IsUnbanRequestsSelected => ModerationPanelSection == 2;
    public bool IsPendingUnbanRequestsSelected => UnbanRequestFilter == 0;
    public bool IsApprovedUnbanRequestsSelected => UnbanRequestFilter == 1;
    public bool IsDeniedUnbanRequestsSelected => UnbanRequestFilter == 2;
    public IEnumerable<UnbanRequestViewModel> VisibleUnbanRequests => UnbanRequests.Where(item =>
        UnbanRequestFilter switch
        {
            1 => item.Value.Status == UnbanRequestStatus.Approved,
            2 => item.Value.Status == UnbanRequestStatus.Denied,
            _ => item.Value.Status == UnbanRequestStatus.Pending
        });
    public bool CanUnbanUsers =>
        CanModerate && _authSession is not null && !string.IsNullOrWhiteSpace(Channel) && !IsModerationPanelBusy;
    public bool CanUnbanByLogin =>
        CanUnbanUsers &&
        IsValidChannelLogin(NormalizeChannel(ModerationUserLogin));
    public bool CanBanByLogin =>
        CanUnbanUsers &&
        IsValidChannelLogin(NormalizeChannel(ModerationUserLogin)) &&
        !string.Equals(NormalizeChannel(ModerationUserLogin), AccountLogin, StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBanByLogin))]
    [NotifyPropertyChangedFor(nameof(CanUnbanUsers))]
    [NotifyPropertyChangedFor(nameof(CanUnbanByLogin))]
    [NotifyCanExecuteChangedFor(nameof(UnbanByLoginCommand))]
    [NotifyCanExecuteChangedFor(nameof(BanByLoginCommand))]
    private bool _canModerate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTwitchModerationSelected))]
    [NotifyPropertyChangedFor(nameof(IsYouTubeModerationSelected))]
    [NotifyPropertyChangedFor(nameof(ModerationChannelLabel))]
    private int _moderationPlatform;

    [ObservableProperty]
    private bool _isModerationDialogOpen;

    [ObservableProperty]
    private string _moderationActionError = string.Empty;

    [ObservableProperty]
    private bool _isModerationPanelOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoModSelected))]
    [NotifyPropertyChangedFor(nameof(IsBannedUsersSelected))]
    [NotifyPropertyChangedFor(nameof(IsUnbanRequestsSelected))]
    private int _moderationPanelSection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUnbanUsers))]
    [NotifyPropertyChangedFor(nameof(ShowNoAutoModMessages))]
    [NotifyPropertyChangedFor(nameof(ShowNoBannedUsers))]
    [NotifyPropertyChangedFor(nameof(ShowNoUnbanRequests))]
    [NotifyPropertyChangedFor(nameof(CanUnbanByLogin))]
    [NotifyPropertyChangedFor(nameof(CanBanByLogin))]
    [NotifyCanExecuteChangedFor(nameof(UnbanByLoginCommand))]
    [NotifyCanExecuteChangedFor(nameof(BanByLoginCommand))]
    private bool _isModerationPanelBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModerationPanelStatus))]
    [NotifyPropertyChangedFor(nameof(ShowNoAutoModMessages))]
    [NotifyPropertyChangedFor(nameof(ShowNoBannedUsers))]
    [NotifyPropertyChangedFor(nameof(ShowNoUnbanRequests))]
    private string _moderationPanelStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUnbanByLogin))]
    [NotifyPropertyChangedFor(nameof(CanBanByLogin))]
    [NotifyCanExecuteChangedFor(nameof(UnbanByLoginCommand))]
    [NotifyCanExecuteChangedFor(nameof(BanByLoginCommand))]
    private string _moderationUserLogin = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPendingUnbanRequestsSelected))]
    [NotifyPropertyChangedFor(nameof(IsApprovedUnbanRequestsSelected))]
    [NotifyPropertyChangedFor(nameof(IsDeniedUnbanRequestsSelected))]
    [NotifyPropertyChangedFor(nameof(VisibleUnbanRequests))]
    [NotifyPropertyChangedFor(nameof(ShowNoUnbanRequests))]
    [NotifyPropertyChangedFor(nameof(EmptyUnbanRequestsLabel))]
    private int _unbanRequestFilter;

    [ObservableProperty]
    private bool _isRecentMessagesOpen;

    [ObservableProperty]
    private string _recentMessagesUserLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModerationTargetLabel))]
    [NotifyPropertyChangedFor(nameof(CanConfirmModeration))]
    [NotifyPropertyChangedFor(nameof(ShowModerationReason))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmModerationCommand))]
    private ChatMessageItemViewModel? _moderationTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModerationConfirmLabel))]
    [NotifyPropertyChangedFor(nameof(ModerationDurationLabel))]
    private int _moderationDurationMinutes;

    [ObservableProperty]
    private string _moderationReason = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmModeration))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmModerationCommand))]
    private bool _isModerationBusy;

    [RelayCommand]
    private void OpenConnectPanel()
    {
        IsSettingsOpen = false;
        IsLogViewerOpen = false;
        IsModerationPanelOpen = false;
        IsChannelEditorOpen = false;
        IsFollowedChannelsTabSelected = false;
        ConnectPanelChannel = string.IsNullOrWhiteSpace(Channel)
            ? SavedChannels.FirstOrDefault()?.Login ?? string.Empty
            : Channel;
        ConnectPanelError = string.Empty;
        IsConnectPanelOpen = true;
        _ = SearchChannelsAsync(ConnectPanelChannel, forConnectPanel: true);
    }

    [RelayCommand]
    private void ShowManualChannelTab()
    {
        IsFollowedChannelsTabSelected = false;
        FollowedChannelsStatus = string.Empty;
    }

    [RelayCommand]
    private async Task ShowFollowedChannelsTabAsync()
    {
        IsFollowedChannelsTabSelected = true;
        ConnectPanelError = string.Empty;
        CancelChannelSearch();
        if (ShowFollowedChannelsContent && _allFollowedChannels.Count == 0)
        {
            await RefreshFollowedChannelsAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CloseConnectPanel()
    {
        IsConnectPanelOpen = false;
        ConnectPanelError = string.Empty;
        _validatedConnectPanelChannel = string.Empty;
        _validatedConnectPanelResult = null;
        CancelChannelSearch();
        CancelFollowedChannelsRefresh();
    }

    [RelayCommand]
    private async Task SignInFromConnectPanelAsync()
    {
        if (!CanAuthorize)
        {
            return;
        }

        IsConnectPanelOpen = false;
        await StartAuthorizationAsync();
    }

    [RelayCommand]
    private async Task WatchChannelFromConnectPanelAsync()
    {
        var channel = NormalizeChannel(ConnectPanelChannel);
        if (!IsValidChannelLogin(channel) ||
            !string.Equals(channel, _validatedConnectPanelChannel, StringComparison.OrdinalIgnoreCase))
        {
            ConnectPanelError = IsValidChannelLogin(channel)
                ? Texts.ChannelSearchNoResults
                : Texts.TwitchChannelNameRequired;
            return;
        }

        var isAlreadySaved = SavedChannels.Any(saved =>
            string.Equals(saved.Login, channel, StringComparison.OrdinalIgnoreCase));
        if (!isAlreadySaved && !CanAddSavedChannel)
        {
            ConnectPanelError = Texts.ChannelLimitReached;
            return;
        }

        ConnectPanelError = string.Empty;
        IsConnectPanelOpen = false;
        Channel = channel;
        await ConnectAsync();
    }

    [RelayCommand]
    private async Task SelectFollowedChannelAsync(ChannelSearchResultViewModel? result)
    {
        if (result is null)
        {
            return;
        }

        CancelChannelSearch();
        ConnectPanelChannel = result.Login;
        _validatedConnectPanelChannel = NormalizeChannel(result.Login);
        _validatedConnectPanelResult = result.Value;
        ConnectPanelError = string.Empty;
        OnPropertyChanged(nameof(CanWatchChannel));
        await WatchChannelFromConnectPanelAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var normalizedChannel = NormalizeChannel(Channel);
        var channelMetadata = string.Equals(
                _validatedChannelDraftResult?.BroadcasterLogin,
                normalizedChannel,
                StringComparison.OrdinalIgnoreCase)
            ? _validatedChannelDraftResult
            : string.Equals(
                _validatedConnectPanelResult?.BroadcasterLogin,
                normalizedChannel,
                StringComparison.OrdinalIgnoreCase)
                ? _validatedConnectPanelResult
                : null;
        Channel = normalizedChannel;
        IsChannelEditorOpen = false;
        if (normalizedChannel.Length == 0)
        {
            return;
        }

        try
        {
            var existing = SavedChannels.FirstOrDefault(item =>
                string.Equals(item.Login, normalizedChannel, StringComparison.OrdinalIgnoreCase));
            if (existing is null && SavedChannels.Count >= 3)
            {
                StatusDetail = Texts.ChannelLimitReached;
                return;
            }

            StatusDetail = Texts.SecureConnection;
            if (!_chatClient.Channels.Contains(normalizedChannel, StringComparer.OrdinalIgnoreCase))
            {
                await _chatClient.JoinChannelAsync(normalizedChannel, _lifetimeCancellation.Token).ConfigureAwait(true);
            }
            if (_disposed) return;
            if (existing is null)
            {
                existing = new ChannelSessionViewModel(normalizedChannel);
                SavedChannels.AppendBatch([existing], 3);
                OnPropertyChanged(nameof(CanAddSavedChannel));
                OnPropertyChanged(nameof(CanConfirmAddChannel));
            }
            if (channelMetadata is not null)
            {
                ApplyChannelMetadata(existing, channelMetadata);
            }
            SelectedSavedChannel = existing;
            RebuildVisibleMessages();
            await SaveSettingsSafeAsync().ConfigureAwait(true);
            await RefreshModerationAccessSafeAsync().ConfigureAwait(true);
            await RestartEventSubSafeAsync().ConfigureAwait(true);
            await RefreshPinnedMessageSafeAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed || _lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                ConnectionState = ChatConnectionState.Error;
                StatusDetail = Texts.CouldNotConnect;
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ReconnectAsync()
    {
        var activeChannel = NormalizeChannel(Channel);
        if (activeChannel.Length == 0)
        {
            return;
        }
        try
        {
            StatusDetail = Texts.Reconnecting;
            await _chatClient.DisconnectAsync().ConfigureAwait(true);
            var channels = SavedChannels.Select(item => item.Login).ToArray();
            if (!channels.Contains(activeChannel, StringComparer.OrdinalIgnoreCase))
            {
                channels = [activeChannel, .. channels.Take(2)];
            }
            foreach (var channel in channels)
            {
                await _chatClient.JoinChannelAsync(channel, _lifetimeCancellation.Token).ConfigureAwait(true);
            }
            Channel = activeChannel;
            StatusDetail = Texts.ConnectedTo(activeChannel);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusDetail = Texts.CouldNotConnect + " " + AppDiagnostics.GetUserMessage(exception);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        await _chatClient.DisconnectAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task HeaderDisconnectAsync()
    {
        if (IsAccountConnected)
        {
            SignOut();
            return;
        }
        if (CanDisconnect)
        {
            await _chatClient.DisconnectAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveChannel))]
    private Task RemoveChannelAsync() => RemoveSavedChannelAsync(SelectedSavedChannel);

    [RelayCommand(CanExecute = nameof(CanAuthorize))]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This UI command boundary must convert every authorization failure into a user-visible status.")]
    private Task StartAuthorizationAsync() => AuthorizeAsync(keepConnectPanelOpen: false, loadFollowedChannels: false);

    [RelayCommand(CanExecute = nameof(CanReauthorizeFollowedChannels))]
    private Task ReauthorizeFollowedChannelsAsync() =>
        AuthorizeAsync(keepConnectPanelOpen: true, loadFollowedChannels: true);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This UI command boundary must convert every authorization failure into a user-visible status.")]
    private async Task AuthorizeAsync(bool keepConnectPanelOpen, bool loadFollowedChannels)
    {
        var connectPanelWasOpen = IsConnectPanelOpen;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _authorizationCompletion = completion;
        CancelAuthorizationCore();
        var cancellation = new CancellationTokenSource();
        _authorizationCancellation = cancellation;
        IsAuthorizing = true;
        AuthStatus = Texts.OpeningTwitchLogin;

        try
        {
            var session = await _authService.SignInWithBrowserAsync(
                uri =>
                {
                    AuthStatus = Texts.ConfirmSignIn;
                    OpenUriRequested?.Invoke(this, new ValueEventArgs<Uri>(uri));
                },
                string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase),
                cancellation.Token);
            ApplyAuthenticatedSession(session);
            _authSessionStore.Save(session);
            UpdateAuthenticatedStorageStatus();
            await RefreshAccountProfileSafeAsync(session);
            await RefreshBadgeCatalogSafeAsync(session, Channel);
            await RefreshStreamStatusSafeAsync(session, Channel);
            await RefreshModerationAccessSafeAsync();
            await RestartEventSubSafeAsync();
            NotifyFollowedChannelAccessChanged();
            if (loadFollowedChannels && HasFollowedChannelsPermission)
            {
                await RefreshFollowedChannelsAsync().ConfigureAwait(true);
            }
            if (string.IsNullOrWhiteSpace(Channel))
            {
                Channel = NormalizeChannel(session.Login);
                await ConnectAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            AuthStatus = Texts.SignInCanceled;
        }
        catch (Exception exception)
        {
            AuthStatus = Texts.SignInFailed(AppDiagnostics.GetUserMessage(exception));
        }
        finally
        {
            if (ReferenceEquals(_authorizationCancellation, cancellation))
            {
                _authorizationCancellation = null;
            }

            if (ReferenceEquals(_authorizationCompletion, completion))
            {
                _authorizationCompletion = null;
            }

            completion.TrySetResult();
            cancellation.Dispose();
            IsAuthorizing = false;
            if (keepConnectPanelOpen && connectPanelWasOpen)
            {
                IsConnectPanelOpen = true;
                IsFollowedChannelsTabSelected = true;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnectYouTube))]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This UI command boundary must convert every Google authorization failure into a user-visible status.")]
    private async Task ConnectYouTubeAsync()
        => await AuthorizeYouTubeAsync().ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(RequiresYouTubeModerationReconnect))]
    private async Task EnableYouTubeModerationAsync()
        => await AuthorizeYouTubeAsync().ConfigureAwait(true);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This UI command boundary must convert every Google authorization failure into a user-visible status.")]
    private async Task AuthorizeYouTubeAsync()
    {
        if (_youTubeAuthService is null || _youTubeAuthSessionStore is null || _youTubeLiveChatClient is null)
        {
            return;
        }

        _youTubeAuthorizationCancellation?.Cancel();
        _youTubeAuthorizationCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _youTubeAuthorizationCancellation = cancellation;
        _youTubeAuthorizationCompletion = completion;
        IsYouTubeAuthorizing = true;
        YouTubeStatus = Texts.YouTubeOpeningLogin;
        try
        {
            _youTubeAuthService.Configure(YouTubeApplication.ClientId);
            var session = await _youTubeAuthService.SignInWithBrowserAsync(
                uri => OpenUriRequested?.Invoke(this, new ValueEventArgs<Uri>(uri)),
                string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase),
                cancellation.Token);
            _youTubeAuthSessionStore.Save(session);
            ApplyYouTubeSession(session);
            await _youTubeLiveChatClient.StartAsync(session, _lifetimeCancellation.Token).ConfigureAwait(true);
            YouTubeStatus = Texts.YouTubeWaitingForBroadcast;
            await SaveSettingsSafeAsync(CreateSettingsSnapshot()).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            YouTubeStatus = Texts.SignInCanceled;
        }
        catch (Exception exception)
        {
            YouTubeStatus = Texts.SignInFailed(GetYouTubeErrorMessage(exception));
        }
        finally
        {
            if (ReferenceEquals(_youTubeAuthorizationCancellation, cancellation))
            {
                _youTubeAuthorizationCancellation = null;
            }
            if (ReferenceEquals(_youTubeAuthorizationCompletion, completion))
            {
                _youTubeAuthorizationCompletion = null;
            }
            completion.TrySetResult();
            cancellation.Dispose();
            IsYouTubeAuthorizing = false;
            ConnectYouTubeCommand.NotifyCanExecuteChanged();
            EnableYouTubeModerationCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task DisconnectYouTubeAsync()
    {
        _youTubeAuthorizationCancellation?.Cancel();
        if (_youTubeLiveChatClient is not null)
        {
            await _youTubeLiveChatClient.StopAsync().ConfigureAwait(true);
        }
        _youTubeAuthSession = null;
        _youTubeModerationLiveChatId = string.Empty;
        YouTubeBans.Clear();
        OnPropertyChanged(nameof(HasYouTubeBans));
        _youTubeAuthSessionStore?.Clear();
        YouTubeAccountName = string.Empty;
        YouTubeBroadcastTitle = string.Empty;
        YouTubeStatus = Texts.YouTubeNotConnected;
        IsYouTubeLiveConnected = false;
        IsYouTubeConnecting = false;
        OnPropertyChanged(nameof(IsYouTubeConnected));
        OnPropertyChanged(nameof(YouTubeAccountLabel));
        OnPropertyChanged(nameof(YouTubeChannelUrl));
        OnPropertyChanged(nameof(CanConnectYouTube));
        NotifyYouTubeAccountLayoutChanged();
        ConnectYouTubeCommand.NotifyCanExecuteChanged();
        EnableYouTubeModerationCommand.NotifyCanExecuteChanged();
        NotifyYouTubeModerationStateChanged();
        RebuildVisibleMessages();
    }

    [RelayCommand]
    private void CancelYouTubeAuthorization()
    {
        _youTubeAuthorizationCancellation?.Cancel();
        YouTubeStatus = Texts.SignInCanceled;
    }

    [RelayCommand(CanExecute = nameof(CanConnectDonationAlerts))]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This UI command boundary converts authorization failures into a visible status.")]
    private async Task ConnectDonationAlertsAsync()
    {
        if (_donationAlertsAuthService is null || _donationAlertsAuthSessionStore is null ||
            _donationAlertsClient is null)
        {
            return;
        }

        _donationAlertsAuthorizationCancellation?.Cancel();
        _donationAlertsAuthorizationCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _donationAlertsAuthorizationCancellation = cancellation;
        _donationAlertsAuthorizationCompletion = completion;
        IsDonationAlertsAuthorizing = true;
        DonationAlertsStatus = Texts.DonationAlertsOpeningLogin;
        try
        {
            _donationAlertsAuthService.Configure(DonationAlertsApplication.ClientId);
            var session = await _donationAlertsAuthService.SignInWithBrowserAsync(
                uri => OpenUriRequested?.Invoke(this, new ValueEventArgs<Uri>(uri)),
                string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase),
                cancellation.Token).ConfigureAwait(true);
            _donationAlertsAuthSessionStore.Save(session);
            _donationAlertsHistoryPermissionRequired = false;
            ApplyDonationAlertsSession(session);
            DonationAlertsStatus = Texts.DonationAlertsConnecting;
            await TryConnectDonationAlertsControlAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
            await _donationAlertsClient.StartAsync(session, _lifetimeCancellation.Token).ConfigureAwait(true);
            await RefreshDonationHistoryAsync().ConfigureAwait(true);
            await SaveSettingsSafeAsync(CreateSettingsSnapshot()).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            DonationAlertsStatus = Texts.SignInCanceled;
        }
        catch (Exception exception)
        {
            DonationAlertsStatus = Texts.SignInFailed(AppDiagnostics.GetUserMessage(exception));
        }
        finally
        {
            if (ReferenceEquals(_donationAlertsAuthorizationCancellation, cancellation))
            {
                _donationAlertsAuthorizationCancellation = null;
            }
            if (ReferenceEquals(_donationAlertsAuthorizationCompletion, completion))
            {
                _donationAlertsAuthorizationCompletion = null;
            }
            completion.TrySetResult();
            cancellation.Dispose();
            IsDonationAlertsAuthorizing = false;
            ConnectDonationAlertsCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task DisconnectDonationAlertsAsync()
    {
        _donationAlertsAuthorizationCancellation?.Cancel();
        if (_donationAlertsClient is not null)
        {
            await _donationAlertsClient.StopAsync().ConfigureAwait(true);
        }
        _donationAlertsAuthSession = null;
        _donationAlertsAuthSessionStore?.Clear();
        _donationAlertsHistoryPermissionRequired = false;
        DonationAlertsAccountName = string.Empty;
        DonationAlertsStatus = Texts.DonationAlertsNotConnected;
        IsDonationAlertsRealtimeConnected = false;
        IsDonationAlertsReconnecting = false;
        OnPropertyChanged(nameof(IsDonationAlertsConnected));
        OnPropertyChanged(nameof(DonationAlertsAccountLabel));
        OnPropertyChanged(nameof(CanConnectDonationAlerts));
        ClearDonationHistory();
        NotifyDonationHistoryStateChanged();
        ConnectDonationAlertsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CancelDonationAlertsAuthorization()
    {
        _donationAlertsAuthorizationCancellation?.Cancel();
        DonationAlertsStatus = Texts.SignInCanceled;
    }

    [RelayCommand]
    private void OpenDonationWindow() => DonationWindowRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void TestDonation() => EnqueueDonation(new DonationAlert(
        "test-" + Guid.NewGuid().ToString("N"),
        Texts.DonationTestSender,
        Texts.DonationTestMessage,
        500,
        "RUB",
        DateTimeOffset.UtcNow));

    [RelayCommand]
    private async Task SkipDonationAsync()
    {
        var donation = CurrentDonation;
        if (donation is null || IsDonationControlBusy || !CanHideDonation)
        {
            return;
        }

        IsDonationControlBusy = true;
        DonationControlError = string.Empty;
        try
        {
            if (IsCurrentDonationControlled && _donationPlaybackCoordinator is not null)
            {
                _ = await _donationPlaybackCoordinator.HideCurrentAsync(
                    _lifetimeCancellation.Token).ConfigureAwait(true);
            }
            else
            {
                AdvanceDonationDisplay();
            }
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception) when (IsDonationControlException(exception))
        {
            DonationControlError = Texts.DonationAlertsObsControlFailed(AppDiagnostics.GetUserMessage(exception));
        }
        finally
        {
            IsDonationControlBusy = false;
            NotifyDonationAlertsObsControlStateChanged();
        }
    }

    [RelayCommand]
    private void ClearDonationQueue()
    {
        _donationPlaybackCoordinator?.ClearPending();
        _pendingDonations.Clear();
        PendingDonationCount = 0;
    }

    [RelayCommand(CanExecute = nameof(CanRefreshDonationHistory), AllowConcurrentExecutions = true)]
    private Task RefreshDonationHistoryAsync()
    {
        if (!CanRefreshDonationHistory)
        {
            return Task.CompletedTask;
        }

        lock (_donationHistoryRefreshGate)
        {
            _donationHistoryRefreshRequested = true;
            if (_donationHistoryRefreshTask is null || _donationHistoryRefreshTask.IsCompleted)
            {
                var generation = ++_donationHistoryRefreshGeneration;
                _donationHistoryRefreshTask = RunDonationHistoryRefreshLoopAsync(generation);
            }
            return _donationHistoryRefreshTask;
        }
    }

    private async Task RunDonationHistoryRefreshLoopAsync(long generation)
    {
        await Task.Yield();
        IsDonationHistoryLoading = true;
        DonationHistoryError = string.Empty;
        try
        {
            while (!_lifetimeCancellation.IsCancellationRequested)
            {
                lock (_donationHistoryRefreshGate)
                {
                    _donationHistoryRefreshRequested = false;
                }

                await RefreshDonationHistoryOnceAsync().ConfigureAwait(true);

                lock (_donationHistoryRefreshGate)
                {
                    if (_donationHistoryRefreshRequested)
                    {
                        continue;
                    }
                    if (_donationHistoryRefreshGeneration == generation)
                    {
                        _donationHistoryRefreshTask = null;
                    }
                    break;
                }
            }
        }
        finally
        {
            var ownsRefreshState = false;
            lock (_donationHistoryRefreshGate)
            {
                if (_donationHistoryRefreshGeneration == generation)
                {
                    _donationHistoryRefreshTask = null;
                    _donationHistoryRefreshRequested = false;
                    ownsRefreshState = true;
                }
            }
            if (ownsRefreshState)
            {
                IsDonationHistoryLoading = false;
                NotifyDonationHistoryStateChanged();
            }
        }
    }

    private async Task RefreshDonationHistoryOnceAsync()
    {
        var session = _donationAlertsAuthSession;
        var client = _donationAlertsClient;
        if (session is null || client is null)
        {
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        cancellation.CancelAfter(DonationHistoryRefreshTimeoutOverride ?? DonationHistoryRefreshTimeout);
        try
        {
            DonationHistoryError = string.Empty;
            var donations = await client.GetDonationHistoryAsync(
                session,
                cancellation.Token).ConfigureAwait(true);
            if (ReferenceEquals(session, _donationAlertsAuthSession))
            {
                ApplyDonationHistory(donations);
            }
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(session, _donationAlertsAuthSession))
            {
                DonationHistoryError = Texts.DonationHistoryRefreshTimedOut;
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException or
                InvalidOperationException or JsonException)
        {
            if (ReferenceEquals(session, _donationAlertsAuthSession))
            {
                DonationHistoryError = Texts.DonationHistoryLoadFailed(AppDiagnostics.GetUserMessage(exception));
            }
        }
    }

    [RelayCommand]
    private void ReplayDonation(DonationHistoryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        DonationControlError = string.Empty;
        if (_donationPlaybackCoordinator is not null && IsDonationAlertsDonation(item.Donation))
        {
            if (!CanUseDonationAlertsDirectControl(item.Donation))
            {
                DonationControlError = Texts.DonationAlertsObsControlMissing;
                NotifyDonationAlertsObsControlStateChanged();
                return;
            }
            if (_donationPlaybackCoordinator.TryEnqueueReplay(item.Donation))
            {
                DonationWindowRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (CurrentDonation?.Id != item.Donation.Id &&
                 !_pendingDonations.Any(donation => donation.Id == item.Donation.Id))
        {
            StartDonationDisplay(item.Donation);
            DonationWindowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private async Task RefreshDonationAlertsObsControlAsync()
    {
        DonationControlError = string.Empty;
        _ = _donationAlertsObsController?.TryAutoConfigure();
        await TryConnectDonationAlertsControlAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
        NotifyDonationAlertsObsControlStateChanged();
    }

    [RelayCommand]
    private void CancelAuthorization()
    {
        CancelAuthorizationCore();
        AuthStatus = Texts.SignInCanceled;
    }

    [RelayCommand]
    private void SignOut()
    {
        CancelAuthorizationCore();
        CancelFollowedChannelsRefresh();
        RemovePrimaryAccountChannel();
        Interlocked.Increment(ref _authenticationGeneration);
        InvalidateModerationContext();
        _authSession = null;
        _authSessionStore.Clear();
        _ = _eventSubClient.ConfigureAsync(null, [], CancellationToken.None);
        CanModerate = false;
        AccountLogin = string.Empty;
        AccountDisplayName = string.Empty;
        AccountProfileImageResource = null;
        _allFollowedChannels = [];
        FollowedChannels.Clear();
        FollowedChannelsStatus = string.Empty;
        IsTwitchClipNotificationVisible = false;
        CreatedTwitchClipEditUri = null;
        CreatedTwitchClipShareUri = null;
        AuthStatus = Texts.AccountDisconnected;
        OnPropertyChanged(nameof(IsAccountConnected));
        NotifyModerationContextChanged();
        OnPropertyChanged(nameof(ShowMessageComposer));
        OnPropertyChanged(nameof(ShowReadOnlyComposerNotice));
        OnPropertyChanged(nameof(CanAuthorize));
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(YouTubeAccountLabel));
        OnPropertyChanged(nameof(ReadOnlyComposerNotice));
        OnPropertyChanged(nameof(WelcomeActionText));
        OnPropertyChanged(nameof(CanHeaderDisconnect));
        OnPropertyChanged(nameof(HeaderDisconnectTip));
        OnPropertyChanged(nameof(HasTwitchClipPermission));
        OnPropertyChanged(nameof(CanCreateTwitchClip));
        OnPropertyChanged(nameof(TwitchClipButtonTip));
        StartAuthorizationCommand.NotifyCanExecuteChanged();
        SendMessageCommand.NotifyCanExecuteChanged();
        CreateTwitchClipCommand.NotifyCanExecuteChanged();
        NotifyFollowedChannelAccessChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendMessageAsync()
    {
        var text = ComposerText.Trim();
        var session = _authSession;
        if (session is null || text.Length == 0)
        {
            return;
        }

        IsSending = true;
        try
        {
            session = await EnsureValidSessionAsync(session, _lifetimeCancellation.Token);
            var result = await _chatApiClient.SendMessageAsync(
                session,
                Channel,
                text,
                cancellationToken: _lifetimeCancellation.Token);
            if (!result.IsSent)
            {
                var reason = string.IsNullOrWhiteSpace(result.DropReasonMessage)
                    ? result.DropReasonCode
                    : result.DropReasonMessage;
                StatusDetail = Texts.MessageNotSent(reason);
                return;
            }

            ComposerText = string.Empty;
            StatusDetail = Texts.MessageSent;
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusDetail = Texts.MessageNotSent(AppDiagnostics.GetUserMessage(exception));
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private async Task ToggleSettingsAsync()
    {
        IsConnectPanelOpen = false;
        if (IsSettingsOpen)
        {
            await CancelSettingsAsync().ConfigureAwait(true);
            return;
        }

        _settingsOpenSnapshot = CreateSettingsSnapshot();
        SelectedSettingsSection = SettingsSection.Program;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private async Task SaveSettingsPanelAsync()
    {
        var settings = CreateSettingsSnapshot();
        _settingsOpenSnapshot = null;
        IsSettingsOpen = false;
        await SaveSettingsSafeAsync(settings).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task CancelSettingsAsync()
    {
        var snapshot = _settingsOpenSnapshot;
        _settingsOpenSnapshot = null;
        if (snapshot is not null)
        {
            _suppressSettingsPersistence = true;
            try
            {
                ApplySettingsSnapshot(snapshot);
            }
            finally
            {
                _suppressSettingsPersistence = false;
            }
        }

        IsSettingsOpen = false;
        return Task.CompletedTask;
    }

    public void StartInitialOnboarding()
    {
        if (!_hasCompletedOnboarding && !IsOnboardingOpen)
        {
            StartOnboarding();
        }
    }

    [RelayCommand]
    private void StartOnboarding()
    {
        ResetTutorialSessionForSwitch();
        ActiveTutorialTopic = TutorialTopic.QuickStart;
        _contextTutorialOriginalSettingsSection = null;
        _contextTutorialOriginalModerationSection = null;
        _contextTutorialOriginalModerationPlatform = null;
        _settingsOpenSnapshot = null;
        IsSettingsOpen = false;
        IsConnectPanelOpen = false;
        IsLogViewerOpen = false;
        IsModerationPanelOpen = false;
        IsModerationDialogOpen = false;
        IsDeleteLogConfirmationOpen = false;
        IsChannelEditorOpen = false;
        IsFiltersVisible = false;
        NavigateOnboarding(0);
        IsOnboardingOpen = true;
    }

    [RelayCommand]
    private void StartContextTutorial(string? topic)
    {
        if (!Enum.TryParse<TutorialTopic>(topic, ignoreCase: true, out var parsedTopic) ||
            parsedTopic == TutorialTopic.QuickStart ||
            !TutorialCatalog.IsContextTopic(parsedTopic))
        {
            return;
        }

        ResetTutorialSessionForSwitch();
        if (parsedTopic == TutorialTopic.Donations && !IsDonationAlertsConnected)
        {
            parsedTopic = TutorialTopic.DonationsSetup;
        }
        _contextTutorialOriginalSettingsSection = parsedTopic == TutorialTopic.Settings
            ? SelectedSettingsSection
            : null;
        _contextTutorialOriginalModerationSection = parsedTopic == TutorialTopic.Moderation
            ? ModerationPanelSection
            : null;
        _contextTutorialOriginalModerationPlatform = parsedTopic == TutorialTopic.Moderation
            ? ModerationPlatform
            : null;
        ActiveTutorialTopic = parsedTopic;
        OnboardingStep = 0;
        PrepareContextTutorialStep(parsedTopic, 0);
        IsOnboardingOpen = true;
    }

    private void ResetTutorialSessionForSwitch()
    {
        if (!IsOnboardingOpen)
        {
            return;
        }

        IsOnboardingOpen = false;
        if (IsContextTutorial && _contextTutorialOriginalSettingsSection is { } originalSection)
        {
            SelectedSettingsSection = originalSection;
        }
        if (IsContextTutorial && _contextTutorialOriginalModerationSection is { } moderationSection)
        {
            ModerationPanelSection = moderationSection;
        }
        if (IsContextTutorial && _contextTutorialOriginalModerationPlatform is { } moderationPlatform)
        {
            ModerationPlatform = moderationPlatform;
        }
        _contextTutorialOriginalSettingsSection = null;
        _contextTutorialOriginalModerationSection = null;
        _contextTutorialOriginalModerationPlatform = null;
    }

    [RelayCommand]
    private void PreviousOnboarding()
    {
        if (!IsOnboardingOpen || OnboardingStep <= 0)
        {
            return;
        }

        NavigateOnboarding(OnboardingStep - 1);
    }

    [RelayCommand]
    private void NextOnboarding()
    {
        if (!IsOnboardingOpen)
        {
            return;
        }

        if (OnboardingStep >= OnboardingTotalSteps - 1)
        {
            CompleteOnboarding();
            return;
        }

        NavigateOnboarding(OnboardingStep + 1);
    }

    [RelayCommand]
    private void SkipOnboarding() => CompleteOnboarding();

    private void NavigateOnboarding(int step)
    {
        var nextStep = Math.Clamp(step, 0, OnboardingTotalSteps - 1);
        if (IsContextTutorial)
        {
            PrepareContextTutorialStep(ActiveTutorialTopic, nextStep);
        }
        OnboardingStep = nextStep;
        if (IsContextTutorial)
        {
            return;
        }

        IsChannelEditorOpen = nextStep == 2;
        IsLogViewerOpen = nextStep == 6;
        if (nextStep is 7 or 8)
        {
            SelectedSettingsSection = nextStep == 7
                ? SettingsSection.Overlay
                : SettingsSection.Program;
            IsSettingsOpen = true;
        }
        else
        {
            IsSettingsOpen = false;
        }
    }

    private void CompleteOnboarding()
    {
        if (!IsOnboardingOpen)
        {
            return;
        }

        IsOnboardingOpen = false;
        if (IsContextTutorial)
        {
            if (_contextTutorialOriginalSettingsSection is { } originalSection)
            {
                SelectedSettingsSection = originalSection;
            }
            if (_contextTutorialOriginalModerationSection is { } moderationSection)
            {
                ModerationPanelSection = moderationSection;
            }
            if (_contextTutorialOriginalModerationPlatform is { } moderationPlatform)
            {
                ModerationPlatform = moderationPlatform;
            }
            _contextTutorialOriginalSettingsSection = null;
            _contextTutorialOriginalModerationSection = null;
            _contextTutorialOriginalModerationPlatform = null;
            return;
        }

        IsSettingsOpen = false;
        IsLogViewerOpen = false;
        IsChannelEditorOpen = false;
        _settingsOpenSnapshot = null;
        _hasCompletedOnboarding = true;
        QueueSettingsSave();
    }

    private void PrepareContextTutorialStep(TutorialTopic topic, int step)
    {
        if (topic == TutorialTopic.Moderation)
        {
            ModerationPlatform = step >= 4 ? 1 : 0;
            ModerationPanelSection = step switch
            {
                2 => 0,
                3 => 1,
                _ => ModerationPanelSection
            };
            return;
        }

        if (topic != TutorialTopic.Settings)
        {
            return;
        }
        SelectedSettingsSection = step switch
        {
            1 => SettingsSection.Program,
            2 => SettingsSection.Chat,
            3 => SettingsSection.ChatLogs,
            4 => SettingsSection.Overlay,
            5 => SettingsSection.Account,
            6 => SettingsSection.Donate,
            7 => SettingsSection.Advanced,
            _ => SelectedSettingsSection
        };
    }

    [RelayCommand]
    private async Task OpenModerationPanelAsync()
    {
        IsSettingsOpen = false;
        IsLogViewerOpen = false;
        IsChannelEditorOpen = false;
        IsStreamEventsOpen = false;
        IsProtectionPanelOpen = false;
        IsMomentsPanelOpen = false;
        if (!CanModerate && CanModerateYouTube)
        {
            ModerationPlatform = 1;
        }
        else if (IsCompactMode && CanModerate)
        {
            ModerationPanelSection = 1;
        }
        IsModerationPanelOpen = true;
        await RefreshModerationPanelAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseModerationPanel()
    {
        IsRecentMessagesOpen = false;
        RecentUserMessages.Clear();
        OnPropertyChanged(nameof(ShowNoRecentMessages));
        IsModerationPanelOpen = false;
    }

    [RelayCommand]
    private void CloseRecentMessages()
    {
        IsRecentMessagesOpen = false;
        RecentUserMessages.Clear();
        OnPropertyChanged(nameof(ShowNoRecentMessages));
    }

    [RelayCommand]
    private void ShowAutoMod() => ModerationPanelSection = 0;

    [RelayCommand]
    private void ShowBannedUsers() => ModerationPanelSection = 1;

    [RelayCommand]
    private void ShowUnbanRequests() => ModerationPanelSection = 2;

    [RelayCommand]
    private async Task ShowTwitchModerationAsync()
    {
        ModerationPlatform = 0;
        await RefreshModerationPanelAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ShowYouTubeModeration()
    {
        ModerationPlatform = 1;
        ModerationPanelStatus = CanModerateYouTube
            ? Texts.YouTubeModerationReady
            : Texts.YouTubeModerationPermissionRequired;
    }

    [RelayCommand]
    private void ShowPendingUnbanRequests() => UnbanRequestFilter = 0;

    [RelayCommand]
    private void ShowApprovedUnbanRequests() => UnbanRequestFilter = 1;

    [RelayCommand]
    private void ShowDeniedUnbanRequests() => UnbanRequestFilter = 2;

    [RelayCommand]
    private async Task RefreshModerationPanelAsync()
    {
        if (IsYouTubeModerationSelected)
        {
            ModerationPanelStatus = CanModerateYouTube
                ? Texts.YouTubeModerationReady
                : Texts.YouTubeModerationPermissionRequired;
            return;
        }
        var session = _authSession;
        var channel = NormalizeChannel(Channel);
        var generation = Volatile.Read(ref _moderationContextGeneration);
        if (session is null || string.IsNullOrWhiteSpace(Channel) || IsModerationPanelBusy)
        {
            return;
        }

        IsModerationPanelBusy = true;
        ModerationPanelStatus = Texts.Loading;
        try
        {
            session = await EnsureValidSessionAsync(session, _lifetimeCancellation.Token);
            if (!IsModerationContextCurrent(generation, channel, session)) return;
            var broadcaster = await _chatApiClient.GetUserProfileAsync(
                session,
                channel,
                _lifetimeCancellation.Token);
            if (!IsModerationContextCurrent(generation, channel, session)) return;
            _moderationBroadcasterId = broadcaster.Id;
            if (BannedUsers.Count == 0 && UnbanRequests.Count == 0)
            {
                var cached = _moderationCacheStore.Restore(broadcaster.Id);
                BannedUsers.AppendBatch(
                    cached.BannedUsers.Select(user => new BannedUserViewModel(user)).ToArray(),
                    1000);
                UnbanRequests.AppendBatch(
                    cached.UnbanRequests.Select(request => new UnbanRequestViewModel(request, Texts)).ToArray(),
                    1000);
                if (BannedUsers.Count > 0 || UnbanRequests.Count > 0)
                {
                    ModerationPanelStatus = Texts.ModerationCacheRestored;
                }
            }
            var bannedUsers = await LoadAllBannedUsersAsync(session, channel, generation);
            if (!IsModerationContextCurrent(generation, channel, session)) return;
            BannedUsers.Clear();
            BannedUsers.AppendBatch(
                bannedUsers.Select(user => new BannedUserViewModel(user)).ToArray(),
                1000);

            var pendingRequests = await LoadAllUnbanRequestsAsync(session, channel, generation, UnbanRequestStatus.Pending);
            var approvedRequests = await LoadAllUnbanRequestsAsync(session, channel, generation, UnbanRequestStatus.Approved);
            var deniedRequests = await LoadAllUnbanRequestsAsync(session, channel, generation, UnbanRequestStatus.Denied);
            if (!IsModerationContextCurrent(generation, channel, session)) return;
            UnbanRequests.Clear();
            UnbanRequests.AppendBatch(
                pendingRequests
                    .Concat(approvedRequests)
                    .Concat(deniedRequests)
                    .OrderByDescending(request => request.CreatedAt)
                    .Select(request => new UnbanRequestViewModel(request, Texts))
                    .ToArray(),
                1000);
            OnPropertyChanged(nameof(VisibleUnbanRequests));
            ModerationPanelStatus = string.Empty;
            ScheduleModerationCacheSave();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested ||
                                                !IsModerationContextCurrent(generation, channel, session))
        {
        }
        catch (Exception exception)
        {
            if (IsModerationContextCurrent(generation, channel, session))
                ModerationPanelStatus = Texts.ModerationPanelFailed(AppDiagnostics.GetUserMessage(exception));
        }
        finally
        {
            if (IsModerationContextCurrent(generation, channel, session))
                IsModerationPanelBusy = false;
        }
    }

    private async Task<IReadOnlyList<BannedUser>> LoadAllBannedUsersAsync(
        TwitchAuthSession session, string channel, long generation)
    {
        var values = new List<BannedUser>();
        string? cursor = null;
        for (var pageNumber = 0; pageNumber < 10; pageNumber++)
        {
            if (!IsModerationContextCurrent(generation, channel, session)) break;
            var page = await _chatApiClient.GetBannedUsersAsync(
                session,
                channel,
                cursor,
                _lifetimeCancellation.Token);
            values.AddRange(page.Users);
            cursor = page.Cursor;
            if (string.IsNullOrWhiteSpace(cursor))
            {
                break;
            }
        }

        return values;
    }

    private async Task<IReadOnlyList<UnbanRequest>> LoadAllUnbanRequestsAsync(
        TwitchAuthSession session,
        string channel,
        long generation,
        UnbanRequestStatus status)
    {
        var values = new List<UnbanRequest>();
        string? cursor = null;
        for (var pageNumber = 0; pageNumber < 10; pageNumber++)
        {
            if (!IsModerationContextCurrent(generation, channel, session)) break;
            var page = await _chatApiClient.GetUnbanRequestsAsync(
                session,
                channel,
                status,
                cursor,
                _lifetimeCancellation.Token);
            values.AddRange(page.Requests);
            cursor = page.Cursor;
            if (string.IsNullOrWhiteSpace(cursor))
            {
                break;
            }
        }

        return values;
    }

    [RelayCommand]
    private async Task UnbanListedUserAsync(BannedUserViewModel? item)
    {
        var session = _authSession;
        var channel = NormalizeChannel(Channel);
        var generation = Volatile.Read(ref _moderationContextGeneration);
        if (item is null || session is null || !CanUnbanUsers || !BannedUsers.Contains(item))
        {
            return;
        }
        try
        {
            session = await EnsureValidSessionAsync(session, _lifetimeCancellation.Token);
            if (!IsModerationContextCurrent(generation, channel, session) || !CanModerate) return;
            await _chatApiClient.RemovePunishmentAsync(
                session, channel, item.Value.UserId, _lifetimeCancellation.Token);
            if (!IsModerationContextCurrent(generation, channel, session)) return;
            _ = BannedUsers.Remove(item);
            ScheduleModerationCacheSave();
            ModerationPanelStatus = Texts.ModerationActionComplete;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested ||
                                                !IsModerationContextCurrent(generation, channel, session))
        {
        }
        catch (Exception exception)
        {
            if (IsModerationContextCurrent(generation, channel, session))
                ModerationPanelStatus = Texts.ModerationPanelFailed(AppDiagnostics.GetUserMessage(exception));
        }
    }

    [RelayCommand]
    private async Task UnbanYouTubeUserAsync(YouTubeChatBanViewModel? item)
    {
        if (item is null || _youTubeLiveChatClient is null || !CanModerateYouTube)
        {
            return;
        }
        try
        {
            await _youTubeLiveChatClient.RemoveBanAsync(
                item.Value.Id, _lifetimeCancellation.Token).ConfigureAwait(true);
            _ = YouTubeBans.Remove(item);
            OnPropertyChanged(nameof(HasYouTubeBans));
            ModerationPanelStatus = Texts.ModerationActionComplete;
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ModerationPanelStatus = Texts.ModerationPanelFailed(AppDiagnostics.GetUserMessage(exception));
        }
    }

    [RelayCommand(CanExecute = nameof(CanUnbanByLogin))]
    private async Task UnbanByLoginAsync()
    {
        var session = _authSession;
        var channel = NormalizeChannel(Channel);
        var generation = Volatile.Read(ref _moderationContextGeneration);
        var login = NormalizeChannel(ModerationUserLogin);
        if (session is null || !CanUnbanByLogin)
        {
            return;
        }

        IsModerationPanelBusy = true;
        try
        {
            session = await EnsureValidSessionAsync(session, _lifetimeCancellation.Token);
            if (!IsModerationContextCurrent(generation, channel, session) || !CanModerate) return;
            var profile = await _chatApiClient.GetUserProfileAsync(
                session,
                login,
                _lifetimeCancellation.Token);
            if (!IsModerationContextCurrent(generation, channel, session) || !CanModerate) return;
            await _chatApiClient.RemovePunishmentAsync(
                session,
                channel,
                profile.Id,
                _lifetimeCancellation.Token);
            if (!IsModerationContextCurrent(generation, channel, session)) return;
            foreach (var item in BannedUsers.Where(item =>
                         string.Equals(item.Value.UserId, profile.Id, StringComparison.Ordinal) ||
                         string.Equals(item.Value.UserLogin, profile.Login, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _ = BannedUsers.Remove(item);
            }
            ModerationUserLogin = string.Empty;
            ModerationPanelStatus = Texts.ModerationActionComplete;
            ScheduleModerationCacheSave();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested ||
                                                !IsModerationContextCurrent(generation, channel, session))
        {
        }
        catch (Exception exception)
        {
            if (IsModerationContextCurrent(generation, channel, session))
                ModerationPanelStatus = Texts.ModerationPanelFailed(AppDiagnostics.GetUserMessage(exception));
        }
        finally
        {
            if (IsModerationContextCurrent(generation, channel, session))
                IsModerationPanelBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanBanByLogin))]
    private void BanByLogin()
    {
        if (!CanBanByLogin)
        {
            return;
        }

        var login = NormalizeChannel(ModerationUserLogin);
        var message = new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Channel = Channel,
            UserLogin = login,
            DisplayName = login,
            Text = string.Empty,
            Timestamp = DateTimeOffset.UtcNow
        };
        OpenModerationDialog(new ChatMessageItemViewModel(message, _imageCache, Texts, owner: this), 0);
    }

    [RelayCommand]
    private async Task ApproveUnbanRequestAsync(UnbanRequestViewModel? item) =>
        await ResolveUnbanRequestAsync(item, approve: true);

    [RelayCommand]
    private async Task DenyUnbanRequestAsync(UnbanRequestViewModel? item) =>
        await ResolveUnbanRequestAsync(item, approve: false);

    private async Task ResolveUnbanRequestAsync(UnbanRequestViewModel? item, bool approve)
    {
        var session = _authSession;
        var channel = NormalizeChannel(Channel);
        var generation = Volatile.Read(ref _moderationContextGeneration);
        if (item is null || session is null || !CanUnbanUsers || !UnbanRequests.Contains(item))
        {
            return;
        }
        try
        {
            session = await EnsureValidSessionAsync(session, _lifetimeCancellation.Token);
            if (!IsModerationContextCurrent(generation, channel, session) || !CanModerate) return;
            var resolved = await _chatApiClient.ResolveUnbanRequestAsync(
                session, channel, item.Value.RequestId, approve, cancellationToken: _lifetimeCancellation.Token);
            if (!IsModerationContextCurrent(generation, channel, session)) return;
            var index = UnbanRequests.IndexOf(item);
            if (index >= 0)
            {
                UnbanRequests[index] = new UnbanRequestViewModel(resolved, Texts);
                OnPropertyChanged(nameof(VisibleUnbanRequests));
            }
            if (approve)
            {
                foreach (var banned in BannedUsers.Where(user => user.Value.UserId == item.Value.UserId).ToArray())
                {
                    _ = BannedUsers.Remove(banned);
                }
            }
            ScheduleModerationCacheSave();
            ModerationPanelStatus = approve ? Texts.UnbanApproved : Texts.UnbanDenied;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested ||
                                                !IsModerationContextCurrent(generation, channel, session))
        {
        }
        catch (Exception exception)
        {
            if (IsModerationContextCurrent(generation, channel, session))
                ModerationPanelStatus = Texts.ModerationPanelFailed(AppDiagnostics.GetUserMessage(exception));
        }
    }

    [RelayCommand]
    private async Task AllowAutoModMessageAsync(HeldAutoModMessageViewModel? item) =>
        await ManageAutoModMessageAsync(item, allow: true);

    [RelayCommand]
    private async Task DenyAutoModMessageAsync(HeldAutoModMessageViewModel? item) =>
        await ManageAutoModMessageAsync(item, allow: false);

    private async Task ManageAutoModMessageAsync(HeldAutoModMessageViewModel? item, bool allow)
    {
        var session = _authSession;
        var channel = NormalizeChannel(Channel);
        var generation = Volatile.Read(ref _moderationContextGeneration);
        if (item is null || session is null || item.IsBusy || !CanModerate || !PendingAutoModMessages.Contains(item))
        {
            return;
        }
        item.IsBusy = true;
        item.ErrorMessage = string.Empty;
        try
        {
            session = await EnsureValidSessionAsync(session, _lifetimeCancellation.Token);
            if (!IsModerationContextCurrent(generation, channel, session) || !CanModerate) return;
            await _chatApiClient.ManageHeldAutoModMessageAsync(
                session, item.Value.MessageId, allow, _lifetimeCancellation.Token);
            if (!IsModerationContextCurrent(generation, channel, session)) return;
            _ = PendingAutoModMessages.Remove(item);
            ModerationPanelStatus = allow ? Texts.AutoModAllowed : Texts.AutoModDenied;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested ||
                                                !IsModerationContextCurrent(generation, channel, session))
        {
        }
        catch (Exception exception)
        {
            if (IsModerationContextCurrent(generation, channel, session))
                item.ErrorMessage = Texts.ModerationPanelFailed(AppDiagnostics.GetUserMessage(exception));
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowProgramSettings() => SelectedSettingsSection = SettingsSection.Program;

    [RelayCommand]
    private void ShowChatSettings() => SelectedSettingsSection = SettingsSection.Chat;

    [RelayCommand]
    private void ShowChatLogsSettings() => SelectedSettingsSection = SettingsSection.ChatLogs;

    [RelayCommand]
    private void ShowOverlaySettings() => SelectedSettingsSection = SettingsSection.Overlay;

    [RelayCommand]
    private void ShowAccountSettings() => SelectedSettingsSection = SettingsSection.Account;

    [RelayCommand]
    private void ShowDonateSettings() => SelectedSettingsSection = SettingsSection.Donate;

    [RelayCommand]
    private void ShowAdvancedSettings() => SelectedSettingsSection = SettingsSection.Advanced;

    [RelayCommand]
    private void ToggleChannelEditor()
    {
        if (IsOnboardingOpen)
        {
            return;
        }

        IsChannelEditorOpen = !IsChannelEditorOpen;
        if (!IsChannelEditorOpen)
        {
            IsAddingChannel = false;
            ChannelDraft = string.Empty;
            CancelChannelSearch();
        }
    }

    [RelayCommand]
    private void StartAddChannel()
    {
        if (!CanAddSavedChannel)
        {
            StatusDetail = Texts.ChannelLimitReached;
            return;
        }

        IsSettingsOpen = false;
        IsLogViewerOpen = false;
        IsModerationPanelOpen = false;
        IsAddingChannel = false;
        ConnectPanelChannel = string.Empty;
        _validatedChannelDraft = string.Empty;
        _validatedChannelDraftResult = null;
        _validatedConnectPanelChannel = string.Empty;
        _validatedConnectPanelResult = null;
        ConnectPanelError = string.Empty;
        ChannelSearchStatus = string.Empty;
        IsFollowedChannelsTabSelected = false;
        ChannelSearchResults.Clear();
        OnPropertyChanged(nameof(HasChannelSearchResults));
        IsConnectPanelOpen = true;
    }

    [RelayCommand]
    private void CancelAddChannel()
    {
        IsAddingChannel = false;
        ChannelDraft = string.Empty;
        _validatedChannelDraft = string.Empty;
        _validatedChannelDraftResult = null;
        CancelChannelSearch();
    }

    [RelayCommand]
    private async Task ConfirmAddChannelAsync()
    {
        if (!CanConfirmAddChannel)
        {
            return;
        }

        Channel = NormalizeChannel(ChannelDraft);
        IsAddingChannel = false;
        await ConnectAsync().ConfigureAwait(true);
        ChannelDraft = string.Empty;
    }

    [RelayCommand]
    private async Task SelectChannelSearchResultAsync(ChannelSearchResultViewModel? result)
    {
        if (result is null)
        {
            return;
        }

        CancelChannelSearch();
        if (IsConnectPanelOpen)
        {
            ConnectPanelChannel = result.Login;
            _validatedConnectPanelChannel = NormalizeChannel(result.Login);
            _validatedConnectPanelResult = result.Value;
            ConnectPanelError = string.Empty;
            OnPropertyChanged(nameof(CanWatchChannel));
            await WatchChannelFromConnectPanelAsync().ConfigureAwait(true);
        }
        else
        {
            ChannelDraft = result.Login;
            _validatedChannelDraft = NormalizeChannel(result.Login);
            _validatedChannelDraftResult = result.Value;
            OnPropertyChanged(nameof(CanConfirmAddChannel));
            await ConfirmAddChannelAsync().ConfigureAwait(true);
        }
    }

    partial void OnChannelDraftChanged(string value)
    {
        _validatedChannelDraft = string.Empty;
        _validatedChannelDraftResult = null;
        OnPropertyChanged(nameof(CanConfirmAddChannel));
        _ = SearchChannelsAsync(value, forConnectPanel: false);
    }

    partial void OnConnectPanelChannelChanged(string value)
    {
        _validatedConnectPanelChannel = string.Empty;
        _validatedConnectPanelResult = null;
        ConnectPanelError = string.Empty;
        OnPropertyChanged(nameof(CanWatchChannel));
        if (IsManualChannelTabSelected)
        {
            _ = SearchChannelsAsync(value, forConnectPanel: true);
        }
    }

    partial void OnFollowedChannelsSearchChanged(string value) => RebuildFollowedChannels();

    [RelayCommand]
    private async Task RefreshFollowedChannelsAsync()
    {
        CancelFollowedChannelsRefresh();
        var session = _authSession;
        if (session is null)
        {
            FollowedChannelsStatus = Texts.FollowedChannelsSignInRequired;
            return;
        }
        if (!HasFollowedChannelsPermission)
        {
            FollowedChannelsStatus = Texts.FollowedChannelsPermissionRequired;
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _followedChannelsCancellation = cancellation;
        IsFollowedChannelsLoading = true;
        FollowedChannelsStatus = string.Empty;
        try
        {
            session = await EnsureValidSessionAsync(session, cancellation.Token).ConfigureAwait(true);
            var results = await _chatApiClient.GetFollowedChannelsAsync(session, cancellation.Token)
                .ConfigureAwait(true);
            if (!ReferenceEquals(_followedChannelsCancellation, cancellation))
            {
                return;
            }

            _allFollowedChannels = results.Select(CreateChannelSearchResultViewModel).ToArray();
            RebuildFollowedChannels();
            if (_allFollowedChannels.Count == 0)
            {
                FollowedChannelsStatus = Texts.FollowedChannelsEmpty;
            }
        }
        catch (OperationCanceledException exception) when (
            cancellation.IsCancellationRequested || exception is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException)
        {
            FollowedChannelsStatus = Texts.FollowedChannelsLoadFailed;
        }
        finally
        {
            if (ReferenceEquals(_followedChannelsCancellation, cancellation))
            {
                _followedChannelsCancellation = null;
                IsFollowedChannelsLoading = false;
            }
            cancellation.Dispose();
        }
    }

    private void RebuildFollowedChannels()
    {
        var query = FollowedChannelsSearch.Trim().TrimStart('@');
        var visible = _allFollowedChannels
            .Where(channel => query.Length == 0 ||
                channel.Login.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                channel.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        FollowedChannels.Clear();
        FollowedChannels.AppendBatch(visible, Math.Max(1, visible.Length));
        OnPropertyChanged(nameof(HasFollowedChannels));
        if (!IsFollowedChannelsLoading && _allFollowedChannels.Count > 0)
        {
            FollowedChannelsStatus = visible.Length == 0
                ? Texts.FollowedChannelsNoMatches
                : string.Empty;
        }
    }

    private ChannelSearchResultViewModel CreateChannelSearchResultViewModel(ChannelSearchResult result)
    {
        ChatImageResource? thumbnail = null;
        if (Uri.TryCreate(result.ThumbnailUrl, UriKind.Absolute, out var uri))
        {
            thumbnail = _imageCache.GetResource(uri, 40);
        }
        return new ChannelSearchResultViewModel(result, thumbnail);
    }

    private void CancelFollowedChannelsRefresh()
    {
        var cancellation = Interlocked.Exchange(ref _followedChannelsCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        IsFollowedChannelsLoading = false;
    }

    private async Task SearchChannelsAsync(string query, bool forConnectPanel)
    {
        CancelChannelSearch(clearStatus: false);
        ChannelSearchStatus = string.Empty;
        var session = _authSession;
        var normalizedQuery = NormalizeChannel(query);
        var targetIsOpen = forConnectPanel ? IsConnectPanelOpen : IsAddingChannel;
        if (!targetIsOpen || normalizedQuery.Length < 2 || !IsValidChannelLogin(normalizedQuery))
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _channelSearchCancellation = cancellation;
        try
        {
            await Task.Delay(300, cancellation.Token).ConfigureAwait(true);
            IsChannelSearchBusy = true;
            IReadOnlyList<ChannelSearchResult> results;
            if (session is null)
            {
                var result = await _chatApiClient.GetPublicChannelAsync(normalizedQuery, cancellation.Token)
                    .ConfigureAwait(true);
                results = result is null ? [] : [result];
            }
            else
            {
                try
                {
                    results = await _chatApiClient.SearchChannelsAsync(session, normalizedQuery, cancellation.Token)
                        .ConfigureAwait(true);
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException)
                {
                    var fallbackExactResult = await _chatApiClient.GetPublicChannelAsync(
                            normalizedQuery,
                            cancellation.Token)
                        .ConfigureAwait(true);
                    results = fallbackExactResult is null ? [] : [fallbackExactResult];
                }
            }
            if (!ReferenceEquals(_channelSearchCancellation, cancellation))
            {
                return;
            }
            var inputStillMatches = string.Equals(
                NormalizeChannel(forConnectPanel ? ConnectPanelChannel : ChannelDraft),
                normalizedQuery,
                StringComparison.OrdinalIgnoreCase);
            var panelStillOpen = forConnectPanel ? IsConnectPanelOpen : IsAddingChannel;
            if (!inputStillMatches || !panelStillOpen)
            {
                return;
            }

            ChannelSearchResults.AppendBatch(results.Select(CreateChannelSearchResultViewModel).ToArray(), 6);
            OnPropertyChanged(nameof(HasChannelSearchResults));
            ChannelSearchStatus = results.Count == 0 ? Texts.ChannelSearchNoResults : string.Empty;
            var exactResult = results.FirstOrDefault(result =>
                string.Equals(result.BroadcasterLogin, normalizedQuery, StringComparison.OrdinalIgnoreCase));
            if (forConnectPanel)
            {
                _validatedConnectPanelChannel = exactResult?.BroadcasterLogin ?? string.Empty;
                _validatedConnectPanelResult = exactResult;
                OnPropertyChanged(nameof(CanWatchChannel));
            }
            else
            {
                _validatedChannelDraft = exactResult?.BroadcasterLogin ?? string.Empty;
                _validatedChannelDraftResult = exactResult;
                OnPropertyChanged(nameof(CanConfirmAddChannel));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException)
        {
            ChannelSearchStatus = Texts.ChannelSearchFailed;
        }
        finally
        {
            if (ReferenceEquals(_channelSearchCancellation, cancellation))
            {
                _channelSearchCancellation = null;
                IsChannelSearchBusy = false;
            }
            cancellation.Dispose();
        }
    }

    private void CancelChannelSearch(bool clearStatus = true)
    {
        var cancellation = Interlocked.Exchange(ref _channelSearchCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        IsChannelSearchBusy = false;
        ChannelSearchResults.Clear();
        OnPropertyChanged(nameof(HasChannelSearchResults));
        if (clearStatus)
        {
            ChannelSearchStatus = string.Empty;
        }
    }

    private bool CanRemoveSavedChannel(ChannelSessionViewModel? channel) =>
        !_disposed && !IsChannelRemovalBusy && channel?.CanRemove == true && SavedChannels.Contains(channel);

    [RelayCommand(CanExecute = nameof(CanRemoveSavedChannel))]
    private async Task RemoveSavedChannelAsync(ChannelSessionViewModel? channel)
    {
        if (!CanRemoveSavedChannel(channel)) return;
        IsChannelRemovalBusy = true;
        ChannelOperationError = string.Empty;
        try
        {
            await _chatClient.PartChannelAsync(channel!.Login, _lifetimeCancellation.Token).ConfigureAwait(true);
            if (_disposed) return;
            _ = SavedChannels.Remove(channel);
            if (ReferenceEquals(SelectedSavedChannel, channel))
            {
                SelectedSavedChannel = SavedChannels.FirstOrDefault();
                Channel = SelectedSavedChannel?.Login ?? string.Empty;
                RebuildVisibleMessages();
            }
            OnPropertyChanged(nameof(CanAddSavedChannel));
            OnPropertyChanged(nameof(CanConfirmAddChannel));
            if (StatusDetail == Texts.ChannelRemovalFailed) StatusDetail = string.Empty;
            await SaveSettingsSafeAsync().ConfigureAwait(true);
            await RestartEventSubSafeAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed || _lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or
                                          InvalidOperationException or OperationCanceledException)
        {
            if (_disposed) return;
            AppDiagnostics.Write("Channel removal", exception);
            ChannelOperationError = Texts.ChannelRemovalFailed;
            StatusDetail = ChannelOperationError;
        }
        finally { IsChannelRemovalBusy = false; }
    }

    [RelayCommand]
    private void SwitchSavedChannel(ChannelSessionViewModel? channel)
    {
        if (channel is not null)
        {
            SelectedSavedChannel = channel;
        }
    }

    [RelayCommand]
    private void DecreaseChatFontSize() => ChatFontSize -= 1;

    [RelayCommand]
    private void IncreaseChatFontSize() => ChatFontSize += 1;

    [RelayCommand]
    private void DecreaseMessageLimit() => MessageLimit -= 250;

    [RelayCommand]
    private void IncreaseMessageLimit() => MessageLimit += 250;

    [RelayCommand]
    private void DecreaseViewerCountRefreshInterval() =>
        ViewerCountRefreshIntervalSeconds = Math.Max(
            WitherChatSettings.MinimumViewerCountRefreshIntervalSeconds,
            ViewerCountRefreshIntervalSeconds - 15);

    [RelayCommand]
    private void IncreaseViewerCountRefreshInterval() =>
        ViewerCountRefreshIntervalSeconds = Math.Min(
            WitherChatSettings.MaximumViewerCountRefreshIntervalSeconds,
            ViewerCountRefreshIntervalSeconds + 15);

    [RelayCommand]
    private void DecreaseOverlayPort() => OverlayPort = Math.Max(1024, OverlayPort - 1);

    [RelayCommand]
    private void IncreaseOverlayPort() => OverlayPort = Math.Min(65535, OverlayPort + 1);

    [RelayCommand]
    private void DecreaseOverlayMessages() => OverlayMaxMessages = Math.Max(1, OverlayMaxMessages - 1);

    [RelayCommand]
    private void IncreaseOverlayMessages() => OverlayMaxMessages = Math.Min(100, OverlayMaxMessages + 1);

    [RelayCommand]
    private void DecreaseOverlayFontSize() => OverlayFontSize = Math.Max(10, OverlayFontSize - 1);

    [RelayCommand]
    private void IncreaseOverlayFontSize() => OverlayFontSize = Math.Min(72, OverlayFontSize + 1);

    [RelayCommand]
    private void DecreaseOverlayFade() => OverlayFadeOutSeconds = Math.Max(0, OverlayFadeOutSeconds - 1);

    [RelayCommand]
    private void IncreaseOverlayFade() => OverlayFadeOutSeconds = Math.Min(600, OverlayFadeOutSeconds + 1);

    [RelayCommand]
    private void DecreaseOverlayOpacity() =>
        OverlayBackgroundOpacity = Math.Max(0, Math.Round(OverlayBackgroundOpacity - 0.05, 2));

    [RelayCommand]
    private void IncreaseOverlayOpacity() =>
        OverlayBackgroundOpacity = Math.Min(1, Math.Round(OverlayBackgroundOpacity + 0.05, 2));

    [RelayCommand]
    private void DecreaseLogViewerLimit() => MaxLogViewerMessages = Math.Max(100, MaxLogViewerMessages - 100);

    [RelayCommand]
    private void IncreaseLogViewerLimit() => MaxLogViewerMessages = Math.Min(50_000, MaxLogViewerMessages + 100);

    [RelayCommand]
    private void CopyOverlayUrl()
    {
        CopyTextRequested?.Invoke(this, new ValueEventArgs<string>(OverlayUrl));
        OverlayStatusText = Texts.OverlayCopied;
    }

    [RelayCommand]
    private async Task TestOverlayMessageAsync()
    {
        if (!EnableObsOverlay)
        {
            OverlayStatusText = Texts.OverlayDisabled;
            StatusDetail = OverlayStatusText;
            return;
        }

        await ApplyOverlaySettingsAsync().ConfigureAwait(true);
        if (!_obsOverlayServer.IsRunning)
        {
            return;
        }

        _obsOverlayServer.Publish(new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Channel = string.IsNullOrWhiteSpace(Channel) ? "witherchat" : Channel,
            UserLogin = "witherchat",
            DisplayName = "WitherChat",
            Text = Texts.OverlayTestText,
            Timestamp = DateTimeOffset.UtcNow,
            UserColor = "#9F8CFF",
            Parts = [ChatMessagePart.PlainText(Texts.OverlayTestText)]
        });
        OverlayStatusText = Texts.OverlayTestSent;
        StatusDetail = OverlayStatusText;
    }

    [RelayCommand]
    private void OpenSupportLink(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            OpenUriRequested?.Invoke(this, new ValueEventArgs<Uri>(uri));
        }
    }

    [RelayCommand]
    private void OpenChatLink(Uri? uri)
    {
        if (uri is { IsAbsoluteUri: true } &&
            uri.Scheme is "http" or "https")
        {
            OpenUriRequested?.Invoke(this, new ValueEventArgs<Uri>(uri));
        }
    }

    [RelayCommand]
    private void CopySupportAddress(string? address)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            CopyTextRequested?.Invoke(this, new ValueEventArgs<string>(address));
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateTwitchClip))]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This UI command boundary converts Twitch and network failures into a localized notification.")]
    private async Task CreateTwitchClipAsync()
    {
        if (Interlocked.CompareExchange(ref _clipCreationGate, 1, 0) != 0)
        {
            return;
        }

        IsCreatingTwitchClip = true;
        IsTwitchClipCreationSuccessful = false;
        IsTwitchClipNotificationVisible = true;
        CreatedTwitchClipEditUri = null;
        CreatedTwitchClipShareUri = null;
        TwitchClipStatusTitle = Texts.TwitchClipCreating;
        TwitchClipStatusMessage = Texts.TwitchClipCreatingDescription;

        try
        {
            var session = _authSession;
            if (session is null || string.IsNullOrWhiteSpace(Channel) || !IsStreamLive)
            {
                TwitchClipStatusTitle = Texts.TwitchClipFailed;
                TwitchClipStatusMessage = Texts.TwitchClipLiveRequired;
                return;
            }

            if (!session.Scopes.Contains(TwitchApplication.ClipsScope, StringComparer.Ordinal))
            {
                TwitchClipStatusTitle = Texts.TwitchClipPermissionTitle;
                TwitchClipStatusMessage = Texts.TwitchClipPermissionDescription;
                await AuthorizeAsync(keepConnectPanelOpen: false, loadFollowedChannels: false)
                    .ConfigureAwait(true);
                session = _authSession;
                if (session is null ||
                    !session.Scopes.Contains(TwitchApplication.ClipsScope, StringComparer.Ordinal))
                {
                    TwitchClipStatusTitle = Texts.TwitchClipFailed;
                    TwitchClipStatusMessage = Texts.TwitchClipPermissionNotGranted;
                    return;
                }

                TwitchClipStatusTitle = Texts.TwitchClipCreating;
                TwitchClipStatusMessage = Texts.TwitchClipCreatingDescription;
            }

            session = await EnsureValidSessionAsync(session, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            var result = await _chatApiClient.CreateClipAsync(
                    session,
                    Channel,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            CreatedTwitchClipEditUri = result.EditUri;
            CreatedTwitchClipShareUri = result.ShareUri;
            IsTwitchClipCreationSuccessful = true;
            TwitchClipStatusTitle = Texts.TwitchClipCreated;
            TwitchClipStatusMessage = Texts.TwitchClipCreatedDescription;
            StatusDetail = Texts.TwitchClipCreated;
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
            IsTwitchClipNotificationVisible = false;
        }
        catch (Exception exception)
        {
            IsTwitchClipCreationSuccessful = false;
            TwitchClipStatusTitle = Texts.TwitchClipFailed;
            TwitchClipStatusMessage = GetTwitchClipFailureText(exception);
            StatusDetail = TwitchClipStatusMessage;
        }
        finally
        {
            Volatile.Write(ref _clipCreationGate, 0);
            IsCreatingTwitchClip = false;
        }
    }

    [RelayCommand]
    private void OpenCreatedTwitchClip()
    {
        if (CreatedTwitchClipEditUri is { } uri)
        {
            OpenUriRequested?.Invoke(this, new ValueEventArgs<Uri>(uri));
        }
    }

    [RelayCommand]
    private void CopyCreatedTwitchClipLink()
    {
        if (CreatedTwitchClipShareUri is { } uri)
        {
            CopyTextRequested?.Invoke(this, new ValueEventArgs<string>(uri.AbsoluteUri));
            TwitchClipStatusMessage = Texts.TwitchClipLinkCopied;
        }
    }

    [RelayCommand]
    private void DismissTwitchClipNotification() => IsTwitchClipNotificationVisible = false;

    private string GetTwitchClipFailureText(Exception exception) => exception switch
    {
        InvalidOperationException => Texts.TwitchClipPermissionNotGranted,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } =>
            Texts.TwitchClipPermissionNotGranted,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } =>
            Texts.TwitchClipRestricted,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } =>
            Texts.TwitchClipLiveRequired,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest } =>
            Texts.TwitchClipUnavailable,
        HttpRequestException or InvalidDataException or JsonException => Texts.TwitchClipTemporaryFailure,
        _ => Texts.TwitchClipTemporaryFailure
    };

    [RelayCommand]
    private void ToggleHeader() => IsHeaderExpanded = !IsHeaderExpanded;

    [RelayCommand]
    private void ToggleComposer() => IsComposerExpanded = !IsComposerExpanded;

    [RelayCommand]
    private void ToggleFilters() => IsFiltersVisible = !IsFiltersVisible;

    [RelayCommand]
    private async Task OpenLogsAsync()
    {
        IsSettingsOpen = false;
        IsChannelEditorOpen = false;
        IsStreamEventsOpen = false;
        IsProtectionPanelOpen = false;
        IsMomentsPanelOpen = false;
        IsLogViewerOpen = true;
        await RefreshChatLogFilesAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseLogs() => IsLogViewerOpen = false;

    [RelayCommand]
    private void OpenStreamEvents()
    {
        IsSettingsOpen = false;
        IsChannelEditorOpen = false;
        IsLogViewerOpen = false;
        IsModerationPanelOpen = false;
        IsProtectionPanelOpen = false;
        IsMomentsPanelOpen = false;
        IsStreamEventsOpen = true;
        RebuildVisibleStreamEvents();
    }

    [RelayCommand]
    private void CloseStreamEvents() => IsStreamEventsOpen = false;

    [RelayCommand]
    private void SetStreamEventFilter(string? filter)
    {
        StreamEventFilter = int.TryParse(filter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 3)
            : 0;
        RebuildVisibleStreamEvents();
    }

    [RelayCommand]
    private void SetChatViewMode(string? mode)
    {
        var value = int.TryParse(mode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 0, (int)ChatViewMode.Roles)
            : 0;
        SelectedChatViewMode = (ChatViewMode)value;
        SetFollowingLatest(true);
        RebuildVisibleMessages();
        ScrollToLatestRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenProtectionPanel()
    {
        IsSettingsOpen = false;
        IsChannelEditorOpen = false;
        IsLogViewerOpen = false;
        IsModerationPanelOpen = false;
        IsStreamEventsOpen = false;
        IsProtectionPanelOpen = true;
        ProtectionStatus = string.Empty;
    }

    [RelayCommand]
    private void CloseProtectionPanel()
    {
        IsClearChatConfirmationOpen = false;
        IsProtectionPanelOpen = false;
    }
    [RelayCommand]
    private void OpenMomentsPanel()
    {
        IsSettingsOpen = false;
        IsChannelEditorOpen = false;
        IsLogViewerOpen = false;
        IsModerationPanelOpen = false;
        IsStreamEventsOpen = false;
        IsProtectionPanelOpen = false;
        if (_streamMomentStore is { HadLoadFailure: true } store && !IsMomentsSaving)
        {
            var loaded = store.Load();
            if (!store.HadLoadFailure || loaded.Count > 0)
            {
                Moments.Clear();
                Moments.AppendBatch(loaded.OrderByDescending(value => value.SavedAtUtc)
                    .Select(value => new StreamMomentItemViewModel(value)).ToArray(), 5000);
                OnPropertyChanged(nameof(HasStreamMoments));
                OnPropertyChanged(nameof(HasHeaderOverflowActivity));
            }
            MomentsStatus = store.HadLoadFailure ? Texts.MomentsLoadProblem : string.Empty;
        }
        IsMomentsPanelOpen = true;
    }

    private bool CanChangeMoments() => !_disposed && !IsMomentsSaving;

    [RelayCommand(CanExecute = nameof(CanChangeMoments))]
    private void CloseMomentsPanel()
    {
        if (!CanChangeMoments()) return;
        CancelSaveMoment();
        IsMomentsPanelOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanChangeMoments))]
    private void BeginSaveMoment(ChatMessageItemViewModel? item)
    {
        if (item is null || !CanChangeMoments()) return;
        MomentsStatus = string.Empty;
        MomentTarget = item;
        MomentNote = string.Empty;
        IsMomentEditorOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanChangeMoments))]
    private void CancelSaveMoment()
    {
        if (!CanChangeMoments()) return;
        IsMomentEditorOpen = false;
        MomentTarget = null;
        MomentNote = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanChangeMoments))]
    private async Task ConfirmSaveMomentAsync()
    {
        var item = MomentTarget;
        if (item is null || !CanChangeMoments()) return;
        var message = item.Message;
        var moment = new StreamMoment(
            Guid.NewGuid().ToString("N"), message.Platform, message.Channel,
            string.IsNullOrWhiteSpace(message.PlatformMessageId) ? message.Id : message.PlatformMessageId,
            message.UserLabel, message.Text, MomentNote.Trim(), message.Timestamp,
            DateTimeOffset.UtcNow, message.StreamEvent?.Kind ?? string.Empty);
        IsMomentsSaving = true;
        var saved = false;
        try
        {
            saved = await SaveMomentsSafeAsync(GetMomentSnapshot().Prepend(moment).Take(5000).ToArray())
                .ConfigureAwait(true);
            if (saved)
            {
                Moments.Insert(0, new StreamMomentItemViewModel(moment));
                while (Moments.Count > 5000) Moments.RemoveAt(Moments.Count - 1);
                OnPropertyChanged(nameof(HasStreamMoments));
                OnPropertyChanged(nameof(HasHeaderOverflowActivity));
            }
        }
        finally { IsMomentsSaving = false; }
        if (saved && ReferenceEquals(MomentTarget, item)) CancelSaveMoment();
    }

    [RelayCommand(CanExecute = nameof(CanChangeMoments))]
    private async Task DeleteMomentAsync(StreamMomentItemViewModel? item)
    {
        if (item is null || !CanChangeMoments() || !Moments.Contains(item)) return;
        IsMomentsSaving = true;
        try
        {
            var snapshot = Moments.Where(value => !ReferenceEquals(value, item))
                .Select(value => value.Value).ToArray();
            if (await SaveMomentsSafeAsync(snapshot).ConfigureAwait(true))
            {
                Moments.Remove(item);
                OnPropertyChanged(nameof(HasStreamMoments));
                OnPropertyChanged(nameof(HasHeaderOverflowActivity));
            }
        }
        finally { IsMomentsSaving = false; }
    }

    public IReadOnlyList<StreamMoment> GetMomentSnapshot() => Moments.Select(item => item.Value).ToArray();

    private async Task<bool> SaveMomentsSafeAsync(IReadOnlyCollection<StreamMoment> snapshot)
    {
        try
        {
            if (_streamMomentStore is not null)
                await _streamMomentStore.SaveAsync(snapshot, _lifetimeCancellation.Token).ConfigureAwait(true);
            if (StatusDetail == _momentsFailureDetail) StatusDetail = string.Empty;
            _momentsFailureDetail = null;
            MomentsStatus = string.Empty;
            return true;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          JsonException or InvalidOperationException)
        {
            MomentsStatus = Texts.MomentsWriteRetry;
            _momentsFailureDetail = Texts.MomentsSaveFailed(AppDiagnostics.GetUserMessage(exception));
            StatusDetail = _momentsFailureDetail;
            return false;
        }
    }

    [RelayCommand]
    private async Task ApplyProtectionAsync()
    {
        var session = _authSession;
        if (session is null || !CanModerate || IsProtectionBusy || string.IsNullOrWhiteSpace(Channel))
        {
            return;
        }
        IsProtectionBusy = true;
        ProtectionStatus = string.Empty;
        try
        {
            var settings = new ChatProtectionSettings(
                ProtectionSlowMode,
                Math.Clamp(ProtectionSlowSeconds, 3, 120),
                ProtectionSubscriberMode,
                ProtectionFollowerMode,
                Math.Clamp(ProtectionFollowerMinutes, 0, 129_600));
            await _chatApiClient.UpdateChatProtectionAsync(
                session, Channel, settings, _lifetimeCancellation.Token).ConfigureAwait(true);
            ProtectionStatus = Texts.ProtectionApplied;
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or
                                          JsonException or InvalidOperationException or ArgumentException)
        {
            ProtectionStatus = Texts.ModerationPanelFailed(AppDiagnostics.GetUserMessage(exception));
        }
        finally
        {
            IsProtectionBusy = false;
        }
    }

    [RelayCommand]
    private void RequestClearChat()
    {
        if (CanApplyProtection)
        {
            IsClearChatConfirmationOpen = true;
        }
    }

    [RelayCommand]
    private void CancelClearChat() => IsClearChatConfirmationOpen = false;

    [RelayCommand]
    private async Task ConfirmClearChatAsync()
    {
        var session = _authSession;
        if (session is null || !CanModerate || IsProtectionBusy || string.IsNullOrWhiteSpace(Channel))
        {
            return;
        }
        IsProtectionBusy = true;
        try
        {
            await _chatApiClient.ClearChatAsync(session, Channel, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            ClearMessageHistory();
            ProtectionStatus = Texts.ProtectionChatCleared;
            IsClearChatConfirmationOpen = false;
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or
                                          JsonException or InvalidOperationException or ArgumentException)
        {
            ProtectionStatus = Texts.ModerationPanelFailed(AppDiagnostics.GetUserMessage(exception));
        }
        finally
        {
            IsProtectionBusy = false;
        }
    }

    [RelayCommand]
    private void RequestDeleteLog()
    {
        if (SelectedLogFile is not null)
        {
            IsDeleteLogConfirmationOpen = true;
        }
    }

    [RelayCommand]
    private void CancelDeleteLog() => IsDeleteLogConfirmationOpen = false;

    [RelayCommand]
    private async Task ConfirmDeleteLogAsync()
    {
        var selected = SelectedLogFile;
        if (selected is null)
        {
            IsDeleteLogConfirmationOpen = false;
            return;
        }
        try
        {
            var root = Path.GetFullPath(_chatLogWriter.LogDirectory) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(selected.Path);
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (IsSafeLogPath(root, path, pathComparison))
            {
                var directory = Path.GetDirectoryName(path) ?? string.Empty;
                if (string.Equals(Path.GetFileNameWithoutExtension(path), "chat", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(directory, "metadata.json")))
                {
                    foreach (var fileName in new[] { "chat.jsonl", "chat.txt", "metadata.json" })
                    {
                        File.Delete(Path.Combine(directory, fileName));
                    }
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }
                else
                {
                    File.Delete(path);
                    File.Delete(Path.ChangeExtension(path, ".log"));
                    File.Delete(Path.ChangeExtension(path, ".jsonl"));
                }
            }
            IsDeleteLogConfirmationOpen = false;
            await RefreshChatLogFilesAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          NotSupportedException or System.Security.SecurityException)
        {
            StatusDetail = Texts.LogOpenFailed + " " + AppDiagnostics.GetUserMessage(exception);
        }
    }

    [RelayCommand]
    private void OpenLogDirectory() => OpenLogDirectoryRequested?.Invoke(
        this,
        new ValueEventArgs<string>(_chatLogWriter.LogDirectory));

    [RelayCommand]
    private void ClearMessages()
    {
        ClearMessageHistory();
    }

    [RelayCommand]
    private void CopyUsername(ChatMessageItemViewModel? item)
    {
        if (item is not null)
        {
            CopyTextRequested?.Invoke(this, new ValueEventArgs<string>(item.Message.UserLogin));
        }
    }

    [RelayCommand]
    private void CopyMessage(ChatMessageItemViewModel? item)
    {
        if (item is not null)
        {
            CopyTextRequested?.Invoke(this, new ValueEventArgs<string>(item.Message.Text));
        }
    }

    [RelayCommand]
    private void OpenUserOnTwitch(ChatMessageItemViewModel? item)
    {
        if (item is not null)
        {
            var profileKey = item.Message.IsYouTubeMessage
                ? item.Message.UserId.Trim()
                : item.Message.UserLogin.Trim().TrimStart('@');
            if (profileKey.Length == 0)
            {
                return;
            }

            var uri = item.Message.IsYouTubeMessage
                ? new Uri("https://www.youtube.com/channel/" + Uri.EscapeDataString(profileKey))
                : new Uri("https://www.twitch.tv/" + Uri.EscapeDataString(profileKey));
            OpenUriRequested?.Invoke(
                this,
                new ValueEventArgs<Uri>(uri));
        }
    }

    [RelayCommand]
    private void OpenModerationUserOnTwitch(string? login)
    {
        var normalizedLogin = login?.Trim().TrimStart('@');
        if (!string.IsNullOrWhiteSpace(normalizedLogin))
        {
            OpenUriRequested?.Invoke(
                this,
                new ValueEventArgs<Uri>(new Uri(
                    "https://www.twitch.tv/" + Uri.EscapeDataString(normalizedLogin))));
        }
    }

    [RelayCommand]
    private void ShowRecentMessages(ChatMessageItemViewModel? item)
    {
        if (item is not null)
        {
            OpenRecentMessages(item.Message.UserId, item.Message.UserLogin, item.UserLabel);
        }
    }

    [RelayCommand]
    private void ShowRecentMessagesForBannedUser(BannedUserViewModel? item)
    {
        if (item is not null)
        {
            OpenRecentMessages(item.Value.UserId, item.Value.UserLogin, item.UserLabel);
        }
    }

    [RelayCommand]
    private void ShowRecentMessagesForUnbanRequest(UnbanRequestViewModel? item)
    {
        if (item is not null)
        {
            OpenRecentMessages(item.Value.UserId, item.Value.UserLogin, item.UserLabel);
        }
    }

    [RelayCommand]
    private void ShowRecentMessagesForAutoMod(HeldAutoModMessageViewModel? item)
    {
        if (item is not null)
        {
            OpenRecentMessages(item.Value.UserId, item.Value.UserLogin, item.UserLabel);
        }
    }

    private void OpenRecentMessages(string userId, string login, string label)
    {
        var recent = Messages
            .Where(message =>
                (!string.IsNullOrWhiteSpace(userId) &&
                 string.Equals(message.Message.UserId, userId, StringComparison.Ordinal)) ||
                string.Equals(message.Message.UserLogin, login, StringComparison.OrdinalIgnoreCase))
            .TakeLast(50)
            .Select(message => new RecentUserMessageViewModel(message))
            .ToArray();

        RecentUserMessages.Clear();
        RecentUserMessages.AppendBatch(recent, 50);
        OnPropertyChanged(nameof(ShowNoRecentMessages));
        RecentMessagesUserLabel = string.IsNullOrWhiteSpace(login)
            ? label
            : $"{label} · @{login}";
        IsRecentMessagesOpen = true;
    }

    public async Task PrepareMessageUserProfileAsync(ChatMessageItemViewModel item)
    {
        if (item.ProfileImageResource is not null ||
            string.IsNullOrWhiteSpace(item.Message.UserLogin))
        {
            return;
        }

        if (item.Message.UserProfileImageUri is { } embeddedProfileUri)
        {
            item.ProfileImageResource = _imageCache.GetResource(embeddedProfileUri, 46);
            return;
        }

        var login = item.Message.UserLogin.Trim().TrimStart('@');
        var loadTask = _messageProfileImageLoads.GetOrAdd(login, LoadMessageProfileImageUriAsync);
        try
        {
            var imageUri = await loadTask;
            if (imageUri is null)
            {
                _messageProfileImageLoads.TryRemove(login, out _);
                return;
            }

            item.ProfileImageResource ??= _imageCache.GetResource(imageUri, 46);
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
            _messageProfileImageLoads.TryRemove(login, out _);
        }
    }

    private async Task<Uri?> LoadMessageProfileImageUriAsync(string login)
    {
        if (_authSession is not null)
        {
            try
            {
                var session = await EnsureValidSessionAsync(_authSession, _lifetimeCancellation.Token);
                var profile = await _chatApiClient.GetUserProfileAsync(
                    session,
                    login,
                    _lifetimeCancellation.Token);
                if (profile.ProfileImageUri is not null)
                {
                    return profile.ProfileImageUri;
                }
            }
            catch (Exception exception) when (IsRecoverableProfileLookupException(exception))
            {
                // A public Twitch lookup below keeps profile cards useful when
                // the signed-in session is unavailable or temporarily rejected.
            }
        }

        try
        {
            var publicProfile = await _chatApiClient.GetPublicChannelAsync(
                login,
                _lifetimeCancellation.Token);
            return Uri.TryCreate(publicProfile?.ThumbnailUrl, UriKind.Absolute, out var imageUri) &&
                   imageUri.Scheme is "http" or "https"
                ? imageUri
                : null;
        }
        catch (Exception exception) when (IsRecoverableProfileLookupException(exception))
        {
            return null;
        }
    }

    private static bool IsRecoverableProfileLookupException(Exception exception) =>
        exception is HttpRequestException or InvalidDataException or InvalidOperationException or
            ArgumentException or JsonException or AuthenticationContextChangedException;

    [RelayCommand(CanExecute = nameof(CanModerateMessage))]
    private async Task DeleteMessageAsync(ChatMessageItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.Message.IsYouTubeMessage)
        {
            _ = await RunYouTubeModerationActionAsync(
                item,
                token => _youTubeLiveChatClient!.DeleteMessageAsync(item.Message, token),
                ChatMessageModerationState.Deleted).ConfigureAwait(true);
        }
        else
        {
            await RunModerationActionAsync(
                item,
                (session, token) => _chatApiClient.DeleteMessageAsync(session, item.Message, token),
                ChatMessageModerationState.Deleted).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModerateMessage))]
    private void BanUser(ChatMessageItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        OpenModerationDialog(item, 0);
    }

    [RelayCommand(CanExecute = nameof(CanModerateMessage))]
    private void TimeoutTenMinutes(ChatMessageItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        OpenModerationDialog(item, 10);
    }

    [RelayCommand(CanExecute = nameof(CanModerateMessage))]
    private void CustomTimeout(ChatMessageItemViewModel? item)
    {
        if (item is not null)
        {
            OpenModerationDialog(item, 1);
        }
    }

    [RelayCommand]
    private void SetModerationDuration(int minutes) => ModerationDurationMinutes = Math.Clamp(minutes, 0, 20_160);

    [RelayCommand]
    private void CancelModeration()
    {
        if (IsModerationBusy)
        {
            return;
        }
        IsModerationDialogOpen = false;
        ModerationTarget = null;
        ModerationReason = string.Empty;
        ModerationActionError = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmModeration))]
    private async Task ConfirmModerationAsync()
    {
        var item = ModerationTarget;
        if (item is null)
        {
            return;
        }

        IsModerationBusy = true;
        ModerationActionError = string.Empty;
        int? seconds = ModerationDurationMinutes <= 0 ? null : ModerationDurationMinutes * 60;
        var moderationState = seconds is null
            ? ChatMessageModerationState.Banned
            : ChatMessageModerationState.TimedOut;
        bool completed;
        try
        {
            if (item.Message.IsYouTubeMessage)
            {
                completed = await RunYouTubeBanActionAsync(item, seconds, moderationState).ConfigureAwait(true);
            }
            else
            {
                completed = await RunModerationActionAsync(
                    item,
                    (session, token) => _chatApiClient.BanOrTimeoutUserAsync(
                        session,
                        item.Message,
                        seconds,
                        ModerationReason,
                        token),
                    moderationState,
                    markAllUserMessages: true).ConfigureAwait(true);
            }
        }
        finally
        {
            IsModerationBusy = false;
        }
        if (!completed)
        {
            ModerationActionError = StatusDetail;
            return;
        }
        IsModerationDialogOpen = false;
        ModerationTarget = null;
        ModerationReason = string.Empty;
        if (IsModerationPanelOpen && !item.Message.IsYouTubeMessage)
        {
            await RefreshModerationPanelAsync().ConfigureAwait(true);
        }
    }

    private void OpenModerationDialog(ChatMessageItemViewModel item, int durationMinutes)
    {
        IsSettingsOpen = false;
        IsLogViewerOpen = false;
        ModerationTarget = item;
        ModerationDurationMinutes = durationMinutes;
        ModerationReason = string.Empty;
        ModerationActionError = string.Empty;
        IsModerationDialogOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanModerateMessage))]
    private async Task RemovePunishmentAsync(ChatMessageItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.Message.IsYouTubeMessage)
        {
            var ban = YouTubeBans.FirstOrDefault(value =>
                string.Equals(value.Value.UserChannelId, item.Message.UserId, StringComparison.Ordinal));
            if (ban is null || _youTubeLiveChatClient is null)
            {
                return;
            }
            var removed = await RunYouTubeModerationActionAsync(
                item,
                token => _youTubeLiveChatClient.RemoveBanAsync(ban.Value.Id, token),
                moderationState: null).ConfigureAwait(true);
            if (removed)
            {
                _ = YouTubeBans.Remove(ban);
                OnPropertyChanged(nameof(HasYouTubeBans));
            }
        }
        else
        {
            await RunModerationActionAsync(
                item,
                (session, token) => _chatApiClient.RemovePunishmentAsync(session, item.Message, token),
                moderationState: null).ConfigureAwait(true);
        }
    }

    private void ClearMessageHistory(bool clearCatalogs = false)
    {
        Messages.Clear();
        VisibleMessages.Clear();
        TwitchVisibleMessages.Clear();
        YouTubeVisibleMessages.Clear();
        NotifyVisibleMessageStateChanged();
        _deferredMessages.Clear();
        while (_pendingMessages.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingMessageCount);
        }

        PinnedMessageAuthor = string.Empty;
        PinnedMessageText = string.Empty;
        _deferredPinnedMessageAuthor = string.Empty;
        _deferredPinnedMessageText = string.Empty;
        _pendingCatalogApplicationChannels.Clear();
        _seenChatters.Clear();
        _suspiciousMessageSamples.Clear();
        if (clearCatalogs)
        {
            _badgeCatalogs.Clear();
            _thirdPartyCatalogs.Clear();
            _thirdPartyCatalogBroadcasterIds.Clear();
            _thirdPartyRefreshRequested.Clear();
        }

        UnreadCount = 0;
        IsFollowingLatest = true;
        MessagesChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowLatest()
    {
        SetFollowingLatest(true);
        ScrollToLatestRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowCombinedChatView() => IsSplitChatView = false;

    [RelayCommand]
    private void ShowSplitChatView() => IsSplitChatView = true;

    [RelayCommand]
    private void ExitApplication() => ExitRequested?.Invoke(this, EventArgs.Empty);

    public void SetFollowingLatest(bool value)
    {
        if (IsFollowingLatest == value)
        {
            return;
        }

        IsFollowingLatest = value;
        if (value)
        {
            UnreadCount = 0;
            FlushDeferredMessages();
            StartPendingCatalogApplication();
        }
    }

    internal bool IsMessageBatchProcessingSuspended => _isMessageBatchProcessingSuspended;
    internal int PendingMessageCount => Volatile.Read(ref _pendingMessageCount);

    internal void SuspendMessageBatchProcessing() => _isMessageBatchProcessingSuspended = true;

    internal void SetUserScrolling(bool isUserScrolling) =>
        _messageBatchTimer.Interval = TimeSpan.FromMilliseconds(isUserScrolling ? 75 : 20);

    internal void ResumeMessageBatchProcessing()
    {
        if (!_isMessageBatchProcessingSuspended)
        {
            return;
        }

        _isMessageBatchProcessingSuspended = false;
        StartPendingCatalogApplication();
    }

#if DEBUG
    internal void LoadStressMessagesForTesting(int count = 10_000)
    {
        count = Math.Clamp(count, 1, 10_000);
        Channel = "witherchat-stress";
        var badgeCatalog = new Dictionary<string, TwitchBadgeDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["vip/1"] = new TwitchBadgeDefinition(
                "vip",
                "1",
                new Uri("https://static-cdn.jtvnw.net/badges/v1/b817aba4-fad8-49e2-b88a-7cc744dfa6ec/2"),
                "VIP")
        };
        var batch = new List<ChatMessageItemViewModel>(count);
        _badgeCatalogs["witherchat-stress"] = badgeCatalog;
        for (var index = 0; index < count; index++)
        {
            var message = CreateStressMessage(index);
            if (index == 14)
            {
                message = message with { IsPinned = true };
                PinnedMessageAuthor = message.UserLabel;
                PinnedMessageText = message.Text;
            }

            batch.Add(new ChatMessageItemViewModel(message, _imageCache, Texts, badgeCatalog, owner: this));
        }

        Messages.Clear();
        VisibleMessages.Clear();
        TwitchVisibleMessages.Clear();
        YouTubeVisibleMessages.Clear();
        _deferredMessages.Clear();
        AppendMessageBatch(batch, count);
        NotifyVisibleMessageStateChanged();
        IsFollowingLatest = true;
        MessagesChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void StartStressFeedForTesting() => _ = RunStressFeedForTestingAsync();

    private async Task RunStressFeedForTestingAsync()
    {
        try
        {
            for (var index = 10_000; index < 25_000; index += 10)
            {
                for (var batchIndex = 0; batchIndex < 10; batchIndex++)
                {
                    _pendingMessages.Enqueue(CreateStressMessage(index + batchIndex));
                    if (Interlocked.Increment(ref _pendingMessageCount) > MaximumPendingMessages &&
                        _pendingMessages.TryDequeue(out _))
                    {
                        Interlocked.Decrement(ref _pendingMessageCount);
                    }
                }

                await Task.Delay(50, _lifetimeCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
    }

    private ChatMessage CreateStressMessage(int index)
    {
        var english = string.Equals(Language, "en", StringComparison.Ordinal);
        var prefix = english ? $"Test message {index + 1} " : $"Тестовое сообщение {index + 1} ";
        var suffix = english
            ? " — scrolling and icons must remain stable."
            : " — прокрутка и значки должны оставаться стабильными.";
        var text = index % 17 == 0
            ? (english
                ? "Long test message for line wrapping and virtualization "
                : "Длинное тестовое сообщение для проверки переноса строк и виртуализации ") +
              string.Join(' ', Enumerable.Repeat("WitherChat", 18))
            : prefix + "Kappa" + suffix;
        return new ChatMessage
        {
            Id = "stress-" + index,
            Channel = "witherchat-stress",
            BroadcasterId = "1",
            UserLogin = "tester" + index,
            DisplayName = "Tester_" + index,
            Text = text,
            Timestamp = DateTimeOffset.Now.AddMilliseconds(index),
            UserColor = index % 2 == 0 ? "#57E3B0" : "#9BB6FF",
            Badges = [new ChatBadge("vip", "1")],
            Parts =
            [
                ChatMessagePart.PlainText(prefix),
                ChatMessagePart.TwitchEmote("Kappa", "25"),
                ChatMessagePart.PlainText(suffix)
            ]
        };
    }
#endif

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetimeCancellation.CancelAsync().ConfigureAwait(false);
        var momentTasks = new[]
        {
            ConfirmSaveMomentCommand.ExecutionTask,
            DeleteMomentCommand.ExecutionTask
        }.Where(task => task is not null).Cast<Task>().ToArray();
        if (momentTasks.Length > 0)
            await Task.WhenAll(momentTasks).ConfigureAwait(false);
        _messageBatchTimer.Stop();
        _messageBatchTimer.Tick -= OnMessageBatchTimerTick;
        _sessionValidationTimer.Stop();
        _sessionValidationTimer.Tick -= OnSessionValidationTimerTick;
        _streamStatusTimer.Stop();
        _streamStatusTimer.Tick -= OnStreamStatusTimerTick;
        _pinnedMessageTimer.Stop();
        _pinnedMessageTimer.Tick -= OnPinnedMessageTimerTick;
        _filterRefreshTimer.Stop();
        _filterRefreshTimer.Tick -= OnFilterRefreshTimerTick;
        _donationDisplayTimer.Stop();
        _donationDisplayTimer.Tick -= OnDonationDisplayTimerTick;
        _chatClient.MessageReceived -= OnMessageReceived;
        _chatClient.StatusChanged -= OnStatusChanged;
        _eventSubClient.MessageReceived -= OnMessageReceived;
        _eventSubClient.StreamEventReceived -= OnStreamEventReceived;
        _eventSubClient.AutoModMessageHeld -= OnAutoModMessageHeld;
        _eventSubClient.AutoModMessageResolved -= OnAutoModMessageResolved;
        _eventSubClient.UserBanned -= OnEventSubUserBanned;
        _eventSubClient.UserUnbanned -= OnEventSubUserUnbanned;
        _eventSubClient.UnbanRequestChanged -= OnEventSubUnbanRequestChanged;
        _eventSubClient.SharedChatStateChanged -= OnSharedChatStateChanged;
        _eventSubClient.ChatMessageDeleted -= OnEventSubChatMessageDeleted;
        _eventSubClient.UserMessagesCleared -= OnEventSubUserMessagesCleared;
        _eventSubClient.ChatCleared -= OnEventSubChatCleared;
        _chatLogWriter.StatusChanged -= OnChatLogWriterStatusChanged;
        if (_youTubeLiveChatClient is not null)
        {
            _youTubeLiveChatClient.MessageReceived -= OnMessageReceived;
            _youTubeLiveChatClient.StreamEventReceived -= OnStreamEventReceived;
            _youTubeLiveChatClient.StatusChanged -= OnYouTubeStatusChanged;
            _youTubeLiveChatClient.SessionUpdated -= OnYouTubeSessionUpdated;
            _youTubeLiveChatClient.MessageDeleted -= OnYouTubeMessageDeleted;
        }
        if (_donationAlertsClient is not null)
        {
            _donationAlertsClient.DonationReceived -= OnDonationReceived;
            _donationAlertsClient.StatusChanged -= OnDonationAlertsStatusChanged;
        }
        Task? donationHistoryRefreshTask;
        lock (_donationHistoryRefreshGate)
        {
            _donationHistoryRefreshRequested = false;
            donationHistoryRefreshTask = _donationHistoryRefreshTask;
        }
        if (donationHistoryRefreshTask is not null)
        {
            try
            {
                await donationHistoryRefreshTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (_donationPlaybackCoordinator is not null)
        {
            _donationPlaybackCoordinator.StateChanged -= OnDonationPlaybackStateChanged;
            await _donationPlaybackCoordinator.DisposeAsync().ConfigureAwait(false);
        }
        var youTubeAuthorizationTask = _youTubeAuthorizationCompletion?.Task;
        var donationAlertsAuthorizationTask = _donationAlertsAuthorizationCompletion?.Task;
        if (_youTubeAuthorizationCancellation is { } youTubeAuthorizationCancellation)
        {
            await youTubeAuthorizationCancellation.CancelAsync().ConfigureAwait(false);
        }
        if (_donationAlertsAuthorizationCancellation is { } donationAlertsAuthorizationCancellation)
        {
            await donationAlertsAuthorizationCancellation.CancelAsync().ConfigureAwait(false);
        }
        CancelAuthorizationCore();
        CancelChannelSearch();
        CancelFollowedChannelsRefresh();
        var authorizationTasks = new[]
        {
            _authorizationCompletion?.Task,
            youTubeAuthorizationTask,
            donationAlertsAuthorizationTask
        }.Where(task => task is not null).Cast<Task>().ToArray();
        if (authorizationTasks.Length > 0)
        {
            await Task.WhenAll(authorizationTasks).ConfigureAwait(false);
        }
        Task[] settingsSaveTasks;
        lock (_settingsSaveTasksLock)
        {
            settingsSaveTasks = _settingsSaveTasks.ToArray();
        }

        await Task.WhenAll(settingsSaveTasks).ConfigureAwait(false);
        await SaveSettingsSafeAsync(CreateSettingsSnapshot()).ConfigureAwait(false);
        await _chatClient.DisposeAsync().ConfigureAwait(false);
        await _eventSubClient.DisposeAsync().ConfigureAwait(false);
        if (_youTubeLiveChatClient is not null)
        {
            await _youTubeLiveChatClient.DisposeAsync().ConfigureAwait(false);
        }
        if (_donationAlertsClient is not null)
        {
            await _donationAlertsClient.DisposeAsync().ConfigureAwait(false);
        }
        if (_donationAlertsObsController is IDisposable donationAlertsObsController)
        {
            donationAlertsObsController.Dispose();
        }
        await _chatLogWriter.DisposeAsync().ConfigureAwait(false);
        await _obsOverlayServer.DisposeAsync().ConfigureAwait(false);
        await _moderationCacheStore.DisposeAsync().ConfigureAwait(false);
        if (_streamMomentStore is not null)
            await _streamMomentStore.DisposeAsync().ConfigureAwait(false);
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        _sessionLock.Release();
        await _badgeRefreshLock.WaitAsync().ConfigureAwait(false);
        _badgeRefreshLock.Release();
        await _thirdPartyRefreshLock.WaitAsync().ConfigureAwait(false);
        _thirdPartyRefreshLock.Release();
        await _streamRefreshLock.WaitAsync().ConfigureAwait(false);
        _streamRefreshLock.Release();
        await _pinnedRefreshLock.WaitAsync().ConfigureAwait(false);
        _pinnedRefreshLock.Release();
        _chatApiClient.Dispose();
        _authService.Dispose();
        _youTubeAuthService?.Dispose();
        _donationAlertsAuthService?.Dispose();
        _thirdPartyEmoteCatalogService.Dispose();
        _imageCache.LoadingStateChanged -= OnImageCacheLoadingStateChanged;
        BannedUsers.CollectionChanged -= OnModerationItemsChanged;
        UnbanRequests.CollectionChanged -= OnModerationItemsChanged;
        PendingAutoModMessages.CollectionChanged -= OnModerationItemsChanged;
        _imageCache.Dispose();
        _settingsStore.Dispose();
        _lifetimeCancellation.Dispose();
        _sessionLock.Dispose();
        _badgeRefreshLock.Dispose();
        _thirdPartyRefreshLock.Dispose();
        _streamRefreshLock.Dispose();
        _pinnedRefreshLock.Dispose();
    }

    partial void OnChannelChanged(string value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        ReconnectCommand.NotifyCanExecuteChanged();
        SendMessageCommand.NotifyCanExecuteChanged();
        CreateTwitchClipCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCreateTwitchClip));
        OnPropertyChanged(nameof(TwitchClipButtonTip));
        IsStreamStatusKnown = false;
        IsStreamLive = false;
        StreamViewerCount = 0;
        InvalidateModerationContext();
        OnPropertyChanged(nameof(SharedChatStatusLabel));
        OnPropertyChanged(nameof(IsSharedChatActive));
        OnPropertyChanged(nameof(ShowComposerToggle));
    }

    private sealed class AuthenticationContextChangedException(CancellationToken token)
        : OperationCanceledException("Authentication context changed.", token)
    {
    }

    private void InvalidateModerationContext()
    {
        Interlocked.Increment(ref _moderationContextGeneration);
        CanModerate = false;
        IsModerationPanelBusy = false;
        IsModerationDialogOpen = false;
        ModerationTarget = null;
        ModerationPanelStatus = string.Empty;
        _moderationBroadcasterId = string.Empty;
        BannedUsers.Clear();
        UnbanRequests.Clear();
        PendingAutoModMessages.Clear();
    }

    private bool IsModerationContextCurrent(long generation, string channel, TwitchAuthSession session) =>
        !_disposed &&
        generation == Volatile.Read(ref _moderationContextGeneration) &&
        string.Equals(channel, NormalizeChannel(Channel), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(session.UserId, _authSession?.UserId, StringComparison.Ordinal);

    private void OnModerationItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(nameof(ShowNoAutoModMessages));
        OnPropertyChanged(nameof(ShowNoBannedUsers));
        OnPropertyChanged(nameof(ShowNoUnbanRequests));
        if (ReferenceEquals(sender, UnbanRequests))
        {
            OnPropertyChanged(nameof(VisibleUnbanRequests));
        }
    }

    private void NotifyModerationContextChanged()
    {
        OnPropertyChanged(nameof(ActiveChannelLabel));
        OnPropertyChanged(nameof(ModerationChannelLabel));
        OnPropertyChanged(nameof(CanUnbanUsers));
        OnPropertyChanged(nameof(CanUnbanByLogin));
        OnPropertyChanged(nameof(CanBanByLogin));
        UnbanByLoginCommand.NotifyCanExecuteChanged();
        BanByLoginCommand.NotifyCanExecuteChanged();
    }

    private void OnImageCacheLoadingStateChanged(object? sender, EventArgs eventArgs)
    {
        var isLoading = _showInitialMediaLoading && _imageCache.IsLoading;
        if (Dispatcher.UIThread.CheckAccess())
        {
            IsChatMediaLoading = isLoading;
            return;
        }

        Dispatcher.UIThread.Post(() =>
            IsChatMediaLoading = _showInitialMediaLoading && _imageCache.IsLoading);
    }

    partial void OnIsHeaderExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowHeaderPanel));

    partial void OnIsComposerExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowComposerPanel));
        OnPropertyChanged(nameof(ComposerToggleTip));
    }

    partial void OnIsCompactModeChanged(bool value)
    {
        OnPropertyChanged(nameof(CompactModeTip));
        OnPropertyChanged(nameof(ShowHeaderPanel));
        OnPropertyChanged(nameof(ShowHeaderToggle));
        OnPropertyChanged(nameof(ShowComposerPanel));
        OnPropertyChanged(nameof(ShowComposerToggle));
        OnPropertyChanged(nameof(ShowFullChatMetadata));
        OnPropertyChanged(nameof(ShowFullChatTimestamps));
        OnPropertyChanged(nameof(ShowFullChatBadges));
        OnPropertyChanged(nameof(ShowCompactChatBadges));
        OnPropertyChanged(nameof(ChatLayoutMargin));
        OnPropertyChanged(nameof(ChatMetadataVerticalAlignment));
        OnPropertyChanged(nameof(WelcomeCardWidth));
        OnPropertyChanged(nameof(ShowPinnedMessageCard));
        OnPropertyChanged(nameof(ShowCombinedMessageList));
        OnPropertyChanged(nameof(ShowSplitMessageLists));
        OnPropertyChanged(nameof(ShowChatEmptyState));
    }

    partial void OnIsSplitChatViewChanged(bool value)
    {
        QueueSettingsSave();
        MessagesChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsChatDisplayPausedChanged(bool value)
    {
        if (value)
        {
            ProtectionSuppressedCount = 0;
        }
    }

    partial void OnSelectedSavedChannelChanged(ChannelSessionViewModel? value)
    {
        foreach (var channel in SavedChannels)
        {
            channel.IsActive = ReferenceEquals(channel, value);
        }

        if (value is null || string.Equals(Channel, value.Login, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Channel = value.Login;
        IsChannelEditorOpen = false;
        RebuildVisibleMessages();
        _ = RefreshChannelAssetsForSelectionAsync(value.Login);
        QueueSettingsSave();
    }

    private async Task RefreshChannelAssetsForSelectionAsync(string channel)
    {
        var session = _authSession;
        if (session is null)
        {
            return;
        }

        await RefreshBadgeCatalogSafeAsync(session, channel);
        await RefreshStreamStatusSafeAsync(session, channel);
        await RefreshModerationAccessSafeAsync();
        await RefreshPinnedMessageSafeAsync();
    }

    partial void OnMessageSearchTextChanged(string value)
    {
        _normalizedMessageSearch = value.Trim();
        ScheduleFilterRefresh();
    }

    partial void OnUserFilterChanged(string value)
    {
        _normalizedUserFilter = value.Trim().TrimStart('@');
        ScheduleFilterRefresh();
    }

    partial void OnConnectionStateChanged(ChatConnectionState value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        ReconnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        SendMessageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanWatchChannel));
    }

    partial void OnIsAuthorizingChanged(bool value)
    {
        StartAuthorizationCommand.NotifyCanExecuteChanged();
        ReauthorizeFollowedChannelsCommand.NotifyCanExecuteChanged();
    }
    partial void OnComposerTextChanged(string value) => SendMessageCommand.NotifyCanExecuteChanged();
    partial void OnIsSendingChanged(bool value) => SendMessageCommand.NotifyCanExecuteChanged();

    partial void OnOnboardingStepChanged(int value)
    {
        OnPropertyChanged(nameof(OnboardingStepNumber));
        OnPropertyChanged(nameof(OnboardingProgressText));
        OnPropertyChanged(nameof(OnboardingTitle));
        OnPropertyChanged(nameof(OnboardingDescription));
        OnPropertyChanged(nameof(OnboardingHint));
        OnPropertyChanged(nameof(OnboardingNextLabel));
        OnPropertyChanged(nameof(CanGoBackInOnboarding));
        OnPropertyChanged(nameof(IsOnboardingWelcomeStep));
        OnPropertyChanged(nameof(IsOnboardingHeaderStep));
        OnPropertyChanged(nameof(IsOnboardingChannelsStep));
        OnPropertyChanged(nameof(IsOnboardingChatStep));
        OnPropertyChanged(nameof(IsOnboardingComposerStep));
        OnPropertyChanged(nameof(IsOnboardingToolsStep));
        OnPropertyChanged(nameof(IsOnboardingLogsStep));
        OnPropertyChanged(nameof(IsOnboardingOverlayStep));
        OnPropertyChanged(nameof(IsOnboardingSettingsStep));
        OnPropertyChanged(nameof(IsOnboardingCompactStep));
        OnPropertyChanged(nameof(ShowOnboardingComposerPreview));
        OnPropertyChanged(nameof(ShowOnboardingChannelPreview));
    }

    partial void OnActiveTutorialTopicChanged(TutorialTopic value)
    {
        OnPropertyChanged(nameof(OnboardingTotalSteps));
        OnPropertyChanged(nameof(OnboardingProgressText));
        OnPropertyChanged(nameof(OnboardingTitle));
        OnPropertyChanged(nameof(OnboardingDescription));
        OnPropertyChanged(nameof(OnboardingHint));
        OnPropertyChanged(nameof(OnboardingNextLabel));
        OnPropertyChanged(nameof(TutorialEyebrow));
        OnPropertyChanged(nameof(TutorialCloseLabel));
        OnPropertyChanged(nameof(IsContextTutorial));
        OnPropertyChanged(nameof(IsMainWindowTutorial));
        OnPropertyChanged(nameof(IsDonationTutorial));
        OnPropertyChanged(nameof(ShowMainWindowTutorial));
        OnPropertyChanged(nameof(ShowDonationTutorial));
        OnPropertyChanged(nameof(IsOnboardingWelcomeStep));
        OnPropertyChanged(nameof(IsOnboardingHeaderStep));
        OnPropertyChanged(nameof(IsOnboardingChannelsStep));
        OnPropertyChanged(nameof(IsOnboardingChatStep));
        OnPropertyChanged(nameof(IsOnboardingComposerStep));
        OnPropertyChanged(nameof(IsOnboardingToolsStep));
        OnPropertyChanged(nameof(IsOnboardingLogsStep));
        OnPropertyChanged(nameof(IsOnboardingOverlayStep));
        OnPropertyChanged(nameof(IsOnboardingSettingsStep));
        OnPropertyChanged(nameof(IsOnboardingCompactStep));
        OnPropertyChanged(nameof(ShowOnboardingComposerPreview));
        OnPropertyChanged(nameof(ShowOnboardingChannelPreview));
    }

    partial void OnCanModerateChanged(bool value)
    {
        OnPropertyChanged(nameof(CanModerateAny));
        OnPropertyChanged(nameof(CanConfirmModeration));
        OnPropertyChanged(nameof(CanApplyProtection));
        BanByLoginCommand.NotifyCanExecuteChanged();
        DeleteMessageCommand.NotifyCanExecuteChanged();
        BanUserCommand.NotifyCanExecuteChanged();
        TimeoutTenMinutesCommand.NotifyCanExecuteChanged();
        CustomTimeoutCommand.NotifyCanExecuteChanged();
        RemovePunishmentCommand.NotifyCanExecuteChanged();
        ConfirmModerationCommand.NotifyCanExecuteChanged();
    }

    partial void OnThemeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedThemeOption));
        RefreshMessagePresentation();
        if (SelectedLogFile is { } selectedLogFile && _allChatLogEntries.Count > 0)
        {
            ApplyChatLogEntryPresentation(_allChatLogEntries, selectedLogFile.Channel);
        }
        ThemeChanged?.Invoke(this, new ValueEventArgs<string>(value));
        QueueSettingsSave();
    }

    partial void OnLanguageChanged(string value)
    {
        ApplyLanguage(value);
        OnPropertyChanged(nameof(SelectedLanguageOption));
#if DEBUG
        if (_suppressLanguageSave)
        {
            return;
        }
#endif
        QueueSettingsSave();
    }
    partial void OnAlwaysOnTopChanged(bool value) => QueueSettingsSave();
    partial void OnToastNotificationsChanged(bool value) => QueueSettingsSave();
    partial void OnCloseToTrayChanged(bool value)
    {
        OnPropertyChanged(nameof(CloseWindowTip));
        QueueSettingsSave();
    }

    partial void OnMessageLimitChanged(int value)
    {
        MessageLimit = Math.Clamp(value, 250, 10_000);
        Messages.TrimToMaximum(MessageLimit * 3);
        RebuildVisibleMessages();
        TrimDeferredMessages();
        QueueSettingsSave();
    }

    partial void OnChatFontSizeChanged(double value)
    {
        ChatFontSize = Math.Clamp(value, 11, 28);
        QueueSettingsSave();
    }

    partial void OnShowTimestampsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFullChatTimestamps));
        QueueSettingsSave();
    }

    partial void OnShowBadgesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFullChatBadges));
        OnPropertyChanged(nameof(ShowCompactChatBadges));
        QueueSettingsSave();
    }

    partial void OnEnableTwitchEmotesChanged(bool value)
    {
        RefreshMessagePresentation();
        QueueSettingsSave();
    }

    partial void OnEnableBttvEmotesChanged(bool value)
    {
        RefreshMessagePresentation();
        QueueSettingsSave();
    }

    partial void OnEnableSevenTvEmotesChanged(bool value)
    {
        RefreshMessagePresentation();
        QueueSettingsSave();
    }

    partial void OnShowChannelPointRedemptionsChanged(bool value)
    {
        RebuildVisibleMessages();
        QueueSettingsSave();
    }

    partial void OnUiFontFamilyChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedFontOption));
        FontFamilyChanged?.Invoke(this, new ValueEventArgs<string>(value));
        QueueSettingsSave();
    }

    partial void OnWindowControlsOnRightChanged(bool value)
    {
        OnPropertyChanged(nameof(WindowControlsAlignment));
        OnPropertyChanged(nameof(WindowControlsColumn));
        OnPropertyChanged(nameof(CompactButtonColumn));
        OnPropertyChanged(nameof(WindowControlsFlowDirection));
        OnPropertyChanged(nameof(CompactButtonMargin));
        OnPropertyChanged(nameof(SelectedWindowControlsPositionOption));
        QueueSettingsSave();
    }

    partial void OnWindowControlsStyleChanged(string value)
    {
        OnPropertyChanged(nameof(UseMacWindowControls));
        OnPropertyChanged(nameof(UseWindowsWindowControls));
        OnPropertyChanged(nameof(SelectedWindowControlsStyleOption));
        QueueSettingsSave();
    }

    partial void OnMessageVisualThemeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedMessageVisualThemeOption));
        QueueOverlayApply();
    }

    partial void OnReduceMotionChanged(bool value) => QueueSettingsSave();

    partial void OnUseCustomClientIdChanged(bool value)
    {
        TryApplyOAuthConfiguration();
        QueueSettingsSave();
    }

    partial void OnClientIdChanged(string value)
    {
        TryApplyOAuthConfiguration();
        QueueSettingsSave();
    }

    partial void OnRedirectUriChanged(string value)
    {
        TryApplyOAuthConfiguration();
        QueueSettingsSave();
    }

    partial void OnDonationAlertsAutoOpenWindowChanged(bool value) => QueueSettingsSave();

    partial void OnEnableObsOverlayChanged(bool value) => QueueOverlayApply();
    partial void OnOverlayPortChanged(int value)
    {
        if (value != Math.Clamp(value, 1024, 65535))
        {
            OverlayPort = Math.Clamp(value, 1024, 65535);
            return;
        }
        QueueOverlayApply();
    }
    partial void OnOverlayMaxMessagesChanged(int value)
    {
        if (value != Math.Clamp(value, 1, 100))
        {
            OverlayMaxMessages = Math.Clamp(value, 1, 100);
            return;
        }
        QueueOverlayApply();
    }
    partial void OnOverlayFontSizeChanged(double value)
    {
        if (value != Math.Clamp(value, 10, 72))
        {
            OverlayFontSize = Math.Clamp(value, 10, 72);
            return;
        }
        QueueOverlayApply();
    }
    partial void OnOverlayShowTimestampsChanged(bool value) => QueueOverlayApply();
    partial void OnOverlayShowBadgesChanged(bool value) => QueueOverlayApply();
    partial void OnOverlayShowEmotesChanged(bool value) => QueueOverlayApply();
    partial void OnOverlayFadeOutSecondsChanged(int value)
    {
        var normalized = Math.Clamp(value, 0, 600);
        if (value != normalized)
        {
            OverlayFadeOutSeconds = normalized;
            return;
        }
        QueueOverlayApply();
    }
    partial void OnOverlayTextShadowChanged(bool value) => QueueOverlayApply();
    partial void OnOverlayTextOutlineChanged(bool value) => QueueOverlayApply();
    partial void OnOverlayDarkBackgroundChanged(bool value) => QueueOverlayApply();
    partial void OnOverlayBackgroundOpacityChanged(double value)
    {
        var normalized = Math.Clamp(value, 0, 1);
        if (Math.Abs(value - normalized) > double.Epsilon)
        {
            OverlayBackgroundOpacity = normalized;
            return;
        }
        QueueOverlayApply();
    }
    partial void OnOverlayAlignChanged(string value) => QueueOverlayApply();
    partial void OnViewerCountRefreshIntervalSecondsChanged(int value)
    {
        var normalized = Math.Clamp(
            value,
            WitherChatSettings.MinimumViewerCountRefreshIntervalSeconds,
            WitherChatSettings.MaximumViewerCountRefreshIntervalSeconds);
        if (value != normalized)
        {
            ViewerCountRefreshIntervalSeconds = normalized;
            return;
        }
        _streamStatusTimer.Interval = TimeSpan.FromSeconds(normalized);
        QueueSettingsSave();
    }
    partial void OnEnableChatLoggingChanged(bool value)
    {
        ApplyChatLoggingSettings();
        QueueSettingsSave();
    }
    partial void OnSaveChatLogTxtChanged(bool value)
    {
        ApplyChatLoggingSettings();
        QueueSettingsSave();
    }
    partial void OnLogChatBadgesChanged(bool value)
    {
        ApplyChatLoggingSettings();
        QueueSettingsSave();
    }
    partial void OnLogChannelPointRedemptionsChanged(bool value)
    {
        ApplyChatLoggingSettings();
        QueueSettingsSave();
    }
    partial void OnChatLogsFolderChanged(string value)
    {
        ApplyChatLoggingSettings();
        QueueSettingsSave();
    }
    partial void OnMaxLogViewerMessagesChanged(int value)
    {
        var normalized = Math.Clamp(value, 100, 50_000);
        if (value != normalized)
        {
            MaxLogViewerMessages = normalized;
            return;
        }
        QueueSettingsSave();
    }

    private void ApplyChatLoggingSettings()
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(ChatLogsFolder)
                ? _defaultChatLogDirectory
                : ChatLogsFolder;
            _chatLogWriter.Configure(
                EnableChatLogging,
                directory,
                SaveChatLogTxt,
                LogChatBadges,
                LogChannelPointRedemptions);
            OnPropertyChanged(nameof(ChatLogDirectory));
            OnPropertyChanged(nameof(ChatLogsFolderDisplay));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StatusDetail = Texts.SettingsSaveFailed(AppDiagnostics.GetUserMessage(exception));
        }
    }

    private void QueueOverlayApply()
    {
        QueueSettingsSave();
        _ = ApplyOverlaySettingsAsync();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The optional local overlay must not terminate the desktop application.")]
    private async Task ApplyOverlaySettingsAsync()
    {
        try
        {
            var alignment = OverlayAlign switch
            {
                "center" => "center",
                "right" => "flex-end",
                _ => "flex-start"
            };
            await _obsOverlayServer.ConfigureAsync(
                EnableObsOverlay,
                new ObsOverlayOptions(
                    OverlayPort,
                    OverlayMaxMessages,
                    OverlayFontSize,
                    OverlayShowTimestamps,
                    OverlayShowBadges,
                    OverlayShowEmotes,
                    Math.Clamp(OverlayFadeOutSeconds, 0, 600),
                    OverlayTextShadow,
                    OverlayTextOutline,
                    OverlayDarkBackground,
                    Math.Clamp(OverlayBackgroundOpacity, 0, 1),
                    alignment,
                    MessageVisualTheme)).ConfigureAwait(true);
            if (EnableObsOverlay && _obsOverlayServer.IsRunning)
            {
                OverlayStatusText = OverlayUrl;
            }
        }
        catch (Exception exception)
        {
            EnableObsOverlay = false;
            OverlayStatusText = Texts.OverlayStartFailed(AppDiagnostics.GetUserMessage(exception));
            StatusDetail = OverlayStatusText;
        }
    }

    partial void OnSelectedLogFileChanged(ChatLogFileViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedLogTitle));
        OnPropertyChanged(nameof(SelectedLogMeta));
        _ = LoadChatLogAsync(value);
    }

    partial void OnLogSearchTextChanged(string value) => ApplyChatLogFilter();
    partial void OnLogUserFilterChanged(string value) => ApplyChatLogFilter();
    partial void OnLogRoleFilterChanged(string value) => ApplyChatLogFilter();
    partial void OnSelectedLogChannelChanged(string? value) => RefreshVisibleChatLogFiles();

    private async Task RefreshChatLogFilesAsync()
    {
        try
        {
            Directory.CreateDirectory(_chatLogWriter.LogDirectory);
            var logRoot = _chatLogWriter.LogDirectory;
            var files = await Task.Run(() => Directory
                .EnumerateFiles(
                    logRoot,
                    "*.*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    })
                .Where(path => path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith("chat.txt", StringComparison.OrdinalIgnoreCase))
                .GroupBy(path => Path.Combine(
                    Path.GetDirectoryName(path) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(path)), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.FirstOrDefault(path => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                                 ?? group.First())
                .Select(path => CreateChatLogFile(logRoot, path))
                .OrderByDescending(file => file.Date)
                .ThenBy(file => file.Channel, StringComparer.OrdinalIgnoreCase)
                .ToArray()).ConfigureAwait(true);
            ChatLogFiles.Clear();
            ChatLogFiles.AppendBatch(files, 10_000);
            ChatLogChannels.Clear();
            ChatLogChannels.AppendBatch(files.Select(file => file.Channel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(), 10_000);
            OnPropertyChanged(nameof(HasChatLogFiles));
            if (SelectedLogChannel is null || !ChatLogChannels.Contains(SelectedLogChannel))
            {
                SelectedLogChannel = ChatLogChannels.FirstOrDefault();
            }
            else
            {
                RefreshVisibleChatLogFiles();
            }
            if (SelectedLogFile is null)
            {
                _allChatLogEntries = [];
                ChatLogEntries.Clear();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          NotSupportedException or System.Security.SecurityException)
        {
            StatusDetail = Texts.LogOpenFailed;
        }
    }

    private async Task LoadChatLogAsync(ChatLogFileViewModel? file)
    {
        if (file is null)
        {
            _allChatLogEntries = [];
            ChatLogEntries.Clear();
            return;
        }

        try
        {
            var root = Path.GetFullPath(_chatLogWriter.LogDirectory) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(file.Path);
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!IsSafeLogPath(root, path, pathComparison))
            {
                return;
            }

            var entries = await Task.Run(() => File.ReadLines(path)
                .TakeLast(MaxLogViewerMessages)
                .Select(ChatLogEntryViewModel.Parse)
                .ToArray()).ConfigureAwait(true);
            if (!Equals(SelectedLogFile, file))
            {
                return;
            }

            _allChatLogEntries = entries;
            ApplyChatLogFilter();
            _ = CompleteChatLogPresentationAsync(file, entries);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          NotSupportedException or System.Security.SecurityException)
        {
            StatusDetail = Texts.LogOpenFailed;
        }
    }

    private static bool IsSafeLogPath(string rootWithSeparator, string path, StringComparison comparison)
    {
        if (!path.StartsWith(rootWithSeparator, comparison))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootWithSeparator));
        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory) && !string.Equals(directory, root, comparison))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            directory = Path.GetDirectoryName(directory);
        }
        return string.Equals(directory, root, comparison);
    }

    private void ApplyChatLogEntryPresentation(
        IEnumerable<ChatLogEntryViewModel> entries,
        string channel)
    {
        channel = NormalizeChannel(channel);
        _badgeCatalogs.TryGetValue(channel, out var badgeCatalog);
        _thirdPartyCatalogs.TryGetValue(channel, out var thirdPartyCatalog);
        foreach (var entry in entries)
        {
            entry.ApplyPresentation(
                _imageCache,
                badgeCatalog,
                thirdPartyCatalog,
                EnableTwitchEmotes,
                EnableBttvEmotes,
                EnableSevenTvEmotes,
                Texts,
                useLightTwitchTheme: string.Equals(Theme, "Light", StringComparison.Ordinal));
        }
    }

    private async Task CompleteChatLogPresentationAsync(
        ChatLogFileViewModel file,
        IReadOnlyList<ChatLogEntryViewModel> entries)
    {
        if (!await ApplyChatLogEntryPresentationInBatchesAsync(file, entries).ConfigureAwait(false))
        {
            return;
        }

        await RefreshChatLogVisualCatalogsAsync(file, entries).ConfigureAwait(false);
    }

    private async Task<bool> ApplyChatLogEntryPresentationInBatchesAsync(
        ChatLogFileViewModel file,
        IReadOnlyList<ChatLogEntryViewModel> entries)
    {
        for (var start = 0; start < entries.Count; start += LogPresentationBatchSize)
        {
            if (_disposed || !Equals(SelectedLogFile, file))
            {
                return false;
            }

            var offset = start;
            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    if (_disposed || !Equals(SelectedLogFile, file))
                    {
                        return;
                    }

                    ApplyChatLogEntryPresentation(
                        entries.Skip(offset).Take(LogPresentationBatchSize),
                        file.Channel);
                },
                DispatcherPriority.Background);
            await Task.Delay(1).ConfigureAwait(false);
        }

        return !_disposed && Equals(SelectedLogFile, file);
    }

    private async Task RefreshChatLogVisualCatalogsAsync(
        ChatLogFileViewModel file,
        IReadOnlyList<ChatLogEntryViewModel> entries)
    {
        var channel = NormalizeChannel(file.Channel);
        if (channel.Length == 0 || _disposed)
        {
            return;
        }

        var broadcasterId = entries
            .Select(entry => entry.BroadcasterId)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? SavedChannels.FirstOrDefault(saved =>
                string.Equals(saved.Login, channel, StringComparison.OrdinalIgnoreCase))?.BroadcasterId
            ?? string.Empty;
        if (_authSession is { } session && !_badgeCatalogs.ContainsKey(channel))
        {
            await RefreshBadgeCatalogSafeAsync(session, channel).ConfigureAwait(false);
        }

        if (NeedsThirdPartyCatalogRefresh(channel, broadcasterId))
        {
            await RefreshThirdPartyCatalogSafeAsync(channel, broadcasterId).ConfigureAwait(false);
        }

        _ = await ApplyChatLogEntryPresentationInBatchesAsync(file, entries).ConfigureAwait(false);
    }

    private void ApplyChatLogFilter()
    {
        var search = LogSearchText.Trim();
        var user = LogUserFilter.Trim().TrimStart('@');
        var role = LogRoleFilter.Trim();
        var entries = _allChatLogEntries.Where(entry =>
            (search.Length == 0 || entry.RawText.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (user.Length == 0 || entry.User.Contains(user, StringComparison.OrdinalIgnoreCase)) &&
            (role.Length == 0 || string.Equals(entry.Role, role, StringComparison.OrdinalIgnoreCase))).ToArray();
        ChatLogEntries.Clear();
        ChatLogEntries.AppendBatch(entries, MaxLogViewerMessages);
    }

    private void RefreshVisibleChatLogFiles()
    {
        var channel = SelectedLogChannel;
        var files = string.IsNullOrWhiteSpace(channel)
            ? Array.Empty<ChatLogFileViewModel>()
            : ChatLogFiles.Where(file => string.Equals(file.Channel, channel, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.Date)
                .ToArray();
        VisibleChatLogFiles.Clear();
        VisibleChatLogFiles.AppendBatch(files, 10_000);
        SelectedLogFile = files.FirstOrDefault();
    }

    private static ChatLogFileViewModel CreateChatLogFile(string root, string path)
    {
        var info = new FileInfo(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var separator = stem.LastIndexOf('-');
        var channel = separator > 0 ? stem[..separator] : stem;
        if (string.Equals(stem, "chat", StringComparison.OrdinalIgnoreCase))
        {
            var relativeParts = Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            channel = relativeParts.Length > 1 ? relativeParts[0] : "chat";
            var sessionDirectory = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
            if (sessionDirectory.EndsWith("_chat", StringComparison.OrdinalIgnoreCase) &&
                DateTime.TryParseExact(
                    sessionDirectory[..^5],
                    "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var sessionDate))
            {
                return new ChatLogFileViewModel(info.FullName, channel, sessionDate, info.Length);
            }
        }
        var date = separator > 0 && DateTime.TryParseExact(
            stem[(separator + 1)..],
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed)
            ? parsed
            : info.LastWriteTime.Date;
        return new ChatLogFileViewModel(info.FullName, channel, date, info.Length);
    }

    private void ScheduleFilterRefresh()
    {
        _filterRefreshTimer.Stop();
        _filterRefreshTimer.Start();
    }

    private void OnFilterRefreshTimerTick(object? sender, EventArgs eventArgs)
    {
        _filterRefreshTimer.Stop();
        SetFollowingLatest(true);
        RebuildVisibleMessages();
        ScrollToLatestRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildVisibleMessages()
    {
        var visible = Messages.Where(MatchesMessageFilters).ToArray();
        VisibleMessages.Clear();
        VisibleMessages.AppendBatch(visible, MessageLimit);
        TwitchVisibleMessages.Clear();
        TwitchVisibleMessages.AppendBatch(
            visible.Where(item => !item.Message.IsYouTubeMessage).ToArray(),
            MessageLimit);
        YouTubeVisibleMessages.Clear();
        YouTubeVisibleMessages.AppendBatch(
            visible.Where(item => item.Message.IsYouTubeMessage).ToArray(),
            MessageLimit);
        NotifyVisibleMessageStateChanged();
        MessagesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshMessagePresentation()
    {
        foreach (var item in Messages.Concat(_deferredMessages)
                     .Concat(RecentUserMessages.Select(recent => recent.Value)).Distinct())
        {
            _thirdPartyCatalogs.TryGetValue(item.Message.Channel, out var catalog);
            item.ApplyPresentationSettings(
                EnableTwitchEmotes,
                EnableBttvEmotes,
                EnableSevenTvEmotes,
                catalog,
                useLightTwitchTheme: string.Equals(Theme, "Light", StringComparison.Ordinal));
        }
    }

    private bool AppendMessageBatch(
        IReadOnlyList<ChatMessageItemViewModel> batch,
        int maximumCount,
        bool forceVisibleTrim = false)
    {
        if (batch.Count == 0)
        {
            return false;
        }

        var storageMaximum = Math.Max(maximumCount, maximumCount * 3);
        Messages.AppendBatch(batch, storageMaximum);
        var visibleIncoming = batch.Where(MatchesMessageFilters).ToArray();
        VisibleMessages.AppendBatch(visibleIncoming, int.MaxValue);
        var twitchIncoming = visibleIncoming.Where(item => !item.Message.IsYouTubeMessage).ToArray();
        var youTubeIncoming = visibleIncoming.Where(item => item.Message.IsYouTubeMessage).ToArray();
        TwitchVisibleMessages.AppendBatch(twitchIncoming, int.MaxValue);
        YouTubeVisibleMessages.AppendBatch(youTubeIncoming, int.MaxValue);
        var trimmed = 0;
        if (forceVisibleTrim ||
            VisibleMessages.Count >= LiveChatBufferPolicy.GetTrimTrigger(maximumCount))
        {
            trimmed = VisibleMessages.RemoveOldestRange(
                Math.Max(0, VisibleMessages.Count - maximumCount));
            trimmed += TwitchVisibleMessages.RemoveOldestRange(
                Math.Max(0, TwitchVisibleMessages.Count - maximumCount));
            trimmed += YouTubeVisibleMessages.RemoveOldestRange(
                Math.Max(0, YouTubeVisibleMessages.Count - maximumCount));
        }
        NotifyVisibleMessageStateChanged();
        return trimmed > 0 || visibleIncoming.Length > 0;
    }

    private void NotifyVisibleMessageStateChanged()
    {
        OnPropertyChanged(nameof(ShowChatEmptyState));
        OnPropertyChanged(nameof(HasTwitchVisibleMessages));
        OnPropertyChanged(nameof(HasYouTubeVisibleMessages));
    }

    private bool MatchesMessageFilters(ChatMessageItemViewModel item)
    {
        var isVisibleYouTubeMessage = item.Message.IsYouTubeMessage &&
                                      string.Equals(
                                          item.Message.Channel,
                                          GetYouTubeChannelKey(),
                                          StringComparison.OrdinalIgnoreCase);
        if (!isVisibleYouTubeMessage &&
            !string.Equals(item.Message.Channel, Channel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!ShowChannelPointRedemptions && item.Message.IsChannelPointRedemption)
        {
            return false;
        }

        if (_normalizedMessageSearch.Length > 0 &&
            !item.Text.Contains(_normalizedMessageSearch, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!MatchesSmartChatMode(item.Message))
        {
            return false;
        }

        return _normalizedUserFilter.Length == 0 ||
               item.UserLabel.Contains(_normalizedUserFilter, StringComparison.OrdinalIgnoreCase) ||
               item.Message.UserLogin.Contains(_normalizedUserFilter, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesSmartChatMode(ChatMessage message) => SelectedChatViewMode switch
    {
        ChatViewMode.All => true,
        ChatViewMode.Questions => message.Text.Contains('?'),
        ChatViewMode.Mentions => ContainsMention(message.Text, AccountLogin) ||
                                 ContainsMention(message.Text, Channel) ||
                                 ContainsMention(message.Text, YouTubeAccountName),
        ChatViewMode.Paid => message.IsPaidEvent,
        ChatViewMode.FirstMessages => message.IsFirstMessage,
        ChatViewMode.Suspicious => message.IsSuspicious,
        ChatViewMode.Roles => message.Badges.Any(IsRoleBadge),
        _ => true
    };

    private ChatMessage EnrichIncomingMessage(ChatMessage message)
    {
        if (message.IsSystemEvent || string.IsNullOrWhiteSpace(message.UserLogin))
        {
            return message;
        }

        var userKey = string.Join('|', message.Platform, NormalizeChannel(message.Channel),
            string.IsNullOrWhiteSpace(message.UserId) ? message.UserLogin : message.UserId);
        var isFirst = _seenChatters.TryAdd(userKey, 0);
        var isSuspicious = IsLocallySuspicious(userKey, message.Text, message.Timestamp);
        return message with { IsFirstMessage = isFirst, IsSuspicious = isSuspicious };
    }

    private bool IsLocallySuspicious(string userKey, string text, DateTimeOffset timestamp)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .ToUpperInvariant();
        var samples = _suspiciousMessageSamples.GetOrAdd(userKey, static _ => new());
        var cutoff = timestamp - TimeSpan.FromSeconds(30);
        while (samples.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
        {
            samples.TryDequeue(out _);
        }

        var repeated = normalized.Length >= 4 &&
                       samples.Count(sample => string.Equals(sample.Text, normalized, StringComparison.Ordinal)) >= 2;
        samples.Enqueue(new SuspiciousMessageSample(normalized, timestamp));
        while (samples.Count > 20)
        {
            samples.TryDequeue(out _);
        }

        var letters = text.Count(char.IsLetter);
        var upper = text.Count(char.IsUpper);
        var excessiveCaps = letters >= 12 && upper >= Math.Ceiling(letters * 0.75);
        var excessiveLinks = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Count(token => token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            token.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                            token.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) >= 3;
        return repeated || excessiveCaps || excessiveLinks;
    }

    private static bool ContainsMention(string text, string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        text.Contains("@" + value.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase);

    private static bool IsRoleBadge(ChatBadge badge) =>
        badge.SetId.Contains("broadcaster", StringComparison.OrdinalIgnoreCase) ||
        badge.SetId.Contains("owner", StringComparison.OrdinalIgnoreCase) ||
        badge.SetId.Contains("moderator", StringComparison.OrdinalIgnoreCase) ||
        badge.SetId.Contains("vip", StringComparison.OrdinalIgnoreCase) ||
        badge.SetId.Contains("subscriber", StringComparison.OrdinalIgnoreCase) ||
        badge.SetId.Contains("sponsor", StringComparison.OrdinalIgnoreCase) ||
        badge.SetId.Contains("member", StringComparison.OrdinalIgnoreCase);

    private void OnMessageReceived(object? sender, ChatMessageEventArgs eventArgs)
    {
        if (ReferenceEquals(sender, _chatClient) &&
            (_eventSubClient.HasEventSubChat(eventArgs.Message.Channel) ||
             eventArgs.Message.IsChannelPointRedemption &&
             _eventSubClient.HasDetailedChannelPoints(eventArgs.Message.Channel)))
        {
            return;
        }
        var message = EnrichIncomingMessage(eventArgs.Message);
        var messageChannel = NormalizeChannel(message.Channel);
        if (!message.IsYouTubeMessage &&
            _channelChatClearedAt.TryGetValue(messageChannel, out var clearedAt) &&
            message.Timestamp <= clearedAt)
        {
            return;
        }
        var presentationMessage = CreateOverlayMessage(message);
        _chatLogWriter.Enqueue(presentationMessage);
        if (!IsObsChatSuppressed)
        {
            _obsOverlayServer.Publish(presentationMessage);
        }
        if (IsChatDisplayPaused)
        {
            Dispatcher.UIThread.Post(() => ProtectionSuppressedCount++);
            return;
        }
        _pendingMessages.Enqueue(message);
        if (Interlocked.Increment(ref _pendingMessageCount) > MaximumPendingMessages &&
            _pendingMessages.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingMessageCount);
        }
    }

    private ChatMessage CreateOverlayMessage(ChatMessage message)
    {
        IReadOnlyList<ChatMessagePart> parts = message.Parts.Count == 0
            ? [ChatMessagePart.PlainText(message.Text)]
            : message.Parts;
        if (!EnableTwitchEmotes)
        {
            parts = parts
                .Select(part => part.Kind == ChatMessagePartKind.Emote &&
                                string.Equals(part.Provider, "Twitch", StringComparison.OrdinalIgnoreCase)
                    ? ChatMessagePart.PlainText(part.Text)
                    : part)
                .ToArray();
        }

        _thirdPartyCatalogs.TryGetValue(message.Channel, out var thirdPartyCatalog);
        IReadOnlyDictionary<string, ThirdPartyEmote>? filteredCatalog = thirdPartyCatalog;
        if (thirdPartyCatalog is not null && (!EnableBttvEmotes || !EnableSevenTvEmotes))
        {
            filteredCatalog = thirdPartyCatalog
                .Where(pair =>
                    (EnableBttvEmotes ||
                     !ThirdPartyEmoteProviders.IsBttv(pair.Value.Provider)) &&
                    (EnableSevenTvEmotes ||
                     !ThirdPartyEmoteProviders.IsSevenTv(pair.Value.Provider)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        parts = ThirdPartyEmoteTokenizer.Tokenize(parts, filteredCatalog);

        _badgeCatalogs.TryGetValue(message.Channel, out var badgeCatalog);
        var badges = message.Badges
            .Select(badge => badgeCatalog is not null &&
                             badgeCatalog.TryGetValue(badge.Key, out var definition)
                ? badge with { ImageUri = definition.ImageUri, Title = definition.Title }
                : badge)
            .ToArray();
        return message with { Parts = parts, Badges = badges };
    }

    private async Task RestartEventSubSafeAsync()
    {
        try
        {
            await _eventSubClient.ConfigureAsync(
                _authSession,
                SavedChannels.Select(item => item.Login).ToArray(),
                _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidDataException or InvalidOperationException or
                ArgumentException or JsonException)
        {
        }
    }

    private void OnAutoModMessageHeld(object? sender, AutoModHeldEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (PendingAutoModMessages.All(item => item.Value.MessageId != eventArgs.Message.MessageId))
            {
                PendingAutoModMessages.AppendBatch([new HeldAutoModMessageViewModel(eventArgs.Message)], 1000);
            }
        });

    private void OnAutoModMessageResolved(object? sender, AutoModResolvedEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in PendingAutoModMessages.Where(item =>
                         item.Value.MessageId == eventArgs.MessageId).ToArray())
            {
                _ = PendingAutoModMessages.Remove(item);
            }
        });

    private void OnEventSubUserBanned(object? sender, EventSubBanEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            var value = eventArgs.Value;
            if (!string.Equals(value.BroadcasterLogin, Channel, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            foreach (var existing in BannedUsers.Where(item => item.Value.UserId == value.UserId).ToArray())
            {
                _ = BannedUsers.Remove(existing);
            }
            BannedUsers.AppendBatch([new BannedUserViewModel(new BannedUser(
                value.UserId, value.UserLogin, value.DisplayName, value.StartedAt,
                value.IsPermanent ? null : value.EndsAt, value.Reason))], 1000);
            MarkUserMessages(
                value.UserId,
                value.UserLogin,
                value.IsPermanent
                    ? ChatMessageModerationState.Banned
                    : ChatMessageModerationState.TimedOut,
                value.BroadcasterLogin);
            _moderationBroadcasterId = value.BroadcasterId;
            ScheduleModerationCacheSave();
        });

    private void OnEventSubUserUnbanned(object? sender, EventSubUnbanEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            var value = eventArgs.Value;
            if (!string.Equals(value.BroadcasterLogin, Channel, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            foreach (var existing in BannedUsers.Where(item => item.Value.UserId == value.UserId).ToArray())
            {
                _ = BannedUsers.Remove(existing);
            }
            _moderationBroadcasterId = value.BroadcasterId;
            ScheduleModerationCacheSave();
        });

    private void OnEventSubUnbanRequestChanged(object? sender, EventSubUnbanRequestEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            var value = eventArgs.Value;
            if (!string.Equals(value.BroadcasterLogin, Channel, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            foreach (var existing in UnbanRequests.Where(item => item.Value.RequestId == value.RequestId).ToArray())
            {
                _ = UnbanRequests.Remove(existing);
            }
            if (string.IsNullOrWhiteSpace(value.Status) ||
                string.Equals(value.Status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                UnbanRequests.AppendBatch([new UnbanRequestViewModel(new UnbanRequest(
                    value.RequestId, value.BroadcasterId, value.UserId, value.UserLogin, value.DisplayName,
                    value.Text, value.CreatedAt, UnbanRequestStatus.Pending, null, string.Empty), Texts)], 1000);
            }
            _moderationBroadcasterId = value.BroadcasterId;
            ScheduleModerationCacheSave();
        });

    private void ScheduleModerationCacheSave()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_moderationBroadcasterId))
        {
            return;
        }

        _moderationCacheStore.ScheduleSave(
            _moderationBroadcasterId,
            BannedUsers.Select(item => item.Value),
            UnbanRequests.Select(item => item.Value));
    }

    private void OnSharedChatStateChanged(object? sender, SharedChatStateEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            var value = eventArgs.Value;
            if (string.IsNullOrWhiteSpace(value.BroadcasterLogin))
            {
                return;
            }
            if (value.IsActive)
            {
                _sharedChatStates[value.BroadcasterLogin] = value;
            }
            else
            {
                _sharedChatStates.Remove(value.BroadcasterLogin);
            }
            OnPropertyChanged(nameof(SharedChatStatusLabel));
            OnPropertyChanged(nameof(IsSharedChatActive));
        });

    private void OnEventSubChatMessageDeleted(object? sender, EventSubMessageDeletedEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            var value = eventArgs.Value;
            foreach (var item in Messages.Where(item =>
                         item.Message.Id == value.MessageId &&
                         (string.IsNullOrWhiteSpace(value.BroadcasterLogin) ||
                          string.Equals(item.Message.Channel, value.BroadcasterLogin, StringComparison.OrdinalIgnoreCase)))
                     .ToArray())
            {
                item.MarkModerated(ChatMessageModerationState.Deleted);
            }
            MessagesChanged?.Invoke(this, EventArgs.Empty);
        });

    private void OnEventSubUserMessagesCleared(object? sender, EventSubUserMessagesClearedEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            var value = eventArgs.Value;
            foreach (var item in Messages.Where(item =>
                         item.Message.UserId == value.UserId &&
                         (string.IsNullOrWhiteSpace(value.BroadcasterLogin) ||
                          string.Equals(item.Message.Channel, value.BroadcasterLogin, StringComparison.OrdinalIgnoreCase)))
                     .ToArray())
            {
                item.MarkModerated(ChatMessageModerationState.TimedOut);
            }
            MessagesChanged?.Invoke(this, EventArgs.Empty);
        });

    private void OnEventSubChatCleared(object? sender, EventSubChatClearedEventArgs eventArgs)
    {
        var value = eventArgs.Value;
        var channel = NormalizeChannel(value.BroadcasterLogin);
        if (!TryRegisterChatClear(channel, value.ClearedAt))
        {
            return;
        }

        // Keep the reset in EventSub delivery order. Posting this operation to
        // the UI thread would let a post-clear message reach OBS first and then
        // be removed by a late reset.
        _obsOverlayServer.ClearChannel(channel);
        Dispatcher.UIThread.Post(() => ApplyEventSubChatClearCore(value, channel));
    }

    internal void ApplyEventSubChatClear(EventSubChatCleared value)
    {
        var channel = NormalizeChannel(value.BroadcasterLogin);
        if (!TryRegisterChatClear(channel, value.ClearedAt))
        {
            return;
        }

        _obsOverlayServer.ClearChannel(channel);
        ApplyEventSubChatClearCore(value, channel);
    }

    private bool TryRegisterChatClear(string channel, DateTimeOffset clearedAt)
    {
        if (channel.Length == 0)
        {
            return false;
        }

        while (true)
        {
            if (_channelChatClearedAt.TryGetValue(channel, out var previousClear))
            {
                if (previousClear >= clearedAt)
                {
                    return false;
                }
                if (_channelChatClearedAt.TryUpdate(channel, clearedAt, previousClear))
                {
                    return true;
                }
                continue;
            }
            if (_channelChatClearedAt.TryAdd(channel, clearedAt))
            {
                return true;
            }
        }
    }

    private void ApplyEventSubChatClearCore(EventSubChatCleared value, string channel)
    {

        RemoveChannelMessages(Messages, channel, value.ClearedAt);
        RemoveChannelMessages(VisibleMessages, channel, value.ClearedAt);
        RemoveChannelMessages(TwitchVisibleMessages, channel, value.ClearedAt);
        RemoveChannelMessages(YouTubeVisibleMessages, channel, value.ClearedAt);
        _deferredMessages.RemoveAll(item => IsClearedChannelMessage(item.Message, channel, value.ClearedAt));

        if (string.Equals(Channel, channel, StringComparison.OrdinalIgnoreCase))
        {
            PinnedMessageAuthor = string.Empty;
            PinnedMessageText = string.Empty;
            _deferredPinnedMessageAuthor = string.Empty;
            _deferredPinnedMessageText = string.Empty;
            UnreadCount = 0;
        }

        NotifyVisibleMessageStateChanged();
        MessagesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void RemoveChannelMessages(
        ICollection<ChatMessageItemViewModel> messages,
        string channel,
        DateTimeOffset clearedAt)
    {
        foreach (var item in messages
                     .Where(item => IsClearedChannelMessage(item.Message, channel, clearedAt))
                     .ToArray())
        {
            _ = messages.Remove(item);
        }
    }

    private static bool IsClearedChannelMessage(ChatMessage message, string channel, DateTimeOffset clearedAt) =>
        !message.IsYouTubeMessage &&
        string.Equals(message.Channel, channel, StringComparison.OrdinalIgnoreCase) &&
        message.Timestamp <= clearedAt;

    private bool WasMessageCleared(ChatMessage message) =>
        !message.IsYouTubeMessage &&
        _channelChatClearedAt.TryGetValue(NormalizeChannel(message.Channel), out var clearedAt) &&
        message.Timestamp <= clearedAt;

    private void OnStatusChanged(object? sender, ChatConnectionStatusEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            ConnectionState = eventArgs.State;
            StatusDetail = eventArgs.State switch
            {
                ChatConnectionState.Connected => Texts.ConnectedTo(eventArgs.Channel),
                ChatConnectionState.Reconnecting => Texts.ConnectionLost,
                ChatConnectionState.Connecting => Texts.ConnectingTo(eventArgs.Channel),
                ChatConnectionState.Error => Texts.CouldNotConnect,
                _ => Texts.ChatDisconnected
            };

            if (eventArgs.State == ChatConnectionState.Connected && _authSession is { } session)
            {
                _ = RefreshBadgeCatalogSafeAsync(session, eventArgs.Channel);
                _ = RefreshStreamStatusSafeAsync(session, eventArgs.Channel);
            }
        });
    }

    private void OnChatLogWriterStatusChanged(
        object? sender,
        ChatLogWriterStatusChangedEventArgs eventArgs)
    {
        if (eventArgs.Error is { } error)
        {
            AppDiagnostics.Write("ChatLogWriter", error);
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            StatusDetail = eventArgs.State switch
            {
                ChatLogWriterState.QueueOverflow =>
                    Texts.ChatLogQueueOverflow(eventArgs.DroppedMessageCount),
                ChatLogWriterState.WriteFailed => Texts.ChatLogWriteFailed,
                ChatLogWriterState.Recovered => Texts.ChatLogWriteRecovered,
                _ => StatusDetail
            };
        });
    }

    private void OnMessageBatchTimerTick(object? sender, EventArgs eventArgs)
        => ProcessPendingMessageBatch();

    internal void ProcessPendingMessageBatch()
    {
        if (_disposed || _isMessageBatchProcessingSuspended || _pendingMessages.IsEmpty)
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var batch = new List<ChatMessageItemViewModel>(
            Math.Min(MaximumBatchSize, Math.Max(1, Volatile.Read(ref _pendingMessageCount))));
        var processedCount = 0;
        while (processedCount < MaximumBatchSize &&
               (processedCount == 0 || Stopwatch.GetElapsedTime(startedAt) < MessageBatchTimeBudget) &&
               _pendingMessages.TryDequeue(out var message))
        {
            processedCount++;
            Interlocked.Decrement(ref _pendingMessageCount);
            if (WasMessageCleared(message))
            {
                continue;
            }
            _badgeCatalogs.TryGetValue(message.Channel, out var badgeCatalog);
            _thirdPartyCatalogs.TryGetValue(message.Channel, out var thirdPartyCatalog);
            var item = new ChatMessageItemViewModel(
                message,
                _imageCache,
                Texts,
                badgeCatalog,
                thirdPartyCatalog,
                this);
            batch.Add(item);
            if (!message.IsYouTubeMessage &&
                NeedsThirdPartyCatalogRefresh(message.Channel, message.BroadcasterId) &&
                _thirdPartyRefreshRequested.Add(message.Channel))
            {
                _ = RefreshThirdPartyCatalogSafeAsync(message.Channel, message.BroadcasterId);
            }

            if (message.IsPinned)
            {
                if (IsFollowingLatest)
                {
                    PinnedMessageAuthor = message.UserLabel;
                    PinnedMessageText = message.Text;
                }
                else
                {
                    _deferredPinnedMessageAuthor = message.UserLabel;
                    _deferredPinnedMessageText = message.Text;
                }
            }
        }

        if (!IsFollowingLatest)
        {
            _deferredMessages.AddRange(batch);
            TrimDeferredMessages();
            UnreadCount = Math.Min(99_999, UnreadCount + batch.Count);
            return;
        }

        if (AppendMessageBatch(batch, MessageLimit))
        {
            MessagesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void FlushDeferredMessages()
    {
        if (_deferredPinnedMessageText.Length > 0)
        {
            PinnedMessageAuthor = _deferredPinnedMessageAuthor;
            PinnedMessageText = _deferredPinnedMessageText;
            _deferredPinnedMessageAuthor = string.Empty;
            _deferredPinnedMessageText = string.Empty;
        }

        if (_deferredMessages.Count == 0)
        {
            return;
        }

        var batch = _deferredMessages.ToArray();
        _deferredMessages.Clear();
        if (AppendMessageBatch(batch, MessageLimit, forceVisibleTrim: true))
        {
            MessagesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void TrimDeferredMessages()
    {
        var removalCount = Math.Max(0, _deferredMessages.Count - MessageLimit);
        if (removalCount > 0)
        {
            _deferredMessages.RemoveRange(0, removalCount);
        }
    }

    private async void OnSessionValidationTimerTick(object? sender, EventArgs eventArgs)
    {
        if (_disposed || _sessionValidationInProgress)
        {
            return;
        }

        var session = _authSession ?? _authSessionStore.Load();
        if (session is null)
        {
            _sessionValidationTimer.Interval = SessionValidationInterval;
            return;
        }

        var authenticationGeneration = Volatile.Read(ref _authenticationGeneration);
        _sessionValidationInProgress = true;
        try
        {
            var validatedSession = _authSession is null
                ? await _authService.ValidateAsync(session, _lifetimeCancellation.Token)
                : await EnsureValidSessionAsync(session, _lifetimeCancellation.Token);
            if (authenticationGeneration != Volatile.Read(ref _authenticationGeneration) || _disposed)
                throw new AuthenticationContextChangedException(_lifetimeCancellation.Token);
            ApplyAuthenticatedSession(validatedSession);
            authenticationGeneration = Volatile.Read(ref _authenticationGeneration);
            _authSessionStore.Save(validatedSession);
            _sessionValidationTimer.Interval = SessionValidationInterval;
            AuthStatus = Texts.SessionValidated;
            await RefreshAccountProfileSafeAsync(validatedSession);
            await RefreshBadgeCatalogSafeAsync(validatedSession, Channel);
            await RefreshStreamStatusSafeAsync(validatedSession, Channel);
            await RefreshModerationAccessSafeAsync();
            await RestartEventSubSafeAsync();
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (OperationCanceledException exception)
        {
            if (_disposed || authenticationGeneration != Volatile.Read(ref _authenticationGeneration)) return;
            _sessionValidationTimer.Interval = SessionValidationRetryInterval;
            AuthStatus = Texts.SessionValidationDeferred(AppDiagnostics.GetUserMessage(exception));
        }
        catch (Exception exception) when (IsTerminalSessionFailure(exception))
        {
            if (_disposed || authenticationGeneration != Volatile.Read(ref _authenticationGeneration)) return;
            EndInvalidSession(AppDiagnostics.GetUserMessage(exception));
            _sessionValidationTimer.Interval = SessionValidationInterval;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_disposed || authenticationGeneration != Volatile.Read(ref _authenticationGeneration)) return;
            _sessionValidationTimer.Interval = SessionValidationRetryInterval;
            AuthStatus = Texts.SessionValidationDeferred(AppDiagnostics.GetUserMessage(exception));
        }
        finally
        {
            _sessionValidationInProgress = false;
        }
    }

    internal static bool IsTerminalSessionFailure(Exception exception) =>
        exception is InvalidOperationException ||
        exception is HttpRequestException
        {
            StatusCode: System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized
        };

    private void EndInvalidSession(string details)
    {
        Interlocked.Increment(ref _authenticationGeneration);
        InvalidateModerationContext();
        _authSession = null;
        _authSessionStore.Clear();
        _ = _eventSubClient.ConfigureAsync(null, [], CancellationToken.None);
        CanModerate = false;
        AccountLogin = string.Empty;
        AccountDisplayName = string.Empty;
        AccountProfileImageResource = null;
        _allFollowedChannels = [];
        FollowedChannels.Clear();
        FollowedChannelsStatus = string.Empty;
        IsTwitchClipNotificationVisible = false;
        CreatedTwitchClipEditUri = null;
        CreatedTwitchClipShareUri = null;
        AuthStatus = Texts.SessionEnded(details);
        OnPropertyChanged(nameof(IsAccountConnected));
        NotifyModerationContextChanged();
        OnPropertyChanged(nameof(CanAuthorize));
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(WelcomeActionText));
        OnPropertyChanged(nameof(ShowMessageComposer));
        OnPropertyChanged(nameof(ShowReadOnlyComposerNotice));
        OnPropertyChanged(nameof(CanHeaderDisconnect));
        OnPropertyChanged(nameof(HeaderDisconnectTip));
        OnPropertyChanged(nameof(HasTwitchClipPermission));
        OnPropertyChanged(nameof(CanCreateTwitchClip));
        OnPropertyChanged(nameof(TwitchClipButtonTip));
        StartAuthorizationCommand.NotifyCanExecuteChanged();
        SendMessageCommand.NotifyCanExecuteChanged();
        CreateTwitchClipCommand.NotifyCanExecuteChanged();
        NotifyFollowedChannelAccessChanged();
    }

    private async void OnStreamStatusTimerTick(object? sender, EventArgs eventArgs)
    {
        var session = _authSession;
        if (session is null || _disposed || string.IsNullOrWhiteSpace(Channel))
        {
            return;
        }

        if (Interlocked.Exchange(ref _savedChannelStatusRefreshInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            await RefreshStreamStatusSafeAsync(session, Channel);
            await RefreshSavedChannelMetadataSafeAsync();
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception)
        {
            _ = exception;
        }
        finally
        {
            Interlocked.Exchange(ref _savedChannelStatusRefreshInProgress, 0);
        }
    }

    private async void OnPinnedMessageTimerTick(object? sender, EventArgs eventArgs)
    {
        try
        {
            await RefreshPinnedMessageSafeAsync();
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
            // Expected while the application is shutting down.
        }
        catch (Exception exception)
        {
            // DispatcherTimer callbacks are async void boundaries. Keep an unexpected
            // malformed service response from terminating the UI process.
            _ = exception;
        }
    }

    private async Task RefreshPinnedMessageSafeAsync()
    {
        var session = _authSession;
        var channel = NormalizeChannel(Channel);
        if (session is null || channel.Length == 0 || !CanModerate || _disposed)
        {
            ClearPinnedMessage();
            return;
        }
        try
        {
            await _pinnedRefreshLock.WaitAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
            return;
        }
        try
        {
            session = await EnsureValidSessionAsync(session, _lifetimeCancellation.Token);
            var pinned = await _chatApiClient.GetPinnedChatMessageAsync(
                session, channel, _lifetimeCancellation.Token);
            if (!_disposed && string.Equals(Channel, channel, StringComparison.OrdinalIgnoreCase))
            {
                ApplyPinnedMessage(pinned);
            }
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (HttpRequestException exception) when (exception.StatusCode is
            System.Net.HttpStatusCode.Unauthorized or
            System.Net.HttpStatusCode.Forbidden or
            System.Net.HttpStatusCode.NotFound)
        {
            ClearPinnedMessage();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidDataException or InvalidOperationException or
                ArgumentException or JsonException)
        {
        }
        finally
        {
            _pinnedRefreshLock.Release();
        }
    }

    private void ApplyPinnedMessage(PinnedChatMessage? pinned)
    {
        if (pinned?.EndsAt is { } endsAt && endsAt <= DateTimeOffset.UtcNow)
        {
            pinned = null;
        }
        PinnedMessageAuthor = pinned is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(pinned.SenderDisplayName) ? pinned.SenderUserLogin : pinned.SenderDisplayName;
        PinnedMessageText = pinned?.Text ?? string.Empty;
        foreach (var item in Messages)
        {
            item.IsPinnedOverride = pinned is not null && item.Message.Id == pinned.MessageId;
        }
        MessagesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearPinnedMessage() => ApplyPinnedMessage(null);

    private async Task<TwitchAuthSession> EnsureValidSessionAsync(
        TwitchAuthSession session,
        CancellationToken cancellationToken)
    {
        var authenticationGeneration = Volatile.Read(ref _authenticationGeneration);
        if (!IsAuthenticationContextCurrent(authenticationGeneration, session))
            throw new AuthenticationContextChangedException(cancellationToken);
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsAuthenticationContextCurrent(authenticationGeneration, session))
                throw new AuthenticationContextChangedException(cancellationToken);
            session = _authSession!;
            try
            {
                if (session.NeedsRefresh(TimeSpan.FromMinutes(2)) && session.RefreshToken.Length > 0)
                {
                    session = await _authService.RefreshAsync(session, cancellationToken);
                }
                else if (session.NeedsRefresh(TimeSpan.FromMinutes(2)) ||
                         DateTimeOffset.UtcNow - session.ValidatedAtUtc >= TimeSpan.FromHours(1))
                {
                    session = await _authService.ValidateAsync(session, cancellationToken);
                }
            }
            catch (Exception) when (!IsAuthenticationContextCurrent(authenticationGeneration, session))
            {
                // A late failure belongs to the discarded account, not the new
                // session. Surface it as cancellation so callers do not log out B.
                throw new AuthenticationContextChangedException(cancellationToken);
            }

            if (!IsAuthenticationContextCurrent(authenticationGeneration, session))
                throw new AuthenticationContextChangedException(cancellationToken);

            _authSession = session;
            _authSessionStore.Save(session);
            UpdateAuthenticatedStorageStatus();
            AccountLogin = session.Login;
            return session;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private bool IsAuthenticationContextCurrent(long generation, TwitchAuthSession session) =>
        !_disposed &&
        generation == Volatile.Read(ref _authenticationGeneration) &&
        _authSession is not null &&
        string.Equals(session.UserId, _authSession.UserId, StringComparison.Ordinal);

    private async Task RefreshModerationAccessSafeAsync()
    {
        var session = _authSession;
        var channel = NormalizeChannel(Channel);
        var generation = Volatile.Read(ref _moderationContextGeneration);
        if (session is null || string.IsNullOrWhiteSpace(Channel) || _disposed)
        {
            CanModerate = false;
            return;
        }

        try
        {
            var access = await _chatApiClient.GetModerationAccessAsync(
                session,
                channel,
                _lifetimeCancellation.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (IsModerationContextCurrent(generation, channel, session))
                    CanModerate = access.CanModerate;
            });
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsModerationContextCurrent(generation, channel, session))
                    CanModerate = false;
            });
        }
    }

    private bool CanModerateMessage(ChatMessageItemViewModel? item) =>
        item is not null &&
        !item.Message.IsSystemEvent &&
        (item.Message.IsYouTubeMessage
            ? CanModerateYouTube &&
              item.Message.UserId.Length > 0 &&
              !string.Equals(item.Message.UserId, _youTubeAuthSession?.ChannelId, StringComparison.Ordinal)
            : CanModerate &&
              item.Message.UserLogin.Length > 0 &&
              !string.Equals(item.Message.UserLogin, AccountLogin, StringComparison.OrdinalIgnoreCase));

    public bool CanModerateTarget(ChatMessageItemViewModel? item) => CanModerateMessage(item);

    public bool IsUserPunished(ChatMessageItemViewModel? item) =>
        item is not null &&
        (item.Message.IsYouTubeMessage
            ? YouTubeBans.Any(user =>
                string.Equals(user.Value.UserChannelId, item.Message.UserId, StringComparison.Ordinal))
            : BannedUsers.Any(user =>
                (!string.IsNullOrWhiteSpace(item.Message.UserId) &&
                 string.Equals(user.Value.UserId, item.Message.UserId, StringComparison.Ordinal)) ||
                string.Equals(user.Value.UserLogin, item.Message.UserLogin, StringComparison.OrdinalIgnoreCase)));

    private async Task<bool> RunYouTubeModerationActionAsync(
        ChatMessageItemViewModel item,
        Func<CancellationToken, Task> action,
        ChatMessageModerationState? moderationState,
        bool markAllUserMessages = false)
    {
        if (_youTubeLiveChatClient is null || !CanModerateMessage(item))
        {
            return false;
        }
        try
        {
            await action(_lifetimeCancellation.Token).ConfigureAwait(true);
            if (moderationState is { } state && markAllUserMessages)
            {
                MarkUserMessages(item.Message.UserId, item.Message.UserLogin, state, item.Message.Channel);
            }
            else if (moderationState is { } targetState)
            {
                item.MarkModerated(targetState);
                MessagesChanged?.Invoke(this, EventArgs.Empty);
            }
            StatusDetail = Texts.ModerationActionComplete;
            ModerationPanelStatus = Texts.ModerationActionComplete;
            return true;
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusDetail = Texts.ModerationActionFailed + AppDiagnostics.GetUserMessage(exception);
            ModerationPanelStatus = StatusDetail;
            return false;
        }
    }

    private async Task<bool> RunYouTubeBanActionAsync(
        ChatMessageItemViewModel item,
        int? durationSeconds,
        ChatMessageModerationState moderationState)
    {
        if (_youTubeLiveChatClient is null || !CanModerateMessage(item))
        {
            return false;
        }
        try
        {
            var ban = await _youTubeLiveChatClient.BanUserAsync(
                item.Message, durationSeconds, _lifetimeCancellation.Token).ConfigureAwait(true);
            foreach (var existing in YouTubeBans.Where(value =>
                         string.Equals(value.Value.UserChannelId, ban.UserChannelId, StringComparison.Ordinal)).ToArray())
            {
                _ = YouTubeBans.Remove(existing);
            }
            YouTubeBans.AppendBatch([new YouTubeChatBanViewModel(ban, Texts)], 1000);
            OnPropertyChanged(nameof(HasYouTubeBans));
            MarkUserMessages(item.Message.UserId, item.Message.UserLogin, moderationState, item.Message.Channel);
            StatusDetail = Texts.ModerationActionComplete;
            ModerationPanelStatus = Texts.ModerationActionComplete;
            return true;
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusDetail = Texts.ModerationActionFailed + AppDiagnostics.GetUserMessage(exception);
            ModerationPanelStatus = StatusDetail;
            return false;
        }
    }

    private async Task<bool> RunModerationActionAsync(
        ChatMessageItemViewModel item,
        Func<TwitchAuthSession, CancellationToken, Task> action,
        ChatMessageModerationState? moderationState,
        bool markAllUserMessages = false)
    {
        var session = _authSession;
        if (session is null || !CanModerateMessage(item))
        {
            return false;
        }

        try
        {
            session = await EnsureValidSessionAsync(session, _lifetimeCancellation.Token);
            await action(session, _lifetimeCancellation.Token);
            if (moderationState is { } state && markAllUserMessages)
            {
                MarkUserMessages(
                    item.Message.UserId,
                    item.Message.UserLogin,
                    state,
                    item.Message.Channel);
            }
            else if (moderationState is { } targetState)
            {
                item.MarkModerated(targetState);
                MessagesChanged?.Invoke(this, EventArgs.Empty);
            }

            StatusDetail = Texts.ModerationActionComplete;
            ModerationPanelStatus = StatusDetail;
            return true;
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusDetail = Texts.ModerationActionFailed + AppDiagnostics.GetUserMessage(exception);
            ModerationPanelStatus = StatusDetail;
            return false;
        }
    }

    private void MarkUserMessages(
        string userId,
        string userLogin,
        ChatMessageModerationState state,
        string broadcasterLogin)
    {
        foreach (var message in Messages
                     .Where(item =>
                         (string.IsNullOrWhiteSpace(userId)
                             ? string.Equals(item.Message.UserLogin, userLogin, StringComparison.OrdinalIgnoreCase)
                             : string.Equals(item.Message.UserId, userId, StringComparison.Ordinal)) &&
                         (string.IsNullOrWhiteSpace(broadcasterLogin) ||
                          string.Equals(item.Message.Channel, broadcasterLogin, StringComparison.OrdinalIgnoreCase))))
        {
            message.MarkModerated(state);
        }

        MessagesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAuthenticatedSession(TwitchAuthSession session)
    {
        if (!string.Equals(_authSession?.UserId, session.UserId, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _authenticationGeneration);
            InvalidateModerationContext();
        }
        _authSession = session;
        AccountLogin = session.Login;
        PromotePrimaryAccountChannel(session.Login);
        UpdateAuthenticatedStorageStatus();
        OnPropertyChanged(nameof(IsAccountConnected));
        NotifyModerationContextChanged();
        OnPropertyChanged(nameof(ShowMessageComposer));
        OnPropertyChanged(nameof(ShowReadOnlyComposerNotice));
        OnPropertyChanged(nameof(CanAuthorize));
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(CanHeaderDisconnect));
        OnPropertyChanged(nameof(HeaderDisconnectTip));
        OnPropertyChanged(nameof(HasTwitchClipPermission));
        OnPropertyChanged(nameof(CanCreateTwitchClip));
        OnPropertyChanged(nameof(TwitchClipButtonTip));
        StartAuthorizationCommand.NotifyCanExecuteChanged();
        SendMessageCommand.NotifyCanExecuteChanged();
        CreateTwitchClipCommand.NotifyCanExecuteChanged();
        NotifyFollowedChannelAccessChanged();
    }

    private void NotifyFollowedChannelAccessChanged()
    {
        OnPropertyChanged(nameof(HasFollowedChannelsPermission));
        OnPropertyChanged(nameof(CanReauthorizeFollowedChannels));
        OnPropertyChanged(nameof(ShowFollowedChannelsPermissionRequest));
        OnPropertyChanged(nameof(ShowFollowedChannelsSignInRequest));
        OnPropertyChanged(nameof(ShowFollowedChannelsContent));
        OnPropertyChanged(nameof(HasFollowedChannels));
        ReauthorizeFollowedChannelsCommand.NotifyCanExecuteChanged();
    }

    private void UpdateAuthenticatedStorageStatus()
    {
        AuthStatus = _authSessionStore.IsPersistent
            ? Texts.AccountConnected
            : Texts.SecureStorageUnavailable;
    }

    private async Task RefreshAccountProfileSafeAsync(TwitchAuthSession session)
    {
        var authenticationGeneration = Volatile.Read(ref _authenticationGeneration);
        try
        {
            var profile = await _chatApiClient.GetUserProfileAsync(
                session,
                session.Login,
                _lifetimeCancellation.Token);
            if (_disposed || authenticationGeneration != Volatile.Read(ref _authenticationGeneration) ||
                !string.Equals(session.UserId, _authSession?.UserId, StringComparison.Ordinal)) return;
            AccountLogin = profile.Login;
            AccountDisplayName = profile.DisplayName;
            AccountProfileImageResource = profile.ProfileImageUri is null
                ? null
                : _imageCache.GetResource(profile.ProfileImageUri, 32);
            if (SavedChannels.FirstOrDefault(channel => channel.IsPrimaryAccountChannel) is { } primary)
            {
                primary.BroadcasterId = profile.Id;
                primary.DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName)
                    ? profile.Login
                    : profile.DisplayName;
                primary.ProfileImageResource = profile.ProfileImageUri is null
                    ? null
                    : _imageCache.GetResource(profile.ProfileImageUri, 40);
            }
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            if (_disposed || authenticationGeneration != Volatile.Read(ref _authenticationGeneration)) return;
            AccountDisplayName = session.Login;
            AccountProfileImageResource = null;
            if (SavedChannels.FirstOrDefault(channel => channel.IsPrimaryAccountChannel) is { } primary)
            {
                primary.DisplayName = session.Login;
                primary.ProfileImageResource = null;
            }
        }
    }

    private void PromotePrimaryAccountChannel(string login)
    {
        login = NormalizeChannel(login);
        if (login.Length == 0)
        {
            return;
        }

        foreach (var channel in SavedChannels)
        {
            channel.IsPrimaryAccountChannel = false;
        }

        var primary = SavedChannels.FirstOrDefault(channel =>
            string.Equals(channel.Login, login, StringComparison.OrdinalIgnoreCase));
        if (primary is null)
        {
            while (SavedChannels.Count >= 3)
            {
                var removed = SavedChannels[^1];
                _ = SavedChannels.Remove(removed);
                _ = PartChannelSafeAsync(removed.Login);
                if (ReferenceEquals(SelectedSavedChannel, removed))
                {
                    SelectedSavedChannel = null;
                }
                if (string.Equals(Channel, removed.Login, StringComparison.OrdinalIgnoreCase))
                {
                    Channel = string.Empty;
                }
            }

            primary = new ChannelSessionViewModel(login);
            SavedChannels.Insert(0, primary);
        }
        else
        {
            var index = SavedChannels.IndexOf(primary);
            if (index > 0)
            {
                SavedChannels.Move(index, 0);
            }
        }

        primary.IsPrimaryAccountChannel = true;
        primary.DisplayName = string.IsNullOrWhiteSpace(AccountDisplayName)
            ? login
            : AccountDisplayName;
        primary.ProfileImageResource = AccountProfileImageResource;
        SelectedSavedChannel ??= primary;
        OnPropertyChanged(nameof(CanAddSavedChannel));
        OnPropertyChanged(nameof(CanConfirmAddChannel));
        OnPropertyChanged(nameof(CanRemoveChannel));
        RemoveChannelCommand.NotifyCanExecuteChanged();
    }

    private void RemovePrimaryAccountChannel()
    {
        var primary = SavedChannels.FirstOrDefault(channel => channel.IsPrimaryAccountChannel);
        if (primary is null)
        {
            return;
        }

        _ = SavedChannels.Remove(primary);
        _ = PartChannelSafeAsync(primary.Login);
        if (ReferenceEquals(SelectedSavedChannel, primary) ||
            string.Equals(Channel, primary.Login, StringComparison.OrdinalIgnoreCase))
        {
            SelectedSavedChannel = SavedChannels.FirstOrDefault();
            Channel = SelectedSavedChannel?.Login ?? string.Empty;
            RebuildVisibleMessages();
        }
        OnPropertyChanged(nameof(CanAddSavedChannel));
        OnPropertyChanged(nameof(CanConfirmAddChannel));
        OnPropertyChanged(nameof(CanRemoveChannel));
        RemoveChannelCommand.NotifyCanExecuteChanged();
        QueueSettingsSave();
    }

    private async Task PartChannelSafeAsync(string login)
    {
        try
        {
            await _chatClient.PartChannelAsync(login, _lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
        }
    }

    private void ApplyChannelMetadata(ChannelSessionViewModel channel, ChannelSearchResult metadata)
    {
        channel.BroadcasterId = metadata.Id;
        channel.DisplayName = string.IsNullOrWhiteSpace(metadata.DisplayName)
            ? metadata.BroadcasterLogin
            : metadata.DisplayName;
        channel.IsLive = metadata.IsLive;
        channel.ViewerCount = metadata.ViewerCount;
        channel.GameName = metadata.GameName;
        channel.ProfileImageResource = Uri.TryCreate(metadata.ThumbnailUrl, UriKind.Absolute, out var uri)
            ? _imageCache.GetResource(uri, 40)
            : null;
        if (ReferenceEquals(SelectedSavedChannel, channel))
        {
            OnPropertyChanged(nameof(HeaderTitle));
            OnPropertyChanged(nameof(HeaderProfileImageResource));
            OnPropertyChanged(nameof(AvatarInitial));
        }
    }

    private async Task RefreshSavedChannelMetadataSafeAsync()
    {
        var channels = SavedChannels.ToArray();
        foreach (var channel in channels)
        {
            try
            {
                var metadata = await _chatApiClient.GetPublicChannelAsync(
                    channel.Login,
                    _lifetimeCancellation.Token).ConfigureAwait(true);
                if (metadata is not null && SavedChannels.Contains(channel))
                {
                    ApplyChannelMetadata(channel, metadata);
                }
            }
            catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
            {
                return;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException)
            {
                _ = exception;
            }
        }
    }

    private async Task RefreshStreamStatusSafeAsync(TwitchAuthSession session, string channel)
    {
        channel = NormalizeChannel(channel);
        if (channel.Length == 0 || _disposed)
        {
            return;
        }

        try
        {
            await _streamRefreshLock.WaitAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
            return;
        }

        try
        {
            var status = await _chatApiClient.GetStreamStatusAsync(
                session,
                channel,
                _lifetimeCancellation.Token);
            if (_disposed)
            {
                return;
            }

            var savedChannel = SavedChannels.FirstOrDefault(item =>
                string.Equals(item.Login, channel, StringComparison.OrdinalIgnoreCase));
            if (savedChannel is not null)
            {
                savedChannel.IsLive = status.IsLive;
                savedChannel.ViewerCount = status.ViewerCount;
            }

            if (!string.Equals(Channel, channel, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            IsStreamLive = status.IsLive;
            StreamViewerCount = status.ViewerCount;
            IsStreamStatusKnown = true;
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidDataException or InvalidOperationException or
                ArgumentException or JsonException)
        {
            IsStreamStatusKnown = false;
            IsStreamLive = false;
            StreamViewerCount = 0;
        }
        finally
        {
            _streamRefreshLock.Release();
        }
    }

    private async Task RefreshBadgeCatalogSafeAsync(TwitchAuthSession session, string channel)
    {
        channel = NormalizeChannel(channel);
        if (channel.Length == 0 || _disposed)
        {
            return;
        }

        var lockTaken = false;
        try
        {
            await _badgeRefreshLock.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            lockTaken = true;
            var catalog = await _chatApiClient.GetBadgeCatalogAsync(
                session,
                channel,
                _lifetimeCancellation.Token).ConfigureAwait(false);
            QueueImagePreload(catalog.Values.Select(item => item.ImageUri));
            await ApplyToChannelMessagesInBatchesAsync(
                channel,
                () => _badgeCatalogs[channel] = catalog,
                message => message.ApplyBadgeCatalog(catalog)).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or InvalidOperationException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    StatusDetail = Texts.BadgesUnavailable(AppDiagnostics.GetUserMessage(exception));
                }
            });
        }
        finally
        {
            if (lockTaken)
            {
                _badgeRefreshLock.Release();
            }
        }
    }

    private async Task RefreshThirdPartyCatalogSafeAsync(string channel, string broadcasterId)
    {
        channel = NormalizeChannel(channel);
        if (channel.Length == 0 || _disposed)
        {
            return;
        }

        var lockTaken = false;
        try
        {
            await _thirdPartyRefreshLock.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            lockTaken = true;
            var load = await _thirdPartyEmoteCatalogService.LoadDetailedAsync(
                broadcasterId,
                _lifetimeCancellation.Token).ConfigureAwait(false);
            var catalog = new Dictionary<string, ThirdPartyEmote>(load.Catalog, StringComparer.Ordinal);
            IReadOnlyDictionary<string, ThirdPartyEmote>? previousCatalog = null;
            string? previousBroadcasterId = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _thirdPartyCatalogs.TryGetValue(channel, out previousCatalog);
                _thirdPartyCatalogBroadcasterIds.TryGetValue(channel, out previousBroadcasterId);
            });
            if (!load.IsComplete && previousCatalog is not null &&
                string.Equals(previousBroadcasterId, broadcasterId.Trim(), StringComparison.Ordinal))
            {
                foreach (var previous in previousCatalog)
                {
                    if (load.FailedProviders.Contains(previous.Value.Provider))
                    {
                        catalog.TryAdd(previous.Key, previous.Value);
                    }
                }
            }
            await ApplyToChannelMessagesInBatchesAsync(
                channel,
                () =>
                {
                    _thirdPartyCatalogs[channel] = catalog;
                    _thirdPartyCatalogBroadcasterIds[channel] = broadcasterId.Trim();
                    if (load.IsComplete)
                    {
                        _thirdPartyFailureCounts.Remove(channel);
                        _thirdPartyRetryAfter.Remove(channel);
                    }
                    else
                    {
                        var failures = _thirdPartyFailureCounts.GetValueOrDefault(channel) + 1;
                        _thirdPartyFailureCounts[channel] = failures;
                        var retrySeconds = Math.Min(120, Math.Pow(2, Math.Min(failures, 6)));
                        _thirdPartyRetryAfter[channel] = DateTimeOffset.UtcNow.AddSeconds(retrySeconds);
                        StatusDetail = Texts.EmotesUnavailable(
                            string.Join(", ", load.FailedProviders));
                    }
                },
                message => message.ApplyPresentationSettings(
                    EnableTwitchEmotes,
                    EnableBttvEmotes,
                    EnableSevenTvEmotes,
                    catalog,
                    useLightTwitchTheme: string.Equals(Theme, "Light", StringComparison.Ordinal))).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException or JsonException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _thirdPartyRefreshRequested.Remove(channel);
                if (!_disposed)
                {
                    StatusDetail = Texts.EmotesUnavailable(AppDiagnostics.GetUserMessage(exception));
                }
            });
        }
        finally
        {
            if (lockTaken)
            {
                _thirdPartyRefreshLock.Release();
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
                _thirdPartyRefreshRequested.Remove(channel));
        }
    }

    private bool NeedsThirdPartyCatalogRefresh(string channel, string broadcasterId)
    {
        if (_thirdPartyRetryAfter.TryGetValue(channel, out var retryAfter))
        {
            return retryAfter <= DateTimeOffset.UtcNow;
        }
        if (!_thirdPartyCatalogs.ContainsKey(channel))
        {
            return true;
        }

        broadcasterId = broadcasterId.Trim();
        return broadcasterId.Length > 0 &&
               (!_thirdPartyCatalogBroadcasterIds.TryGetValue(channel, out var loadedBroadcasterId) ||
                !string.Equals(loadedBroadcasterId, broadcasterId, StringComparison.Ordinal));
    }

    private void QueueImagePreload(IEnumerable<Uri?> imageUris)
    {
        var snapshot = imageUris.Where(uri => uri is not null).ToArray();
        if (snapshot.Length > 0)
        {
            _ = PreloadImagesSafeAsync(snapshot);
        }
    }

    private async Task PreloadImagesSafeAsync(IReadOnlyList<Uri?> imageUris)
    {
        try
        {
            await _imageCache.PreloadAsync(
                imageUris,
                _lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
    }

    private async Task ApplyToChannelMessagesInBatchesAsync(
        string channel,
        Action updateCatalog,
        Action<ChatMessageItemViewModel> updateMessage)
    {
        ChatMessageItemViewModel[] snapshot = [];
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            updateCatalog();
            if (!IsFollowingLatest)
            {
                _pendingCatalogApplicationChannels.Add(channel);
            }

            var visibleMessages = IsFollowingLatest
                ? Messages
                : Enumerable.Empty<ChatMessageItemViewModel>();
            snapshot = visibleMessages
                .Concat(_deferredMessages)
                .Where(item => string.Equals(item.Message.Channel, channel, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        });

        const int updateBatchSize = 96;
        for (var offset = 0; offset < snapshot.Length; offset += updateBatchSize)
        {
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            var start = offset;
            var end = Math.Min(snapshot.Length, start + updateBatchSize);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                for (var index = start; index < end; index++)
                {
                    updateMessage(snapshot[index]);
                }
            }, DispatcherPriority.Background);

            if (end < snapshot.Length)
            {
                await Task.Delay(1, _lifetimeCancellation.Token).ConfigureAwait(false);
            }
        }

        if (snapshot.Length > 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsFollowingLatest && !_disposed)
                {
                    ScrollToLatestRequested?.Invoke(this, EventArgs.Empty);
                }
            });
        }
    }

    private void StartPendingCatalogApplication()
    {
        if (_pendingCatalogApplicationChannels.Count == 0)
        {
            return;
        }

        var channels = _pendingCatalogApplicationChannels.ToArray();
        _pendingCatalogApplicationChannels.Clear();
        _ = ApplyPendingCatalogsAsync(channels);
    }

    private async Task ApplyPendingCatalogsAsync(IReadOnlyCollection<string> channels)
    {
        try
        {
            var snapshot = Messages
                .Where(item => channels.Contains(item.Message.Channel, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            const int updateBatchSize = 96;
            for (var offset = 0; offset < snapshot.Length; offset += updateBatchSize)
            {
                _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                var start = offset;
                var end = Math.Min(snapshot.Length, start + updateBatchSize);
                var paused = false;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_disposed || !IsFollowingLatest)
                    {
                        paused = !_disposed;
                        if (paused)
                        {
                            foreach (var channel in channels)
                            {
                                _pendingCatalogApplicationChannels.Add(channel);
                            }
                        }

                        return;
                    }

                    for (var index = start; index < end; index++)
                    {
                        var item = snapshot[index];
                        if (_badgeCatalogs.TryGetValue(item.Message.Channel, out var badgeCatalog))
                        {
                            item.ApplyBadgeCatalog(badgeCatalog);
                        }

                        if (_thirdPartyCatalogs.TryGetValue(item.Message.Channel, out var thirdPartyCatalog))
                        {
                            item.ApplyPresentationSettings(
                                EnableTwitchEmotes,
                                EnableBttvEmotes,
                                EnableSevenTvEmotes,
                                thirdPartyCatalog,
                                useLightTwitchTheme: string.Equals(Theme, "Light", StringComparison.Ordinal));
                        }
                    }
                }, DispatcherPriority.Background);

                if (paused)
                {
                    return;
                }

                if (end < snapshot.Length)
                {
                    await Task.Delay(1, _lifetimeCancellation.Token).ConfigureAwait(false);
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsFollowingLatest && !_disposed)
                {
                    ScrollToLatestRequested?.Invoke(this, EventArgs.Empty);
                }
            });
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
    }

    private async Task RefreshMessageLocalizationAsync()
    {
        try
        {
            var snapshot = Messages.ToArray();
            const int updateBatchSize = 96;
            for (var offset = 0; offset < snapshot.Length && !_disposed; offset += updateBatchSize)
            {
                var start = offset;
                var end = Math.Min(snapshot.Length, start + updateBatchSize);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    for (var index = start; index < end; index++)
                    {
                        snapshot[index].RefreshLocalization();
                    }
                }, DispatcherPriority.Background);

                if (end < snapshot.Length)
                {
                    await Task.Delay(1, _lifetimeCancellation.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
    }

    private void ApplyLanguage(string value)
    {
        Texts.SetLanguage(value);
        RefreshLocalizedOptions();
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(ProductVersionLabel));
        OnPropertyChanged(nameof(ChatEmptyText));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(ActiveChannelLabel));
        OnPropertyChanged(nameof(ApiConnectionLabel));
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(StreamStatusText));
        OnPropertyChanged(nameof(StreamViewerText));
        OnPropertyChanged(nameof(CloseWindowTip));
        OnPropertyChanged(nameof(HeaderToggleTip));
        OnPropertyChanged(nameof(WindowMaximizeTip));
        OnPropertyChanged(nameof(HeaderDisconnectTip));
        OnPropertyChanged(nameof(CompactModeTip));
        OnPropertyChanged(nameof(ComposerToggleTip));
        OnPropertyChanged(nameof(TwitchClipButtonTip));
        OnPropertyChanged(nameof(SharedChatStatusLabel));
        OnPropertyChanged(nameof(ReadOnlyComposerNotice));
        OnPropertyChanged(nameof(YouTubeAccountLabel));
        OnPropertyChanged(nameof(CurrentDonationRemainingLabel));
        OnPropertyChanged(nameof(CurrentDonationUsername));
        OnPropertyChanged(nameof(CurrentDonationMessage));
        OnPropertyChanged(nameof(DonationAlertsObsControlStatus));
        OnPropertyChanged(nameof(WelcomeActionText));
        OnPropertyChanged(nameof(ModerationConfirmLabel));
        OnPropertyChanged(nameof(ModerationDurationLabel));
        OnPropertyChanged(nameof(ModerationChannelLabel));
        OnPropertyChanged(nameof(PinnedCardText));
        OnPropertyChanged(nameof(EmptyUnbanRequestsLabel));
        OnPropertyChanged(nameof(OnboardingTitle));
        OnPropertyChanged(nameof(OnboardingDescription));
        OnPropertyChanged(nameof(OnboardingHint));
        OnPropertyChanged(nameof(OnboardingNextLabel));
        OnPropertyChanged(nameof(TutorialEyebrow));
        OnPropertyChanged(nameof(TutorialCloseLabel));
        StatusDetail = ConnectionState switch
        {
            ChatConnectionState.Connected => Texts.ConnectedTo(Channel),
            ChatConnectionState.Reconnecting => Texts.ConnectionLost,
            ChatConnectionState.Connecting => Texts.ConnectingTo(Channel),
            ChatConnectionState.Error => Texts.CouldNotConnect,
            _ => Texts.InitialStatus
        };
        AuthStatus = IsAccountConnected
            ? Texts.AccountConnected
            : _authSessionStore.IsPersistent ? Texts.SessionTemporary : Texts.SecureStorageUnavailable;
        YouTubeStatus = IsYouTubeLiveConnected
            ? Texts.YouTubeChatConnected + (string.IsNullOrWhiteSpace(YouTubeBroadcastTitle)
                ? string.Empty
                : ": " + YouTubeBroadcastTitle)
            : IsYouTubeConnected ? Texts.YouTubeWaitingForBroadcast : Texts.YouTubeNotConnected;
        DonationAlertsStatus = IsDonationAlertsRealtimeConnected
            ? Texts.DonationAlertsConnected
            : IsDonationAlertsConnected
                ? Texts.DonationAlertsReconnecting
                : _donationAlertsHistoryPermissionRequired
                    ? Texts.DonationAlertsReconnectForHistory
                    : Texts.DonationAlertsNotConnected;
        OnPropertyChanged(nameof(DonationAlertsAccountLabel));
        OnPropertyChanged(nameof(DonationQueueLabel));
        RebuildDonationHistoryLocalization();
        for (var index = 0; index < UnbanRequests.Count; index++)
        {
            UnbanRequests[index] = new UnbanRequestViewModel(UnbanRequests[index].Value, Texts);
        }
        for (var index = 0; index < YouTubeBans.Count; index++)
        {
            YouTubeBans[index] = new YouTubeChatBanViewModel(YouTubeBans[index].Value, Texts);
        }
        for (var index = 0; index < StreamEvents.Count; index++)
        {
            StreamEvents[index] = new StreamEventItemViewModel(StreamEvents[index].Value, Texts);
        }
        RebuildVisibleStreamEvents();
        OnPropertyChanged(nameof(VisibleUnbanRequests));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        _ = RefreshMessageLocalizationAsync();
    }

    private void RefreshLocalizedOptions()
    {
        var previousThemeOptions = ThemeOptions;
        var previousLanguageOptions = LanguageOptions;
        var previousFontOptions = FontOptions;
        var previousWindowControlOptions = WindowControlsPositionOptions;
        var previousWindowControlStyleOptions = WindowControlsStyleOptions;
        var previousMessageVisualThemeOptions = MessageVisualThemeOptions;
        var previousOverlayAlignmentOptions = OverlayAlignmentOptions;
        var previousLogRoleOptions = LogRoleOptions;

        ThemeOptions = ReconcileOptions(ThemeOptions,
        [
            new SelectionOptionViewModel("Dark", Texts.DarkTheme),
            new SelectionOptionViewModel("Light", Texts.LightTheme),
            new SelectionOptionViewModel("System", Texts.SystemTheme)
        ]);
        LanguageOptions = ReconcileOptions(LanguageOptions,
        [
            new SelectionOptionViewModel("ru", Texts.RussianLanguage),
            new SelectionOptionViewModel("en", Texts.EnglishLanguage)
        ]);
        FontOptions = ReconcileOptions(FontOptions,
        [
            new SelectionOptionViewModel("SegoeUIVariable", "Segoe UI Variable"),
            new SelectionOptionViewModel("Inter", "Inter"),
            new SelectionOptionViewModel("SegoeUI", "Segoe UI"),
            new SelectionOptionViewModel("Aptos", "Aptos"),
            new SelectionOptionViewModel("Bahnschrift", "Bahnschrift"),
            new SelectionOptionViewModel("Calibri", "Calibri"),
            new SelectionOptionViewModel("Candara", "Candara"),
            new SelectionOptionViewModel("Trebuchet", "Trebuchet MS")
        ]);
        WindowControlsPositionOptions = ReconcileOptions(WindowControlsPositionOptions,
        [
            new SelectionOptionViewModel("Left", Texts.WindowControlsLeft),
            new SelectionOptionViewModel("Right", Texts.WindowControlsRight)
        ]);
        WindowControlsStyleOptions = ReconcileOptions(WindowControlsStyleOptions,
        [
            new SelectionOptionViewModel("Mac", Texts.WindowControlsMac),
            new SelectionOptionViewModel("Windows", Texts.WindowControlsWindows)
        ]);
        MessageVisualThemeOptions = ReconcileOptions(MessageVisualThemeOptions,
        [
            new SelectionOptionViewModel("Default", Texts.MessageThemeDefault),
            new SelectionOptionViewModel("TornBlack", Texts.MessageThemeTornBlack)
        ]);
        OverlayAlignmentOptions = ReconcileOptions(OverlayAlignmentOptions,
        [
            new SelectionOptionViewModel("left", Texts.AlignLeft),
            new SelectionOptionViewModel("center", Texts.AlignCenter),
            new SelectionOptionViewModel("right", Texts.AlignRight)
        ]);
        LogRoleOptions = ReconcileOptions(LogRoleOptions,
        [
            new SelectionOptionViewModel(string.Empty, Texts.AllRoles),
            new SelectionOptionViewModel("broadcaster", Texts.BroadcasterRole),
            new SelectionOptionViewModel("moderator", Texts.ModeratorRole),
            new SelectionOptionViewModel("subscriber", Texts.SubscriberRole),
            new SelectionOptionViewModel("vip", Texts.VipRole)
        ]);
        if (!ReferenceEquals(previousThemeOptions, ThemeOptions))
        {
            OnPropertyChanged(nameof(ThemeOptions));
        }
        if (!ReferenceEquals(previousLanguageOptions, LanguageOptions))
        {
            OnPropertyChanged(nameof(LanguageOptions));
        }
        if (!ReferenceEquals(previousFontOptions, FontOptions))
        {
            OnPropertyChanged(nameof(FontOptions));
        }
        if (!ReferenceEquals(previousWindowControlOptions, WindowControlsPositionOptions))
        {
            OnPropertyChanged(nameof(WindowControlsPositionOptions));
        }
        if (!ReferenceEquals(previousWindowControlStyleOptions, WindowControlsStyleOptions))
        {
            OnPropertyChanged(nameof(WindowControlsStyleOptions));
        }
        if (!ReferenceEquals(previousMessageVisualThemeOptions, MessageVisualThemeOptions))
        {
            OnPropertyChanged(nameof(MessageVisualThemeOptions));
        }
        if (!ReferenceEquals(previousOverlayAlignmentOptions, OverlayAlignmentOptions))
        {
            OnPropertyChanged(nameof(OverlayAlignmentOptions));
        }
        if (!ReferenceEquals(previousLogRoleOptions, LogRoleOptions))
        {
            OnPropertyChanged(nameof(LogRoleOptions));
        }
        OnPropertyChanged(nameof(SelectedThemeOption));
        OnPropertyChanged(nameof(SelectedLanguageOption));
        OnPropertyChanged(nameof(SelectedFontOption));
        OnPropertyChanged(nameof(SelectedWindowControlsPositionOption));
        OnPropertyChanged(nameof(SelectedWindowControlsStyleOption));
        OnPropertyChanged(nameof(SelectedMessageVisualThemeOption));
        OnPropertyChanged(nameof(SelectedOverlayAlignmentOption));
        OnPropertyChanged(nameof(SelectedLogRoleOption));
    }

    private static IReadOnlyList<SelectionOptionViewModel> ReconcileOptions(
        IReadOnlyList<SelectionOptionViewModel> current,
        IReadOnlyList<SelectionOptionViewModel> localized)
    {
        if (current.Count != localized.Count || current.Where((item, index) =>
                !string.Equals(item.Value, localized[index].Value, StringComparison.Ordinal)).Any())
        {
            return localized;
        }

        for (var index = 0; index < current.Count; index++)
        {
            current[index].Label = localized[index].Label;
        }
        return current;
    }

#if DEBUG
    internal void ApplyLanguageForTesting(string value)
    {
        _suppressLanguageSave = true;
        try
        {
            Language = value;
        }
        finally
        {
            _suppressLanguageSave = false;
        }
    }

    internal void OpenModerationForTesting()
    {
        IsModerationPanelOpen = true;
        PendingAutoModMessages.AppendBatch(
        [
            new HeldAutoModMessageViewModel(new HeldAutoModMessage(
                "ui-test-held", "1", Channel, "42", "tester", "Tester",
                "Сообщение, остановленное AutoMod для проверки интерфейса.",
                "aggression", 3, DateTimeOffset.Now))
        ], 1000);
    }
#endif

    private void CancelAuthorizationCore()
    {
        var cancellation = _authorizationCancellation;
        _authorizationCancellation = null;
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
        finally
        {
            cancellation.Dispose();
        }
    }

    private void QueueSettingsSave()
    {
        if (_disposed || _suppressSettingsPersistence || _settingsOpenSnapshot is not null)
        {
            return;
        }

        var task = SaveSettingsSafeAsync(CreateSettingsSnapshot());
        lock (_settingsSaveTasksLock)
        {
            _settingsSaveTasks.Add(task);
        }

        _ = TrackSettingsSaveAsync(task);
    }

    private async Task TrackSettingsSaveAsync(Task task)
    {
        await task.ConfigureAwait(false);
        lock (_settingsSaveTasksLock)
        {
            _settingsSaveTasks.Remove(task);
        }
    }

    private async Task SaveSettingsSafeAsync(WitherChatSettings? settings = null)
    {
        try
        {
            await _settingsStore.SaveAsync(settings ?? CreateSettingsSnapshot()).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Dispatcher.UIThread.Post(() =>
                StatusDetail = Texts.SettingsSaveFailed(AppDiagnostics.GetUserMessage(exception)));
        }
    }

    private WitherChatSettings CreateSettingsSnapshot() => new()
    {
        Channel = Channel,
        SavedChannels = SavedChannels
            .Where(channel => !channel.IsPrimaryAccountChannel)
            .Select(channel => channel.Login)
            .ToList(),
        Theme = Theme,
        Language = Language,
        AlwaysOnTop = AlwaysOnTop,
        ToastNotifications = ToastNotifications,
        CloseToTray = CloseToTray,
        MessageLimit = MessageLimit,
        ViewerCountRefreshIntervalSeconds = ViewerCountRefreshIntervalSeconds,
        ChatFontSize = ChatFontSize,
        ShowTimestamps = ShowTimestamps,
        ShowBadges = ShowBadges,
        EnableTwitchEmotes = EnableTwitchEmotes,
        EnableBttvEmotes = EnableBttvEmotes,
        EnableSevenTvEmotes = EnableSevenTvEmotes,
        ShowChannelPointRedemptions = ShowChannelPointRedemptions,
        SplitChatView = IsSplitChatView,
        MessageVisualTheme = MessageVisualTheme,
        UiFontFamily = UiFontFamily,
        WindowControlsOnRight = WindowControlsOnRight,
        WindowControlsStyle = WindowControlsStyle,
        ReduceMotion = ReduceMotion,
        HasCompletedOnboarding = _hasCompletedOnboarding,
        UseCustomClientId = UseCustomClientId,
        ClientId = ClientId,
        RedirectUri = RedirectUri,
        DonationAlertsAutoOpenWindow = DonationAlertsAutoOpenWindow,
        EnableObsOverlay = EnableObsOverlay,
        OverlayPort = OverlayPort,
        OverlayMaxMessages = OverlayMaxMessages,
        OverlayFontSize = OverlayFontSize,
        OverlayShowTimestamps = OverlayShowTimestamps,
        OverlayShowBadges = OverlayShowBadges,
        OverlayShowEmotes = OverlayShowEmotes,
        OverlayFadeOutSeconds = OverlayFadeOutSeconds,
        OverlayTextShadow = OverlayTextShadow,
        OverlayTextOutline = OverlayTextOutline,
        OverlayDarkBackground = OverlayDarkBackground,
        OverlayBackgroundOpacity = OverlayBackgroundOpacity,
        OverlayAlign = OverlayAlign,
        EnableChatLogging = EnableChatLogging,
        SaveChatLogTxt = SaveChatLogTxt,
        LogChatBadges = LogChatBadges,
        LogChannelPointRedemptions = LogChannelPointRedemptions,
        ChatLogsFolder = ChatLogsFolder,
        MaxLogViewerMessages = MaxLogViewerMessages
    };

    private void ApplySettingsSnapshot(WitherChatSettings settings)
    {
        Theme = settings.Theme;
        Language = settings.Language;
        AlwaysOnTop = settings.AlwaysOnTop;
        ToastNotifications = settings.ToastNotifications;
        CloseToTray = settings.CloseToTray;
        MessageLimit = settings.MessageLimit;
        ViewerCountRefreshIntervalSeconds = settings.ViewerCountRefreshIntervalSeconds;
        ChatFontSize = settings.ChatFontSize;
        ShowTimestamps = settings.ShowTimestamps;
        ShowBadges = settings.ShowBadges;
        EnableTwitchEmotes = settings.EnableTwitchEmotes;
        EnableBttvEmotes = settings.EnableBttvEmotes;
        EnableSevenTvEmotes = settings.EnableSevenTvEmotes;
        ShowChannelPointRedemptions = settings.ShowChannelPointRedemptions;
        IsSplitChatView = settings.SplitChatView;
        MessageVisualTheme = settings.MessageVisualTheme;
        UiFontFamily = settings.UiFontFamily;
        WindowControlsOnRight = settings.WindowControlsOnRight;
        WindowControlsStyle = settings.WindowControlsStyle;
        ReduceMotion = settings.ReduceMotion;
        _hasCompletedOnboarding = settings.HasCompletedOnboarding;
        UseCustomClientId = settings.UseCustomClientId;
        ClientId = settings.ClientId;
        RedirectUri = settings.RedirectUri;
        DonationAlertsAutoOpenWindow = settings.DonationAlertsAutoOpenWindow;
        EnableObsOverlay = settings.EnableObsOverlay;
        OverlayPort = settings.OverlayPort;
        OverlayMaxMessages = settings.OverlayMaxMessages;
        OverlayFontSize = settings.OverlayFontSize;
        OverlayShowTimestamps = settings.OverlayShowTimestamps;
        OverlayShowBadges = settings.OverlayShowBadges;
        OverlayShowEmotes = settings.OverlayShowEmotes;
        OverlayFadeOutSeconds = settings.OverlayFadeOutSeconds;
        OverlayTextShadow = settings.OverlayTextShadow;
        OverlayTextOutline = settings.OverlayTextOutline;
        OverlayDarkBackground = settings.OverlayDarkBackground;
        OverlayBackgroundOpacity = settings.OverlayBackgroundOpacity;
        OverlayAlign = settings.OverlayAlign;
        EnableChatLogging = settings.EnableChatLogging;
        SaveChatLogTxt = settings.SaveChatLogTxt;
        LogChatBadges = settings.LogChatBadges;
        LogChannelPointRedemptions = settings.LogChannelPointRedemptions;
        ChatLogsFolder = settings.ChatLogsFolder;
        MaxLogViewerMessages = settings.MaxLogViewerMessages;
        ApplyChatLoggingSettings();
        TryApplyOAuthConfiguration();
        _ = ApplyOverlaySettingsAsync();
    }

    private void TryApplyOAuthConfiguration()
    {
        var effectiveClientId = UseCustomClientId && !string.IsNullOrWhiteSpace(ClientId)
            ? ClientId.Trim()
            : TwitchApplication.ClientId;
        try
        {
            _authService.Configure(effectiveClientId, RedirectUri);
            _chatApiClient.ConfigureClientId(effectiveClientId);
        }
        catch (ArgumentException)
        {
            // Keep the last valid runtime configuration while the user edits the URI.
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Startup must recover from an invalid or expired DonationAlerts session.")]
    private async Task InitializeDonationAlertsAsync()
    {
        if (_donationAlertsAuthService is null || _donationAlertsAuthSessionStore is null ||
            _donationAlertsClient is null)
        {
            DonationAlertsStatus = Texts.DonationAlertsNotConnected;
            return;
        }
        var storedSession = _donationAlertsAuthSessionStore.Load();
        if (storedSession is null ||
            !string.Equals(storedSession.ClientId, DonationAlertsApplication.ClientId, StringComparison.Ordinal))
        {
            DonationAlertsStatus = Texts.DonationAlertsNotConnected;
            return;
        }
        if (!DonationAlertsApplication.HasRequiredScopes(storedSession.Scopes))
        {
            _donationAlertsAuthSessionStore.Clear();
            _donationAlertsHistoryPermissionRequired = true;
            DonationAlertsStatus = Texts.DonationAlertsReconnectForHistory;
            return;
        }
        try
        {
            _donationAlertsAuthService.Configure(DonationAlertsApplication.ClientId);
            var session = await _donationAlertsAuthService.ValidateAsync(
                storedSession,
                _lifetimeCancellation.Token).ConfigureAwait(true);
            _donationAlertsAuthSessionStore.Save(session);
            ApplyDonationAlertsSession(session);
            DonationAlertsStatus = Texts.DonationAlertsConnecting;
            await TryConnectDonationAlertsControlAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
            await _donationAlertsClient.StartAsync(session, _lifetimeCancellation.Token).ConfigureAwait(true);
            await RefreshDonationHistoryAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or
                System.Net.HttpStatusCode.Forbidden)
        {
            _donationAlertsAuthSessionStore.Clear();
            _donationAlertsAuthSession = null;
            DonationAlertsAccountName = string.Empty;
            DonationAlertsStatus = Texts.SignInFailed(AppDiagnostics.GetUserMessage(exception));
            OnPropertyChanged(nameof(IsDonationAlertsConnected));
            OnPropertyChanged(nameof(CanConnectDonationAlerts));
            ConnectDonationAlertsCommand.NotifyCanExecuteChanged();
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write("Restore DonationAlerts session", exception);
            ApplyDonationAlertsSession(storedSession);
            DonationAlertsStatus = Texts.DonationAlertsReconnecting;
            try
            {
                await _donationAlertsClient.StartAsync(
                    storedSession,
                    _lifetimeCancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
            {
            }
        }
    }

    private void ApplyDonationAlertsSession(DonationAlertsAuthSession session)
    {
        _donationAlertsAuthSession = session;
        _donationAlertsHistoryPermissionRequired = false;
        DonationAlertsAccountName = string.IsNullOrWhiteSpace(session.DisplayName)
            ? session.UserCode
            : session.DisplayName;
        if (!IsDonationAlertsRealtimeConnected)
        {
            DonationAlertsStatus = Texts.DonationAlertsConnecting;
        }
        OnPropertyChanged(nameof(IsDonationAlertsConnected));
        OnPropertyChanged(nameof(DonationAlertsAccountLabel));
        OnPropertyChanged(nameof(CanConnectDonationAlerts));
        NotifyDonationHistoryStateChanged();
        ConnectDonationAlertsCommand.NotifyCanExecuteChanged();
    }

    private void OnDonationReceived(object? sender, DonationAlertEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            var donation = eventArgs.Donation;
            var value = new StreamEvent
            {
                Id = "da-" + donation.Id,
                Platform = "DonationAlerts",
                Channel = Channel,
                Kind = StreamEventKinds.Donation,
                DisplayName = donation.Username,
                Message = donation.Message,
                AmountDisplay = donation.Amount.ToString("0.##", CultureInfo.CurrentCulture) + " " + donation.Currency,
                AmountMicros = decimal.ToInt64(decimal.Round(donation.Amount * 1_000_000m)),
                Currency = donation.Currency,
                Timestamp = donation.ReceivedAtUtc
            };
            AddStreamEvent(value);
            OnMessageReceived(this, new ChatMessageEventArgs(TwitchEventSubClient.ToSystemMessage(value)));
            EnqueueLiveDonation(donation);
        });

    private void OnStreamEventReceived(object? sender, StreamEventEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() => AddStreamEvent(eventArgs.Value));

    private void AddStreamEvent(StreamEvent value)
    {
        if (!_streamEventIds.Add(value.Platform + ":" + value.Id))
        {
            return;
        }
        var item = new StreamEventItemViewModel(value, Texts);
        StreamEvents.AppendBatch([item], 1000);
        if (MatchesStreamEventFilter(item))
        {
            VisibleStreamEvents.AppendBatch([item], 1000);
        }
        OnPropertyChanged(nameof(HasStreamEvents));
        OnPropertyChanged(nameof(HasHeaderOverflowActivity));
        OnPropertyChanged(nameof(HasVisibleStreamEvents));
    }

    private void RebuildVisibleStreamEvents()
    {
        VisibleStreamEvents.Clear();
        VisibleStreamEvents.AppendBatch(StreamEvents.Where(MatchesStreamEventFilter).ToArray(), 1000);
        OnPropertyChanged(nameof(HasVisibleStreamEvents));
    }

    private bool MatchesStreamEventFilter(StreamEventItemViewModel item) => StreamEventFilter switch
    {
        1 => string.Equals(item.Value.Platform, ChatPlatforms.Twitch, StringComparison.OrdinalIgnoreCase),
        2 => string.Equals(item.Value.Platform, ChatPlatforms.YouTube, StringComparison.OrdinalIgnoreCase),
        3 => item.Value.IsPaid || string.Equals(item.Value.Platform, "DonationAlerts", StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    private void OnDonationPlaybackStateChanged(
        object? sender,
        DonationPlaybackSnapshot snapshot) =>
        Dispatcher.UIThread.Post(() => ApplyDonationPlaybackSnapshot(snapshot));

    private void OnDonationAlertsStatusChanged(
        object? sender,
        DonationAlertsConnectionStatusEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsDonationAlertsRealtimeConnected = eventArgs.IsConnected;
            IsDonationAlertsReconnecting = eventArgs.IsReconnecting;
            DonationAlertsStatus = eventArgs.IsConnected
                ? Texts.DonationAlertsConnected
                : eventArgs.IsReconnecting
                    ? Texts.DonationAlertsReconnecting
                    : IsDonationAlertsConnected
                        ? Texts.DonationAlertsReconnecting
                        : Texts.DonationAlertsNotConnected;
        });

    private void EnqueueDonation(DonationAlert donation, bool addToHistory = true)
    {
        if (addToHistory)
        {
            _ = InsertDonationHistory(donation);
        }
        if (CanUseDonationAlertsDirectControl(donation))
        {
            _ = _donationPlaybackCoordinator!.TryEnqueueLive(donation);
            if (DonationAlertsAutoOpenWindow)
            {
                DonationWindowRequested?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        if (CurrentDonation is null)
        {
            StartDonationDisplay(donation);
        }
        else
        {
            _pendingDonations.Enqueue(donation);
            PendingDonationCount = _pendingDonations.Count;
        }
        if (DonationAlertsAutoOpenWindow)
        {
            DonationWindowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EnqueueLiveDonation(DonationAlert donation)
    {
        ArgumentNullException.ThrowIfNull(donation);
        if (!_liveDonationIds.Add(donation.Id))
        {
            return;
        }
        EnqueueDonation(donation);
    }

    internal void EnqueueDonationForTesting(DonationAlert donation) => EnqueueLiveDonation(donation);

    internal void EnqueueMessageForTesting(ChatMessage message) =>
        OnMessageReceived(this, new ChatMessageEventArgs(message));

    internal void EnqueueStreamEventForTesting(StreamEvent streamEvent) =>
        OnStreamEventReceived(this, new StreamEventEventArgs(streamEvent));

    internal TimeSpan? DonationDisplayDurationOverride { get; set; }

    internal TimeSpan? DonationHistoryRefreshTimeoutOverride { get; set; }

    private void ApplyDonationPlaybackSnapshot(DonationPlaybackSnapshot snapshot)
    {
        DonationPlaybackState = snapshot.State;
        CurrentDonation = snapshot.CurrentDonation;
        PendingDonationCount = snapshot.PendingCount;
        DonationPresentationOpacity = snapshot.State is DonationPlaybackState.Exiting or
            DonationPlaybackState.Completed or DonationPlaybackState.Skipped
            ? 0
            : 1;
        DonationDisplayProgress = snapshot.State == DonationPlaybackState.Playing ? 100 : 0;
        CurrentDonationSecondsRemaining = 0;
        DonationControlError = snapshot.Error switch
        {
            "" => string.Empty,
            DonationPlaybackCoordinator.ConnectionLostError => Texts.DonationAlertsControlReconnecting,
            DonationPlaybackCoordinator.ServerStartTimeoutError => Texts.DonationServerStartTimedOut,
            _ => Texts.DonationAlertsObsControlFailed(snapshot.Error)
        };
        NotifyDonationAlertsObsControlStateChanged();
        OnPropertyChanged(nameof(CanHideDonation));
    }

    private async Task TryConnectDonationAlertsControlAsync(CancellationToken cancellationToken)
    {
        if (_donationPlaybackCoordinator is null ||
            _donationAlertsObsController?.IsConfigured != true)
        {
            return;
        }
        try
        {
            await _donationPlaybackCoordinator.EnsureConnectedAsync(cancellationToken).ConfigureAwait(true);
            DonationControlError = string.Empty;
        }
        catch (Exception exception) when (IsDonationControlException(exception))
        {
            DonationControlError = Texts.DonationAlertsObsControlFailed(AppDiagnostics.GetUserMessage(exception));
        }
        finally
        {
            NotifyDonationAlertsObsControlStateChanged();
        }
    }

    private bool IsCurrentDonationControlled =>
        CurrentDonation is not null &&
        _donationPlaybackCoordinator?.Snapshot.CurrentDonation?.Id == CurrentDonation.Id;

    private bool CanUseDonationAlertsDirectControl(DonationAlert donation) =>
        _donationPlaybackCoordinator is not null &&
        _donationAlertsObsController is { IsConfigured: true, IsConnected: true } &&
        IsDonationAlertsDonation(donation);

    private static bool IsDonationAlertsDonation(DonationAlert donation) =>
        long.TryParse(donation.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
        id > 0;

    private void NotifyDonationAlertsObsControlStateChanged()
    {
        OnPropertyChanged(nameof(IsDonationAlertsObsControlConfigured));
        OnPropertyChanged(nameof(DonationAlertsObsControlStatus));
        OnPropertyChanged(nameof(DonationAlertsObsControlBrush));
    }

    private static bool IsDonationControlException(Exception exception) =>
        exception is HttpRequestException or IOException or InvalidDataException or
            InvalidOperationException or JsonException or OperationCanceledException or
            WebSocketException;

    private void StartDonationDisplay(DonationAlert donation)
    {
        ArgumentNullException.ThrowIfNull(donation);
        _donationDisplayTimer.Stop();
        CurrentDonation = donation;
        _donationDisplayDuration = DonationDisplayDurationOverride ?? CalculateDonationDisplayDuration(donation);
        if (_donationDisplayDuration <= TimeSpan.Zero)
        {
            _donationDisplayDuration = MinimumDonationDisplayDuration;
        }
        _donationDisplayEndsAtUtc = DateTimeOffset.UtcNow + _donationDisplayDuration;
        DonationDisplayProgress = 100;
        CurrentDonationSecondsRemaining = Math.Max(1, (int)Math.Ceiling(_donationDisplayDuration.TotalSeconds));
        _donationDisplayTimer.Start();
    }

    private void OnDonationDisplayTimerTick(object? sender, EventArgs eventArgs)
    {
        var remaining = _donationDisplayEndsAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            AdvanceDonationDisplay();
            return;
        }

        DonationDisplayProgress = Math.Clamp(
            remaining.TotalMilliseconds / _donationDisplayDuration.TotalMilliseconds * 100,
            0,
            100);
        CurrentDonationSecondsRemaining = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
    }

    private void AdvanceDonationDisplay()
    {
        _donationDisplayTimer.Stop();
        if (_pendingDonations.TryDequeue(out var next))
        {
            PendingDonationCount = _pendingDonations.Count;
            StartDonationDisplay(next);
            return;
        }

        PendingDonationCount = 0;
        StopDonationDisplay();
    }

    private void StopDonationDisplay()
    {
        _donationDisplayTimer.Stop();
        CurrentDonation = null;
        DonationDisplayProgress = 0;
        CurrentDonationSecondsRemaining = 0;
    }

    private static TimeSpan CalculateDonationDisplayDuration(DonationAlert donation)
    {
        var seconds = 7 + Math.Ceiling(donation.Message.Length / 32d);
        return TimeSpan.FromSeconds(Math.Clamp(
            seconds,
            MinimumDonationDisplayDuration.TotalSeconds,
            MaximumDonationDisplayDuration.TotalSeconds));
    }

    private bool InsertDonationHistory(DonationAlert donation)
    {
        if (!_donationHistoryIds.Add(donation.Id))
        {
            return false;
        }
        DonationHistory.Insert(
            0,
            new DonationHistoryItemViewModel(
                donation,
                string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase)));
        NotifyDonationHistoryStateChanged();
        return true;
    }

    private void ApplyDonationHistory(IReadOnlyList<DonationAlert> donations)
    {
        var merged = new Dictionary<string, DonationAlert>(StringComparer.Ordinal);
        foreach (var item in DonationHistory)
        {
            merged[item.Id] = item.Donation;
        }
        foreach (var donation in donations)
        {
            merged.TryAdd(donation.Id, donation);
        }

        DonationHistory.Clear();
        _donationHistoryIds.Clear();
        var useEnglish = string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);
        foreach (var donation in merged.Values
                     .OrderByDescending(item => item.ReceivedAtUtc)
                     .ThenByDescending(item => item.Id, StringComparer.Ordinal))
        {
            _donationHistoryIds.Add(donation.Id);
            DonationHistory.Add(new DonationHistoryItemViewModel(donation, useEnglish));
        }
        NotifyDonationHistoryStateChanged();
    }

    private void RebuildDonationHistoryLocalization()
    {
        var useEnglish = string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);
        for (var index = 0; index < DonationHistory.Count; index++)
        {
            DonationHistory[index] = new DonationHistoryItemViewModel(
                DonationHistory[index].Donation,
                useEnglish);
        }
        NotifyDonationHistoryStateChanged();
    }

    private void ClearDonationHistory()
    {
        DonationHistory.Clear();
        _donationHistoryIds.Clear();
        DonationHistoryError = string.Empty;
        NotifyDonationHistoryStateChanged();
    }

    private void NotifyDonationHistoryStateChanged()
    {
        OnPropertyChanged(nameof(HasDonationHistory));
        OnPropertyChanged(nameof(ShowDonationHistoryPanel));
        OnPropertyChanged(nameof(ShowDonationConnectPanel));
        OnPropertyChanged(nameof(ShowDonationHistoryEmptyState));
        OnPropertyChanged(nameof(HasDonationHistoryError));
        OnPropertyChanged(nameof(CanRefreshDonationHistory));
        OnPropertyChanged(nameof(DonationHistoryCountLabel));
        RefreshDonationHistoryCommand.NotifyCanExecuteChanged();
    }

    private async Task InitializeYouTubeAsync()
    {
        if (_youTubeAuthService is null || _youTubeAuthSessionStore is null || _youTubeLiveChatClient is null)
        {
            YouTubeStatus = Texts.YouTubeNotConnected;
            return;
        }
        try
        {
            _youTubeAuthService.Configure(YouTubeApplication.ClientId);
            var session = _youTubeAuthSessionStore.Load();
            if (session is null ||
                !string.Equals(session.ClientId, YouTubeApplication.ClientId, StringComparison.Ordinal))
            {
                YouTubeStatus = Texts.YouTubeNotConnected;
                return;
            }
            session = session.NeedsRefresh(TimeSpan.FromMinutes(2))
                ? await _youTubeAuthService.RefreshAsync(session, _lifetimeCancellation.Token).ConfigureAwait(true)
                : await _youTubeAuthService.ValidateAsync(session, _lifetimeCancellation.Token).ConfigureAwait(true);
            _youTubeAuthSessionStore.Save(session);
            ApplyYouTubeSession(session);
            await _youTubeLiveChatClient.StartAsync(session, _lifetimeCancellation.Token).ConfigureAwait(true);
            YouTubeStatus = Texts.YouTubeWaitingForBroadcast;
        }
        catch (OperationCanceledException cancellationException) when (_lifetimeCancellation.IsCancellationRequested || cancellationException is AuthenticationContextChangedException)
        {
        }
        catch (Exception exception)
        {
            YouTubeStatus = Texts.SignInFailed(GetYouTubeErrorMessage(exception));
        }
    }

    private string GetYouTubeErrorMessage(Exception exception)
    {
        var useEnglish = string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);
        return exception switch
        {
            YouTubeChannelUnavailableException channelUnavailable =>
                channelUnavailable.GetLocalizedMessage(useEnglish),
            YouTubeOAuthClientConfigurationException clientConfiguration =>
                clientConfiguration.GetLocalizedMessage(useEnglish),
            _ => AppDiagnostics.GetUserMessage(exception, "YouTube integration")
        };
    }

    private void ApplyYouTubeSession(YouTubeAuthSession session)
    {
        _youTubeAuthSession = session;
        YouTubeAccountName = string.IsNullOrWhiteSpace(session.ChannelTitle)
            ? session.ChannelHandle
            : session.ChannelTitle;
        OnPropertyChanged(nameof(IsYouTubeConnected));
        OnPropertyChanged(nameof(YouTubeAccountLabel));
        OnPropertyChanged(nameof(YouTubeChannelUrl));
        OnPropertyChanged(nameof(CanConnectYouTube));
        NotifyYouTubeModerationStateChanged();
        NotifyYouTubeAccountLayoutChanged();
        ConnectYouTubeCommand.NotifyCanExecuteChanged();
        EnableYouTubeModerationCommand.NotifyCanExecuteChanged();
        RebuildVisibleMessages();
    }

    private void NotifyYouTubeModerationStateChanged()
    {
        OnPropertyChanged(nameof(HasYouTubeModerationPermission));
        OnPropertyChanged(nameof(CanModerateYouTube));
        OnPropertyChanged(nameof(CanModerateAny));
        OnPropertyChanged(nameof(RequiresYouTubeModerationReconnect));
        OnPropertyChanged(nameof(CanConfirmModeration));
        DeleteMessageCommand.NotifyCanExecuteChanged();
        BanUserCommand.NotifyCanExecuteChanged();
        TimeoutTenMinutesCommand.NotifyCanExecuteChanged();
        CustomTimeoutCommand.NotifyCanExecuteChanged();
        RemovePunishmentCommand.NotifyCanExecuteChanged();
        ConfirmModerationCommand.NotifyCanExecuteChanged();
        EnableYouTubeModerationCommand.NotifyCanExecuteChanged();
    }

    private string GetYouTubeChannelKey() => _youTubeAuthSession is { ChannelId.Length: > 0 } session
        ? "youtube_" + session.ChannelId
        : string.Empty;

    private void NotifyYouTubeAccountLayoutChanged()
    {
        OnPropertyChanged(nameof(HasActiveChannel));
        OnPropertyChanged(nameof(ShowMessageList));
        OnPropertyChanged(nameof(ShowChatEmptyState));
        OnPropertyChanged(nameof(ShowWelcomeState));
        OnPropertyChanged(nameof(ShowReadOnlyComposerNotice));
        OnPropertyChanged(nameof(ReadOnlyComposerNotice));
        OnPropertyChanged(nameof(ShowCombinedMessageList));
        OnPropertyChanged(nameof(ShowSplitMessageLists));
        OnPropertyChanged(nameof(HasDualChatSources));
    }

    private void OnYouTubeStatusChanged(object? sender, ChatConnectionStatusEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            var liveChatId = eventArgs.State == ChatConnectionState.Connected
                ? _youTubeLiveChatClient?.CurrentLiveChatId ?? string.Empty
                : string.Empty;
            if (liveChatId.Length > 0 &&
                !string.Equals(liveChatId, _youTubeModerationLiveChatId, StringComparison.Ordinal))
            {
                _youTubeModerationLiveChatId = liveChatId;
                YouTubeBans.Clear();
                OnPropertyChanged(nameof(HasYouTubeBans));
            }
            IsYouTubeLiveConnected = eventArgs.State == ChatConnectionState.Connected;
            IsYouTubeConnecting = eventArgs.State is ChatConnectionState.Connecting or ChatConnectionState.Reconnecting;
            YouTubeBroadcastTitle = IsYouTubeLiveConnected
                ? _youTubeLiveChatClient?.BroadcastTitle ?? string.Empty
                : string.Empty;
            YouTubeStatus = eventArgs.State switch
            {
                ChatConnectionState.Connected => string.IsNullOrWhiteSpace(YouTubeBroadcastTitle)
                    ? Texts.YouTubeChatConnected
                    : Texts.YouTubeChatConnected + ": " + YouTubeBroadcastTitle,
                ChatConnectionState.Connecting => Texts.YouTubeConnecting,
                ChatConnectionState.Reconnecting => Texts.YouTubeWaitingForBroadcast,
                ChatConnectionState.Error => Texts.ConnectionError + ": " +
                                             AppDiagnostics.GetUserMessage(eventArgs.Detail),
                _ => IsYouTubeConnected ? Texts.YouTubeWaitingForBroadcast : Texts.YouTubeNotConnected
            };
            RebuildVisibleMessages();
            NotifyYouTubeModerationStateChanged();
        });

    private void OnYouTubeSessionUpdated(object? sender, YouTubeSessionEventArgs eventArgs)
    {
        _youTubeAuthSession = eventArgs.Session;
        Dispatcher.UIThread.Post(NotifyYouTubeModerationStateChanged);
        try
        {
            _youTubeAuthSessionStore?.Save(eventArgs.Session);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.Cryptography.CryptographicException)
        {
            Dispatcher.UIThread.Post(() =>
                YouTubeStatus = Texts.SettingsSaveFailed(AppDiagnostics.GetUserMessage(exception)));
        }
    }

    private void OnYouTubeMessageDeleted(object? sender, YouTubeMessageDeletedEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in Messages.Where(item =>
                         item.Message.IsYouTubeMessage &&
                         string.Equals(
                             string.IsNullOrWhiteSpace(item.Message.PlatformMessageId)
                                 ? item.Message.Id
                                 : item.Message.PlatformMessageId,
                             eventArgs.MessageId,
                             StringComparison.Ordinal)).ToArray())
            {
                item.MarkModerated(ChatMessageModerationState.Deleted);
            }
            MessagesChanged?.Invoke(this, EventArgs.Empty);
        });

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Twitch channel logins are canonically lowercase.")]
    private static string NormalizeChannel(string value) =>
        (value ?? string.Empty).Trim().TrimStart('#').ToLowerInvariant();

    private static bool IsValidChannelLogin(string value) =>
        value.Length is > 0 and <= 25 &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private sealed record SuspiciousMessageSample(string Text, DateTimeOffset Timestamp);
}
