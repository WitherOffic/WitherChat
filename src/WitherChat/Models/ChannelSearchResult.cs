namespace WitherChat.Models;

public sealed class ChannelSearchResult
{
    public string Id { get; init; } = string.Empty;
    public string BroadcasterLogin { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ThumbnailUrl { get; init; } = string.Empty;
    public string GameName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public bool IsLive { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
}
