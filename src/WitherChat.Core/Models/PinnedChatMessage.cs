namespace WitherChat.Core.Models;

public sealed record PinnedChatMessage(
    string MessageId,
    string BroadcasterId,
    string SenderUserId,
    string SenderUserLogin,
    string SenderDisplayName,
    string Text,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset UpdatedAt);
