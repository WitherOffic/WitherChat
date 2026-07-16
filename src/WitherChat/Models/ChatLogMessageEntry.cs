using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace WitherChat.Models;

public sealed class ChatLogMessageEntry
{
    private static readonly Brush DefaultNameBrush = CreateDefaultNameBrush();

    public DateTimeOffset TimestampLocal { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string ChannelLogin { get; set; } = string.Empty;
    public string BroadcasterId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string SourceRoomId { get; set; } = string.Empty;
    public string StreamTitle { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserLogin { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string UserColor { get; set; } = string.Empty;
    public List<ChatLogBadgeEntry> Badges { get; set; } = [];
    public string Message { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string RelatedMessageId { get; set; } = string.Empty;
    public string ReplyParentMessageId { get; set; } = string.Empty;
    public string ReplyParentUserId { get; set; } = string.Empty;
    public string ReplyParentUserLogin { get; set; } = string.Empty;
    public string ReplyParentDisplayName { get; set; } = string.Empty;
    public string ReplyParentMessageBody { get; set; } = string.Empty;
    public ModerationMessageState ModerationState { get; set; }
    public DateTimeOffset? ModeratedAt { get; set; }
    public string ModeratorId { get; set; } = string.Empty;
    public string ModeratorName { get; set; } = string.Empty;
    public string ModerationReason { get; set; } = string.Empty;
    public ChatMessageKind Kind { get; set; }
    public string RedemptionId { get; set; } = string.Empty;
    public string RewardId { get; set; } = string.Empty;
    public string RewardTitle { get; set; } = string.Empty;
    public int? RewardCost { get; set; }
    public string RewardPrompt { get; set; } = string.Empty;
    public string RewardUserInput { get; set; } = string.Empty;
    public string RewardType { get; set; } = string.Empty;
    public DateTimeOffset? RedeemedAt { get; set; }
    public bool IsModerator { get; set; }
    public bool IsSubscriber { get; set; }
    public bool IsBroadcaster { get; set; }
    public bool IsVip { get; set; }

    [JsonIgnore]
    public string TimeText => TimestampLocal.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    [JsonIgnore]
    public string UserLabel => (string.IsNullOrWhiteSpace(DisplayName) ? UserLogin : DisplayName) ?? string.Empty;

    [JsonIgnore]
    public string BadgeText => Badges is not { Count: > 0 }
        ? string.Empty
        : string.Join(" ", Badges.Select(b => b.DisplayText));

    [JsonIgnore]
    public bool IsChannelPointsRedemption => Kind == ChatMessageKind.ChannelPointsRedemption;

    [JsonIgnore]
    public bool HasRewardUserInput => !string.IsNullOrWhiteSpace(RewardUserInput);

    [JsonIgnore]
    public string RedemptionDetail => string.IsNullOrWhiteSpace(RewardUserInput) ? Message : RewardUserInput;

    [JsonIgnore]
    public bool HasRedemptionDetail => !string.IsNullOrWhiteSpace(RedemptionDetail);

    [JsonIgnore]
    public string RewardCostText => RewardCost?.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty;

    [JsonIgnore]
    public string RedemptionSummary => string.IsNullOrWhiteSpace(RewardCostText)
        ? $"{UserLabel} · {RewardTitle}"
        : $"{UserLabel} · {RewardTitle} · {RewardCostText}";

    [JsonIgnore]
    public Brush NickBrush
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(UserColor))
            {
                try
                {
                    return (Brush)new BrushConverter().ConvertFromString(UserColor)!;
                }
                catch
                {
                    return DefaultNameBrush;
                }
            }

            return DefaultNameBrush;
        }
    }

    public bool MatchesRole(string role)
    {
        return role switch
        {
            "broadcaster" => IsBroadcaster,
            "moderator" => IsModerator,
            "subscriber" => IsSubscriber,
            "vip" => IsVip,
            _ => true
        };
    }

    private static Brush CreateDefaultNameBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(180, 180, 255));
        brush.Freeze();
        return brush;
    }
}
