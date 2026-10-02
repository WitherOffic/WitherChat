using WitherChat.Core.Models;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.Models;

public sealed record ChannelSearchResultViewModel(
    ChannelSearchResult Value,
    ChatImageResource? Thumbnail)
{
    public string Login => Value.BroadcasterLogin;
    public string LoginLabel => "@" + Value.BroadcasterLogin;
    public string DisplayName => Value.DisplayName;
    public string GameName => Value.GameName;
    public bool IsLive => Value.IsLive;
    public int ViewerCount => Value.ViewerCount;
    public bool HasViewerCount => Value.IsLive;
}
