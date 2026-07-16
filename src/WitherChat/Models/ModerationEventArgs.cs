namespace WitherChat.Models;

public sealed class ChatMessageDeletedEventArgs(
    string broadcasterId,
    string messageId,
    string targetUserId) : EventArgs
{
    public string BroadcasterId { get; } = broadcasterId;
    public string MessageId { get; } = messageId;
    public string TargetUserId { get; } = targetUserId;
}

public sealed class UserMessagesClearedEventArgs(
    string broadcasterId,
    string targetUserId) : EventArgs
{
    public string BroadcasterId { get; } = broadcasterId;
    public string TargetUserId { get; } = targetUserId;
}

public sealed class ChannelUserBannedEventArgs(
    string broadcasterId,
    string targetUserId,
    string targetUserLogin,
    string targetUserName,
    string moderatorUserId,
    string moderatorUserName,
    string reason,
    DateTimeOffset startedAt,
    DateTimeOffset? endsAt,
    bool isPermanent) : EventArgs
{
    public string BroadcasterId { get; } = broadcasterId;
    public string TargetUserId { get; } = targetUserId;
    public string TargetUserLogin { get; } = targetUserLogin;
    public string TargetUserName { get; } = targetUserName;
    public string ModeratorUserId { get; } = moderatorUserId;
    public string ModeratorUserName { get; } = moderatorUserName;
    public string Reason { get; } = reason;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public DateTimeOffset? EndsAt { get; } = endsAt;
    public bool IsPermanent { get; } = isPermanent;
}

public sealed class ChannelUserUnbannedEventArgs(
    string broadcasterId,
    string targetUserId,
    string targetUserLogin,
    string targetUserName,
    string moderatorUserId,
    string moderatorUserName) : EventArgs
{
    public string BroadcasterId { get; } = broadcasterId;
    public string TargetUserId { get; } = targetUserId;
    public string TargetUserLogin { get; } = targetUserLogin;
    public string TargetUserName { get; } = targetUserName;
    public string ModeratorUserId { get; } = moderatorUserId;
    public string ModeratorUserName { get; } = moderatorUserName;
}

public sealed class AutoModMessageUpdatedEventArgs(
    string broadcasterId,
    string messageId,
    HeldAutoModStatus status) : EventArgs
{
    public string BroadcasterId { get; } = broadcasterId;
    public string MessageId { get; } = messageId;
    public HeldAutoModStatus Status { get; } = status;
}
