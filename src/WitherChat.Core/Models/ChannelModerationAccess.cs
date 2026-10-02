namespace WitherChat.Core.Models;

public sealed record ChannelModerationAccess(
    bool IsBroadcaster,
    bool IsModerator,
    bool IsConfirmed,
    string FailureReason = "")
{
    public bool CanModerate => IsConfirmed && (IsBroadcaster || IsModerator);
}
