using System.Collections.ObjectModel;
using WitherChat.Models;

namespace WitherChat.ViewModels;

public sealed class ChannelSessionViewModel : ObservableObject
{
    private string _broadcasterId = string.Empty;
    private string _displayName;
    private string _profileImageUrl = string.Empty;
    private bool _isLive;
    private int _viewerCount;
    private string _streamTitle = string.Empty;
    private string _gameName = string.Empty;
    private DateTimeOffset? _streamStartedAt;
    private bool _hasAuthoritativeStreamStatus;
    private bool _isConnected;
    private bool _isConnecting;
    private string _connectionStatus = string.Empty;
    private string _connectionError = string.Empty;
    private DateTimeOffset? _lastConnectedAt;
    private bool _isActive;
    private int _unreadCount;
    private DateTimeOffset _lastActivityAt;
    private bool _autoScroll = true;
    private double _savedVerticalOffset;
    private int _newMessagesBelowCount;
    private bool _canSend;
    private bool _hasSendRestriction;
    private string _sendRestrictionText = string.Empty;
    private DateTimeOffset? _sendRestrictionEndsAt;
    private int _sendRestrictionGeneration;
    private bool _canModerate;
    private bool _isModerator;
    private bool _isBroadcaster;
    private string _moderationStatus = string.Empty;
    private bool _moderationCheckCompleted;
    private string _moderationCheckError = string.Empty;
    private int _badgeCatalogGeneration;
    private bool _channelPointsDetailsAvailable;
    private bool _isBannedUsersLoading;
    private string _bannedUsersStatus = string.Empty;
    private string _bannedUsersCursor = string.Empty;
    private string _bannedUsersLoadError = string.Empty;
    private BannedUsersCapability _bannedUsersCapability;
    private bool _hasLoadedBannedUsers;
    private bool _hasCachedBannedUsers;
    private bool _isBannedUsersDataStale;
    private bool _requiresModerationReauthentication;
    private DateTimeOffset? _lastBannedUsersRefreshAt;
    private bool _isUnbanRequestsLoading;
    private string _unbanRequestsStatus = string.Empty;
    private string _unbanRequestsCursor = string.Empty;
    private UnbanRequestStatus _unbanRequestFilter = UnbanRequestStatus.Pending;
    private int _unreadUnbanRequests;
    private bool _isSharedChatActive;
    private string _sharedChatSessionId = string.Empty;
    private string _sharedChatHostLogin = string.Empty;
    private int _sharedChatParticipantCount;
    private PinnedChatMessageModel? _pinnedMessage;

    public ChannelSessionViewModel(string channelLogin)
    {
        ChannelLogin = channelLogin;
        _displayName = channelLogin;
    }

