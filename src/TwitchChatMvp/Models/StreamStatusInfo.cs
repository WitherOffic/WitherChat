namespace TwitchChatMvp.Models;

public sealed record StreamStatusInfo(
    bool IsLive,
    int ViewerCount,
    string Title,
    string GameName = "",
    DateTimeOffset? StartedAt = null);
