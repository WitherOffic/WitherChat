using WitherChat.Core.Models;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.ViewModels;

public sealed class ChatMessagePartViewModel : ViewModelBase
{
    private readonly ChatMessagePart _part;

    public ChatMessagePartViewModel(
        ChatMessagePart part,
        ChatImageCache imageCache,
        bool useLightTwitchTheme = false)
    {
        _part = part ?? throw new ArgumentNullException(nameof(part));
        ArgumentNullException.ThrowIfNull(imageCache);
        var imageUri = part.GetImageUri(useLightTwitchTheme);
        if (part.Kind == ChatMessagePartKind.Emote && imageUri is not null)
        {
            var fallbackWidth = part.SourceWidth > 0 && part.SourceHeight > 0
                ? Math.Clamp(DisplayHeight * part.SourceWidth / part.SourceHeight, 8, 196)
                : Math.Clamp(Math.Max(DisplayHeight, part.Text.EnumerateRunes().Count() * 8.5), DisplayHeight, 196);
            ImageResource = imageCache.GetResource(imageUri, fallbackWidth);
        }
    }

    private ChatMessagePartViewModel(string displayText, Uri linkUri, string fullLink)
    {
        _part = ChatMessagePart.PlainText(displayText);
        LinkUri = linkUri;
        FullLink = fullLink;
    }

    public static IReadOnlyList<ChatMessagePartViewModel> Create(
        ChatMessagePart part,
        ChatImageCache imageCache,
        bool useLightTwitchTheme = false)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(imageCache);
        if (part.Kind != ChatMessagePartKind.Text)
        {
            return [new ChatMessagePartViewModel(part, imageCache, useLightTwitchTheme)];
        }

        return ChatLinkParser.Parse(part.Text)
            .Select(segment => segment.IsLink
                ? new ChatMessagePartViewModel(segment.Text, segment.Uri!, segment.FullUrl)
                : new ChatMessagePartViewModel(
                    ChatMessagePart.PlainText(segment.Text),
                    imageCache,
                    useLightTwitchTheme))
            .ToArray();
    }

    public string Text => _part.Text;
    public ChatMessagePartKind Kind => _part.Kind;
    public string Provider => _part.Provider;
    public string ToolTip => IsLink
        ? FullLink
        : string.IsNullOrWhiteSpace(_part.Provider)
        ? _part.Text
        : $"{_part.Text} ({_part.Provider})";
    public bool IsText => _part.Kind == ChatMessagePartKind.Text && !IsLink;
    public bool IsLink => LinkUri is not null;
    public bool IsZeroWidth => _part.IsZeroWidth;
    public double DisplayHeight => 28;
    public ChatImageResource? ImageResource { get; }
    public Uri? LinkUri { get; }
    public string FullLink { get; } = string.Empty;
}
