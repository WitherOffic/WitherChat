using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public interface IChannelSearchService
{
    bool IsOnlineSearchAvailable { get; }

    Task<IReadOnlyList<ChannelSearchResult>> SearchChannelsAsync(
        string query,
        CancellationToken cancellationToken = default);
}
