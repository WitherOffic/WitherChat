namespace WitherChat.Core.Models;

public sealed record StreamEvent
{
    public required string Id { get; init; }
    public required string Platform { get; init; }
    public required string Channel { get; init; }
    public required string Kind { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string UserLogin { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string AmountDisplay { get; init; } = string.Empty;
    public long AmountMicros { get; init; }
    public string Currency { get; init; } = string.Empty;
    public int Count { get; init; }
    public string Detail { get; init; } = string.Empty;
    public Uri? ImageUri { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    public string UserLabel => string.IsNullOrWhiteSpace(DisplayName) ? UserLogin : DisplayName;
    public bool IsPaid => AmountMicros > 0 ||
                          Kind is StreamEventKinds.Cheer or StreamEventKinds.Donation or
                              StreamEventKinds.SuperChat or StreamEventKinds.SuperSticker;
}

public static class StreamEventKinds
{
    public const string Subscription = "subscription";
    public const string Resubscription = "resubscription";
    public const string GiftSubscription = "gift_subscription";
    public const string CommunityGift = "community_gift";
    public const string Membership = "membership";
    public const string MembershipGift = "membership_gift";
    public const string GiftMembershipReceived = "gift_membership_received";
    public const string Raid = "raid";
    public const string Cheer = "cheer";
    public const string ChannelPoints = "channel_points";
    public const string SuperChat = "super_chat";
    public const string SuperSticker = "super_sticker";
    public const string Poll = "poll";
    public const string Donation = "donation";
    public const string Announcement = "announcement";
    public const string Charity = "charity";
    public const string Other = "other";
}

public sealed class StreamEventEventArgs(StreamEvent value) : EventArgs
{
    public StreamEvent Value { get; } = value;
}
