namespace WitherChat.Models;

public sealed class ChannelUserModeratedEventArgs(
    string channelLogin,
    string roomId,
    string targetUserId,
    string targetLogin,
    PunishmentType punishmentType,
    int? durationSeconds,
    DateTimeOffset observedAt) : EventArgs
{
    public string ChannelLogin { get; } = channelLogin;
    public string RoomId { get; } = roomId;
    public string TargetUserId { get; } = targetUserId;
    public string TargetLogin { get; } = targetLogin;
    public PunishmentType PunishmentType { get; } = punishmentType;
    public int? DurationSeconds { get; } = durationSeconds;
    public DateTimeOffset ObservedAt { get; } = observedAt;
}

public sealed class ChannelMessageDeletedEventArgs(
    string channelLogin,
    string roomId,
    string targetMessageId,
    string targetLogin,
    DateTimeOffset deletedAt) : EventArgs
{
    public string ChannelLogin { get; } = channelLogin;
    public string RoomId { get; } = roomId;
    public string TargetMessageId { get; } = targetMessageId;
    public string TargetLogin { get; } = targetLogin;
    public DateTimeOffset DeletedAt { get; } = deletedAt;
}

public sealed class ChannelChatClearedEventArgs(
    string channelLogin,
    string roomId,
    DateTimeOffset clearedAt) : EventArgs
{
    public string ChannelLogin { get; } = channelLogin;
    public string RoomId { get; } = roomId;
    public DateTimeOffset ClearedAt { get; } = clearedAt;
}
