namespace WitherChat.Core.Models;

public sealed record YouTubeChatBan(
    string Id,
    string LiveChatId,
    string UserChannelId,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndsAt);
