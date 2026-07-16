namespace WitherChat.Models;

public sealed record ThirdPartyEmote(
    string Id,
    string Code,
    string ImageUrl,
    string Provider,
    string FallbackImageUrl = "",
    bool IsZeroWidth = false,
    bool IsAnimated = false,
    int Flags = 0,
    int SourceWidth = 0,
    int SourceHeight = 0);
