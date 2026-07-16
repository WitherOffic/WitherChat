using WitherChat.Models;

namespace WitherChat.Services;

public interface IThirdPartyEmoteProvider
{
    string Name { get; }
    Task<IReadOnlyList<ThirdPartyEmote>> LoadEmotesAsync(string twitchBroadcasterId, CancellationToken cancellationToken = default);
}
