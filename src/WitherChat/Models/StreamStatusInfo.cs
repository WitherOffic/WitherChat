namespace WitherChat.Models;

public sealed record StreamStatusInfo(
    bool IsLive,
    int ViewerCount,
    string Title,
    string GameName = "",
    DateTimeOffset? StartedAt = null,
    bool IsAuthoritative = true);
