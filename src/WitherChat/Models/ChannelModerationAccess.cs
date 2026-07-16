namespace WitherChat.Models;

public sealed record ChannelModerationAccess(
    bool IsBroadcaster,
    bool IsModerator,
    bool CanModerate,
    string FailureReason = "");
