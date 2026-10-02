using System.Globalization;

namespace WitherChat.Core.Models;

public sealed record ChatMessage
{
    public required string Id { get; init; }
    public string PlatformMessageId { get; init; } = string.Empty;
    public required string Channel { get; init; }
    public string BroadcasterId { get; init; } = string.Empty;
    public required string UserLogin { get; init; }
    public string UserId { get; init; } = string.Empty;
    public required string DisplayName { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string UserColor { get; init; } = string.Empty;
    public IReadOnlyList<ChatBadge> Badges { get; init; } = [];
    public IReadOnlyList<ChatMessagePart> Parts { get; init; } = [];
    public string ReplyParentDisplayName { get; init; } = string.Empty;
    public string ReplyParentText { get; init; } = string.Empty;
    public bool IsAction { get; init; }
    public bool IsPinned { get; init; }
    public string CustomRewardId { get; init; } = string.Empty;
    public string RewardTitle { get; init; } = string.Empty;
    public int? RewardCost { get; init; }
    public string RewardPrompt { get; init; } = string.Empty;
    public string SourceBroadcasterId { get; init; } = string.Empty;
    public string SourceChannelLogin { get; init; } = string.Empty;
    public string SourceChannelDisplayName { get; init; } = string.Empty;
    public string Platform { get; init; } = ChatPlatforms.Twitch;
    public Uri? UserProfileImageUri { get; init; }
    public StreamEvent? StreamEvent { get; init; }
    public bool IsFirstMessage { get; init; }
    public bool IsSuspicious { get; init; }

    public string UserLabel => string.IsNullOrWhiteSpace(DisplayName) ? UserLogin : DisplayName;
    public string TimeText => Timestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string AvatarInitial => string.IsNullOrWhiteSpace(UserLabel)
        ? "?"
        : UserLabel.Trim()[0].ToString().ToUpperInvariant();
    public bool HasReply => !string.IsNullOrWhiteSpace(ReplyParentDisplayName) ||
                            !string.IsNullOrWhiteSpace(ReplyParentText);
    public bool IsLong => Text.Length > 320 || Text.Count(character => character == '\n') >= 5;
    public bool IsChannelPointRedemption => CustomRewardId.Length > 0;
    public bool IsSharedChatMessage => SourceBroadcasterId.Length > 0;
    public bool IsYouTubeMessage => string.Equals(Platform, ChatPlatforms.YouTube, StringComparison.OrdinalIgnoreCase);
    public bool IsSystemEvent => StreamEvent is not null;
    public bool IsPaidEvent => StreamEvent?.IsPaid == true;
}

public static class ChatPlatforms
{
    public const string Twitch = "Twitch";
    public const string YouTube = "YouTube";
}
