using WitherChat.Models;

namespace WitherChat.Services;

public interface IChannelSearchService
{
    bool IsOnlineSearchAvailable { get; }

    Task<IReadOnlyList<ChannelSearchResult>> SearchChannelsAsync(
        string query,
        CancellationToken cancellationToken = default);
}
