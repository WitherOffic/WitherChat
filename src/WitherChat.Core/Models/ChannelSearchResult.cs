namespace WitherChat.Core.Models;

public sealed record ChannelSearchResult(
    string Id,
    string BroadcasterLogin,
    string DisplayName,
    string ThumbnailUrl,
    string GameName,
    string Title,
    bool IsLive,
    DateTimeOffset? StartedAt,
    int ViewerCount = 0);
