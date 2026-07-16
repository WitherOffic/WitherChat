using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using WitherChat.ViewModels;
using WitherChat.Services;

namespace WitherChat.Models;

public sealed class ChatMessageModel : ObservableObject
{
    private const int LongMessageCharacterThreshold = 320;
    private const int LongMessagePartThreshold = 32;
    private const int LongMessageLineThreshold = 5;
    private static readonly Brush DefaultNameBrush = CreateDefaultNameBrush();
    private Brush _nickBrush = DefaultNameBrush;
    private string _profileImageUrl = string.Empty;
    private ChatMessageKind _kind;
    private string _redemptionId = string.Empty;
    private string _rewardId = string.Empty;
    private string _rewardTitle = string.Empty;
    private int? _rewardCost;
    private string _rewardUserInput = string.Empty;
    private string _rewardType = string.Empty;
    private string _redemptionSummary = string.Empty;
    private string _rewardUserInputLabel = string.Empty;
    private string _messageType = "text";
    private string _customRewardId = string.Empty;
    private bool _isChannelPointsMessage;
    private bool _channelPointsDetailsAvailable;
    private ModerationMessageState _moderationState;
    private string _moderationReason = string.Empty;
    private string _moderatedByUserId = string.Empty;
    private string _moderatedByDisplayName = string.Empty;
    private DateTimeOffset? _moderatedAt;
    private string _color = string.Empty;
    private string _sourceChannelLogin = string.Empty;
    private string _sourceChannelDisplayName = string.Empty;
    private bool _isPinned;
    private bool _isLongMessage;
    private bool _isMessageExpanded;
    private IReadOnlyList<ChatMessagePartModel>? _originalParts;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string MessageId { get; init; } = string.Empty;
    public string RelatedMessageId { get; init; } = string.Empty;
    public string ReplyParentMessageId { get; init; } = string.Empty;
    public string ReplyParentUserId { get; init; } = string.Empty;
    public string ReplyParentUserLogin { get; init; } = string.Empty;
    public string ReplyParentDisplayName { get; init; } = string.Empty;
    public string ReplyParentMessageBody { get; init; } = string.Empty;
    public string MessageType { get => _messageType; set => SetProperty(ref _messageType, value); }
    public ChatMessageKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsChannelPointsRedemption));
            }
        }
    }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string UserId { get; init; } = string.Empty;
    public string ChannelLogin { get; set; } = string.Empty;
    public string RoomId { get; init; } = string.Empty;
    public string SourceRoomId { get; init; } = string.Empty;
    public string SourceBroadcasterId { get; init; } = string.Empty;
    public string SourceChannelLogin
    {
        get => _sourceChannelLogin;
        set
        {
            if (SetProperty(ref _sourceChannelLogin, value))
            {
                OnPropertyChanged(nameof(SourceChannelLabel));
                OnPropertyChanged(nameof(HasSourceChannelLabel));
                OnPropertyChanged(nameof(IsSharedChatMessage));
            }
        }
    }
    public string SourceChannelDisplayName
    {
        get => _sourceChannelDisplayName;
        set
        {
            if (SetProperty(ref _sourceChannelDisplayName, value))
            {
                OnPropertyChanged(nameof(SourceChannelLabel));
                OnPropertyChanged(nameof(HasSourceChannelLabel));
                OnPropertyChanged(nameof(IsSharedChatMessage));
            }
        }
    }
    public string BadgeBroadcasterId { get; init; } = string.Empty;
    public string Login { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<ChatMessagePartModel> OriginalParts =>
        _originalParts ??= Parts.Count > 0
            ? Parts.ToArray()
            : [ChatMessagePartModel.TextPart(Text)];
    public string Color
    {
        get => _color;
        set
        {
            if (SetProperty(ref _color, value))
            {
                _nickBrush = CreateNameBrush(value);
                OnPropertyChanged(nameof(NickBrush));
            }
        }
    }
    public bool IsLocalEcho { get; init; }
    public bool IsPinned { get => _isPinned; set => SetProperty(ref _isPinned, value); }
    public bool IsLongMessage => _isLongMessage;
    public bool IsMessageExpanded
    {
        get => _isMessageExpanded;
        set
        {
            if (_isLongMessage)
            {
                SetProperty(ref _isMessageExpanded, value);
            }
        }
    }
    public ObservableCollection<BadgeModel> Badges { get; init; } = new();
    public ObservableCollection<ChatMessagePartModel> Parts { get; init; } = new();

    internal void ReplaceOriginalParts(IEnumerable<ChatMessagePartModel> parts)
    {
        _originalParts = parts.ToArray();
    }

    internal int PresentationVersion { get; set; }

    internal void RefreshLongMessageState()
    {
        var renderedCharacterCount = 0;
        var renderedLineBreakCount = 0;
        foreach (var part in Parts)
        {
            renderedCharacterCount += part.Text.Length;
            renderedLineBreakCount += part.Text.Count(character => character == '\n');
        }

        var logicalText = Text ?? string.Empty;
        var isLong = Math.Max(logicalText.Length, renderedCharacterCount) > LongMessageCharacterThreshold ||
                     Math.Max(
                         logicalText.Count(character => character == '\n'),
                         renderedLineBreakCount) >= LongMessageLineThreshold ||
                     Parts.Count > LongMessagePartThreshold;
        if (_isLongMessage == isLong)
        {
            return;
        }

        _isLongMessage = isLong;
        OnPropertyChanged(nameof(IsLongMessage));
        if (!isLong && _isMessageExpanded)
        {
            _isMessageExpanded = false;
            OnPropertyChanged(nameof(IsMessageExpanded));
        }
    }

    public string UserLabel => string.IsNullOrWhiteSpace(DisplayName) ? Login : DisplayName;
    public bool IsSharedChatMessage =>
        (!string.IsNullOrWhiteSpace(SourceBroadcasterId) &&
         !string.Equals(SourceBroadcasterId, BroadcasterId, StringComparison.Ordinal)) ||
        (!string.IsNullOrWhiteSpace(SourceRoomId) &&
         !string.Equals(SourceRoomId, RoomId, StringComparison.Ordinal));
    public string SourceChannelLabel
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(SourceChannelDisplayName)
                ? SourceChannelLogin
                : SourceChannelDisplayName;
            return string.IsNullOrWhiteSpace(label) ? string.Empty : "@" + label.TrimStart('@');
        }
    }
    public bool HasSourceChannelLabel => IsSharedChatMessage && !string.IsNullOrWhiteSpace(SourceChannelLabel);
    public string LoginLabel => string.IsNullOrWhiteSpace(Login) ? string.Empty : "@" + Login;
    public bool HasReply => !string.IsNullOrWhiteSpace(ReplyParentMessageId) ||
                            !string.IsNullOrWhiteSpace(ReplyParentUserLogin) ||
                            !string.IsNullOrWhiteSpace(ReplyParentDisplayName);
    public bool HasReplyParentMessageBody => !string.IsNullOrWhiteSpace(ReplyParentMessageBody);
    public string ReplyParentUserLabel
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(ReplyParentDisplayName)
                ? ReplyParentUserLogin
                : ReplyParentDisplayName;
            return string.IsNullOrWhiteSpace(label) ? string.Empty : "@" + label.TrimStart('@');
        }
    }
    public string AvatarInitial => string.IsNullOrWhiteSpace(UserLabel)
        ? "?"
        : UserLabel.Trim()[0].ToString().ToUpperInvariant();
    public string TimeText => Timestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public bool IsModerator => HasBadge("moderator") || HasBadge("broadcaster");
    public bool IsVip => HasBadge("vip");
    public bool IsSubscriber => HasBadge("subscriber") || HasBadge("founder");
    public string ProfileImageUrl
    {
        get => _profileImageUrl;
        set
        {
            if (SetProperty(ref _profileImageUrl, value))
            {
                OnPropertyChanged(nameof(HasProfileImage));
            }
        }
    }
    public bool HasProfileImage => !string.IsNullOrWhiteSpace(ProfileImageUrl);
    public string RedemptionId { get => _redemptionId; set => SetProperty(ref _redemptionId, value); }
    public string BroadcasterId { get; init; } = string.Empty;
    public string ChannelDisplayName { get; init; } = string.Empty;
    public string RedemptionStatus { get; init; } = string.Empty;
    public string CustomRewardId { get => _customRewardId; set => SetProperty(ref _customRewardId, value); }
    public string RewardId { get => _rewardId; set => SetProperty(ref _rewardId, value); }
    public string RewardTitle { get => _rewardTitle; set => SetProperty(ref _rewardTitle, value); }
    public int? RewardCost { get => _rewardCost; set => SetProperty(ref _rewardCost, value); }
    public string RewardPrompt { get; init; } = string.Empty;
    public string RewardUserInput
    {
        get => _rewardUserInput;
        set
        {
            if (SetProperty(ref _rewardUserInput, value))
            {
                OnPropertyChanged(nameof(HasRewardUserInput));
            }
        }
    }
    public string RewardType { get => _rewardType; set => SetProperty(ref _rewardType, value); }
    public DateTimeOffset? RedeemedAt { get; init; }
    public string RedemptionSummary { get => _redemptionSummary; set => SetProperty(ref _redemptionSummary, value); }
    public string RewardUserInputLabel { get => _rewardUserInputLabel; set => SetProperty(ref _rewardUserInputLabel, value); }
    public bool IsChannelPointsRedemption => Kind == ChatMessageKind.ChannelPointsRedemption;
    public bool IsChannelPointsMessage { get => _isChannelPointsMessage; set => SetProperty(ref _isChannelPointsMessage, value); }
    public bool ChannelPointsDetailsAvailable { get => _channelPointsDetailsAvailable; set => SetProperty(ref _channelPointsDetailsAvailable, value); }
    public bool HasRewardUserInput => !string.IsNullOrWhiteSpace(RewardUserInput);
    public ModerationMessageState ModerationState
    {
        get => _moderationState;
        set
        {
            if (SetProperty(ref _moderationState, value))
            {
                OnPropertyChanged(nameof(IsModerated));
                OnPropertyChanged(nameof(IsDeleted));
                OnPropertyChanged(nameof(IsRemovedByTimeout));
                OnPropertyChanged(nameof(IsRemovedByBan));
                OnPropertyChanged(nameof(ModerationStatus));
                OnPropertyChanged(nameof(ModeratedContentOpacity));
            }
        }
    }
    public bool IsModerated => ModerationState != ModerationMessageState.None;
    public bool IsDeleted => ModerationState == ModerationMessageState.Deleted;
    public bool IsRemovedByTimeout => ModerationState == ModerationMessageState.TimedOut;
    public bool IsRemovedByBan => ModerationState == ModerationMessageState.Banned;
    public string ModerationReason { get => _moderationReason; set => SetProperty(ref _moderationReason, value); }
    public string ModeratedByUserId { get => _moderatedByUserId; set => SetProperty(ref _moderatedByUserId, value); }
    public string ModeratedByDisplayName
    {
        get => _moderatedByDisplayName;
        set
        {
            if (SetProperty(ref _moderatedByDisplayName, value))
            {
                OnPropertyChanged(nameof(ModerationStatus));
            }
        }
    }
    public DateTimeOffset? ModeratedAt { get => _moderatedAt; set => SetProperty(ref _moderatedAt, value); }
    public double ModeratedContentOpacity => IsModerated ? 0.48 : 1.0;
    public string ModerationStatus
    {
        get
        {
            var key = ModerationState switch
            {
                ModerationMessageState.Deleted => "MessageDeleted",
                ModerationMessageState.TimedOut => "UserTimedOut",
                ModerationMessageState.Banned => "UserBanned",
                ModerationMessageState.AutoModDenied => "MessageDenied",
                ModerationMessageState.RemovedByModeration => "RemovedByModerator",
                _ => string.Empty
            };
            var status = string.IsNullOrEmpty(key)
                ? string.Empty
                : LocalizationService.Get(LocalizationService.CurrentLanguage, key);
            if (!string.IsNullOrWhiteSpace(ModeratedByDisplayName))
            {
                status += " · " + LocalizationService.Get(LocalizationService.CurrentLanguage, "RemovedByModerator") + ": " + ModeratedByDisplayName;
            }
            return status;
        }
    }

    public void MarkModerated(
        ModerationMessageState state,
        string? reason = null,
        string? moderatorUserId = null,
        string? moderatorDisplayName = null)
    {
        if (state == ModerationMessageState.None)
        {
            return;
        }

        if (ModerationState == state)
        {
            if (string.IsNullOrWhiteSpace(ModerationReason) && !string.IsNullOrWhiteSpace(reason))
            {
                ModerationReason = reason;
            }
            if (string.IsNullOrWhiteSpace(ModeratedByUserId) && !string.IsNullOrWhiteSpace(moderatorUserId))
            {
                ModeratedByUserId = moderatorUserId;
            }
            if (string.IsNullOrWhiteSpace(ModeratedByDisplayName) && !string.IsNullOrWhiteSpace(moderatorDisplayName))
            {
                ModeratedByDisplayName = moderatorDisplayName;
            }
            ModeratedAt ??= DateTimeOffset.UtcNow;
            return;
        }

        ModerationReason = reason ?? string.Empty;
        ModeratedByUserId = moderatorUserId ?? string.Empty;
        ModeratedByDisplayName = moderatorDisplayName ?? string.Empty;
        ModeratedAt = DateTimeOffset.UtcNow;
        ModerationState = state;
    }

    public void RefreshModerationText() => OnPropertyChanged(nameof(ModerationStatus));

    public Brush NickBrush => _nickBrush;

    private bool HasBadge(string setId)
    {
        return Badges.Any(b => string.Equals(b.SetId, setId, StringComparison.OrdinalIgnoreCase));
    }

    private static Brush CreateDefaultNameBrush()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 180, 255));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateNameBrush(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultNameBrush;
        }

        try
        {
            var color = (System.Windows.Media.Color)ColorConverter.ConvertFromString(value)!;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return DefaultNameBrush;
        }
        catch (NotSupportedException)
        {
            return DefaultNameBrush;
        }
    }
}
