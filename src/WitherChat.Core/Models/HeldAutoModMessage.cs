namespace WitherChat.Core.Models;

public sealed record HeldAutoModMessage(
    string MessageId,
    string BroadcasterId,
    string ChannelLogin,
    string UserId,
    string UserLogin,
    string UserDisplayName,
    string MessageText,
    string Category,
    int Level,
    DateTimeOffset HeldAt);

public sealed record EventSubBan(
    string BroadcasterId,
    string BroadcasterLogin,
    string UserId,
    string UserLogin,
    string DisplayName,
    string Reason,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndsAt,
    bool IsPermanent);

public sealed record EventSubUnban(string BroadcasterId, string BroadcasterLogin, string UserId);

public sealed record EventSubUnbanRequest(
    string RequestId,
    string BroadcasterId,
    string BroadcasterLogin,
    string UserId,
    string UserLogin,
    string DisplayName,
    string Text,
    DateTimeOffset CreatedAt,
    string Status);

public sealed record SharedChatState(
    string BroadcasterId,
    string BroadcasterLogin,
    bool IsActive,
    string SessionId,
    int ParticipantCount);

public sealed record EventSubMessageDeleted(
    string BroadcasterId,
    string BroadcasterLogin,
    string MessageId,
    string UserId);

public sealed record EventSubUserMessagesCleared(
    string BroadcasterId,
    string BroadcasterLogin,
    string UserId);

public sealed record EventSubChatCleared(
    string BroadcasterId,
    string BroadcasterLogin,
    DateTimeOffset ClearedAt);
