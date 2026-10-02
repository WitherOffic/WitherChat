namespace WitherChat.Core.Models;

public enum ChatMessagePartKind
{
    Text,
    Emote
}

public sealed record ChatMessagePart
{
    public required ChatMessagePartKind Kind { get; init; }
    public required string Text { get; init; }
    public Uri? ImageUri { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string EmoteId { get; init; } = string.Empty;
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public bool IsZeroWidth { get; init; }

    public static ChatMessagePart PlainText(string text) => new()
    {
        Kind = ChatMessagePartKind.Text,
        Text = text
    };

    public static ChatMessagePart TwitchEmote(
        string text,
        string emoteId,
        bool isAnimated = false) => new()
        {
            Kind = ChatMessagePartKind.Emote,
            Text = text,
            EmoteId = emoteId,
            Provider = "Twitch",
            ImageUri = string.IsNullOrWhiteSpace(emoteId)
            ? null
            : new Uri(
                $"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(emoteId)}/" +
                $"{(isAnimated ? "animated" : "static")}/dark/2.0")
        };

    public Uri? GetImageUri(bool useLightTwitchTheme)
    {
        if (!useLightTwitchTheme ||
            !string.Equals(Provider, "Twitch", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(EmoteId))
        {
            return ImageUri;
        }

        var animation = ImageUri?.AbsolutePath.Contains("/animated/", StringComparison.OrdinalIgnoreCase) == true
            ? "animated"
            : "static";
        return new Uri(
            $"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(EmoteId)}/" +
            $"{animation}/light/2.0");
    }
}
