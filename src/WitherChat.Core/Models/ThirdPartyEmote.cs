namespace WitherChat.Core.Models;

public sealed record ThirdPartyEmote(
    string Id,
    string Code,
    Uri ImageUri,
    string Provider,
    bool IsZeroWidth = false,
    int SourceWidth = 0,
    int SourceHeight = 0);

public static class ThirdPartyEmoteProviders
{
    public const string Bttv = "BTTV";
    public const string SevenTv = "7TV";

    public static bool IsBttv(string provider) =>
        string.Equals(provider, Bttv, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider, "BetterTTV", StringComparison.OrdinalIgnoreCase);

    public static bool IsSevenTv(string provider) =>
        string.Equals(provider, SevenTv, StringComparison.OrdinalIgnoreCase);
}
