namespace TwitchChatMvp.Models;

public sealed record ThirdPartyEmote(
    string Id,
    string Code,
    string ImageUrl,
    string Provider,
    string FallbackImageUrl = "",
    bool IsZeroWidth = false);