    public string ChannelLogin { get; }
    public LiveMessageCollection<ChatMessageModel> Messages { get; } = [];
    public Queue<ChatMessageModel> PendingVisualMessages { get; } = new();
    public ObservableCollection<HeldAutoModMessage> PendingAutoModMessages { get; } = [];
    public ObservableCollection<BannedUserEntry> BannedUsers { get; } = [];
    public ObservableCollection<UnbanRequestEntry> UnbanRequests { get; } = [];
    public Dictionary<string, ActivePunishmentState> ActivePunishments { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> UserColors { get; } = new(StringComparer.Ordinal);
    public Queue<string> UserColorOrder { get; } = new();
    public string BroadcasterId { get => _broadcasterId; set { if (SetProperty(ref _broadcasterId, value)) OnPropertyChanged(nameof(CanRefreshBannedUsers)); } }
    public string DisplayName { get => _displayName; set { if (SetProperty(ref _displayName, value)) OnPropertyChanged(nameof(AvatarInitial)); } }
    public string ProfileImageUrl { get => _profileImageUrl; set { if (SetProperty(ref _profileImageUrl, value)) OnPropertyChanged(nameof(HasProfileImage)); } }
    public bool HasProfileImage => !string.IsNullOrWhiteSpace(ProfileImageUrl);
    public string AvatarInitial => string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName.Trim()[0].ToString().ToUpperInvariant();
    public bool IsLive { get => _isLive; set => SetProperty(ref _isLive, value); }
    public int ViewerCount { get => _viewerCount; set => SetProperty(ref _viewerCount, value); }
    public string StreamTitle { get => _streamTitle; set => SetProperty(ref _streamTitle, value); }
    public string GameName { get => _gameName; set => SetProperty(ref _gameName, value); }
    public DateTimeOffset? StreamStartedAt { get => _streamStartedAt; set => SetProperty(ref _streamStartedAt, value); }
    public bool HasAuthoritativeStreamStatus { get => _hasAuthoritativeStreamStatus; set => SetProperty(ref _hasAuthoritativeStreamStatus, value); }
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(ShowConnectionStatus));
            }
        }
    }
    public bool IsConnecting { get => _isConnecting; set => SetProperty(ref _isConnecting, value); }
    public string ConnectionStatus { get => _connectionStatus; set => SetProperty(ref _connectionStatus, value); }
    public bool ShowConnectionStatus => !IsConnected;
    public string ConnectionError { get => _connectionError; set => SetProperty(ref _connectionError, value); }
    public DateTimeOffset? LastConnectedAt { get => _lastConnectedAt; set => SetProperty(ref _lastConnectedAt, value); }
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
    public int UnreadCount { get => _unreadCount; set { if (SetProperty(ref _unreadCount, value)) OnPropertyChanged(nameof(HasUnread)); } }
    public bool HasUnread => UnreadCount > 0;
    public DateTimeOffset LastActivityAt { get => _lastActivityAt; set => SetProperty(ref _lastActivityAt, value); }
    public bool AutoScroll { get => _autoScroll; set => SetProperty(ref _autoScroll, value); }
    public double SavedVerticalOffset { get => _savedVerticalOffset; set => SetProperty(ref _savedVerticalOffset, value); }
    public int NewMessagesBelowCount { get => _newMessagesBelowCount; set => SetProperty(ref _newMessagesBelowCount, value); }
    private bool _isPrimaryAccountChannel;
    public bool IsPrimaryAccountChannel
    {
        get => _isPrimaryAccountChannel;
        set
        {
            if (SetProperty(ref _isPrimaryAccountChannel, value))
            {
                OnPropertyChanged(nameof(CanRemove));
                OnPropertyChanged(nameof(ShowModerationStatus));
            }
        }
    }
    public bool CanRemove => !IsPrimaryAccountChannel;
    public bool CanSend { get => _canSend; set => SetProperty(ref _canSend, value); }
    public bool HasSendRestriction { get => _hasSendRestriction; set => SetProperty(ref _hasSendRestriction, value); }
    public string SendRestrictionText { get => _sendRestrictionText; set => SetProperty(ref _sendRestrictionText, value); }
    public DateTimeOffset? SendRestrictionEndsAt { get => _sendRestrictionEndsAt; set => SetProperty(ref _sendRestrictionEndsAt, value); }
    public PunishmentType? SendRestrictionType { get; set; }
    public int SendRestrictionGeneration { get => _sendRestrictionGeneration; set => SetProperty(ref _sendRestrictionGeneration, value); }
    public bool CanModerate
    {
        get => _canModerate;
        set
        {
            if (SetProperty(ref _canModerate, value))
            {
                OnPropertyChanged(nameof(CanRefreshBannedUsers));
                OnPropertyChanged(nameof(HasConfirmedModerationAccess));
            }
        }
    }
    public bool IsModerator
    {
        get => _isModerator;
        set
        {
            if (SetProperty(ref _isModerator, value))
            {
                OnPropertyChanged(nameof(CanRefreshBannedUsers));
                OnPropertyChanged(nameof(HasConfirmedModerationAccess));
            }
        }
    }
    public bool IsBroadcaster
    {
        get => _isBroadcaster;
        set
        {
            if (SetProperty(ref _isBroadcaster, value))
            {
                OnPropertyChanged(nameof(CanRefreshBannedUsers));
                OnPropertyChanged(nameof(HasConfirmedModerationAccess));
            }
        }
    }
    public string ModerationStatus
    {
        get => _moderationStatus;
        set
        {
            if (SetProperty(ref _moderationStatus, value))
            {
                OnPropertyChanged(nameof(ShowModerationStatus));
            }
        }
    }
    public bool ShowModerationStatus => !IsPrimaryAccountChannel && !string.IsNullOrWhiteSpace(ModerationStatus);
    public bool ModerationCheckCompleted
    {
        get => _moderationCheckCompleted;
        set
        {
            if (SetProperty(ref _moderationCheckCompleted, value))
            {
                OnPropertyChanged(nameof(CanRefreshBannedUsers));
                OnPropertyChanged(nameof(HasConfirmedModerationAccess));
            }
        }
    }
    public bool HasConfirmedModerationAccess =>
        ModerationCheckCompleted && CanModerate && (IsBroadcaster || IsModerator);
    public string ModerationCheckError { get => _moderationCheckError; set => SetProperty(ref _moderationCheckError, value); }
    public bool ChannelPointsDetailsAvailable { get => _channelPointsDetailsAvailable; set => SetProperty(ref _channelPointsDetailsAvailable, value); }
    public int BadgeCatalogGeneration { get => _badgeCatalogGeneration; set => SetProperty(ref _badgeCatalogGeneration, value); }
    public bool IsBannedUsersLoading { get => _isBannedUsersLoading; set { if (SetProperty(ref _isBannedUsersLoading, value)) OnPropertyChanged(nameof(CanRefreshBannedUsers)); } }
    public string BannedUsersStatus { get => _bannedUsersStatus; set => SetProperty(ref _bannedUsersStatus, value); }
    public string BannedUsersCursor { get => _bannedUsersCursor; set => SetProperty(ref _bannedUsersCursor, value); }
    public string BannedUsersLoadError { get => _bannedUsersLoadError; set => SetProperty(ref _bannedUsersLoadError, value); }
    public BannedUsersCapability BannedUsersCapability { get => _bannedUsersCapability; set => SetProperty(ref _bannedUsersCapability, value); }
    public bool HasLoadedBannedUsers { get => _hasLoadedBannedUsers; set => SetProperty(ref _hasLoadedBannedUsers, value); }
    public bool HasCachedBannedUsers { get => _hasCachedBannedUsers; set => SetProperty(ref _hasCachedBannedUsers, value); }
    public bool IsBannedUsersDataStale { get => _isBannedUsersDataStale; set => SetProperty(ref _isBannedUsersDataStale, value); }
    public bool RequiresModerationReauthentication { get => _requiresModerationReauthentication; set => SetProperty(ref _requiresModerationReauthentication, value); }
    public DateTimeOffset? LastBannedUsersRefreshAt { get => _lastBannedUsersRefreshAt; set => SetProperty(ref _lastBannedUsersRefreshAt, value); }
    public bool CanRefreshBannedUsers =>
        HasConfirmedModerationAccess && IsBroadcaster && !IsBannedUsersLoading && !string.IsNullOrWhiteSpace(BroadcasterId);
    public bool IsUnbanRequestsLoading { get => _isUnbanRequestsLoading; set => SetProperty(ref _isUnbanRequestsLoading, value); }
    public string UnbanRequestsStatus { get => _unbanRequestsStatus; set => SetProperty(ref _unbanRequestsStatus, value); }
    public string UnbanRequestsCursor { get => _unbanRequestsCursor; set => SetProperty(ref _unbanRequestsCursor, value); }
    public UnbanRequestStatus UnbanRequestFilter { get => _unbanRequestFilter; set => SetProperty(ref _unbanRequestFilter, value); }
    public int UnreadUnbanRequests { get => _unreadUnbanRequests; set => SetProperty(ref _unreadUnbanRequests, value); }
    public bool IsSharedChatActive { get => _isSharedChatActive; set => SetProperty(ref _isSharedChatActive, value); }
    public string SharedChatSessionId { get => _sharedChatSessionId; set => SetProperty(ref _sharedChatSessionId, value); }
    public string SharedChatHostLogin { get => _sharedChatHostLogin; set => SetProperty(ref _sharedChatHostLogin, value); }
    public int SharedChatParticipantCount { get => _sharedChatParticipantCount; set => SetProperty(ref _sharedChatParticipantCount, value); }
    public PinnedChatMessageModel? PinnedMessage
    {
        get => _pinnedMessage;
        set
        {
            if (SetProperty(ref _pinnedMessage, value))
            {
                OnPropertyChanged(nameof(HasPinnedMessage));
            }
        }
    }
    public bool HasPinnedMessage => PinnedMessage is { } message &&
                                    (message.EndsAt is null || message.EndsAt > DateTimeOffset.UtcNow);
}
