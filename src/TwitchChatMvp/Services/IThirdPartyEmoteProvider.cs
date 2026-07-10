using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public interface IThirdPartyEmoteProvider
{
    string Name { get; }
    Task<IReadOnlyList<ThirdPartyEmote>> LoadEmotesAsync(string twitchBroadcasterId, CancellationToken cancellationToken = default);
}
