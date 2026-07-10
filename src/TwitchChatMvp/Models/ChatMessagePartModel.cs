using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace TwitchChatMvp.Models;

public enum ChatMessagePartKind
{
    Text,
    TwitchEmote,
    ThirdPartyEmote
}

public sealed class ChatMessagePartModel : INotifyPropertyChanged
{
    private ImageSource? _imageSource;
    private EmoteMedia? _media;
    private bool _overlayPrevious;

    public ChatMessagePartKind Kind { get; init; } = ChatMessagePartKind.Text;
    public string Text { get; init; } = string.Empty;
    public string ImageUrl { get; init; } = string.Empty;
    public string FallbackImageUrl { get; init; } = string.Empty;
    public string CacheKey { get; init; } = string.Empty;
    public string ToolTip { get; init; } = string.Empty;
    public bool IsZeroWidth { get; init; }
    public Thickness DisplayMargin => _overlayPrevious ? new Thickness(-29, 0, 1, 0) : new Thickness(0, 0, 1, 0);

    public bool OverlayPrevious
    {
        get => _overlayPrevious;
        set
        {
            if (_overlayPrevious != value)
            {
                _overlayPrevious = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayMargin));
            }
        }
    }

    public ImageSource? ImageSource
    {
        get => _imageSource;
        set
        {
            if (!ReferenceEquals(_imageSource, value))
            {
                _imageSource = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasImage));
            }
        }
    }

    public EmoteMedia? Media
    {
        get => _media;
        set
        {
            if (!ReferenceEquals(_media, value))
            {
                _media = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasImage));
            }
        }
    }

    public bool HasImage => Media?.FirstFrame is not null || ImageSource is not null;

    public static ChatMessagePartModel TextPart(string text) => new()
    {
        Kind = ChatMessagePartKind.Text,
        Text = text
    };

    public static ChatMessagePartModel TwitchEmote(string text, string emoteId, bool isAnimated = false) => new()
    {
        Kind = ChatMessagePartKind.TwitchEmote,
        Text = text,
        ImageUrl = string.IsNullOrWhiteSpace(emoteId)
            ? string.Empty
            : $"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(emoteId)}/{(isAnimated ? "animated" : "static")}/dark/2.0",
        FallbackImageUrl = !isAnimated || string.IsNullOrWhiteSpace(emoteId)
            ? string.Empty
            : $"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(emoteId)}/static/dark/2.0",
        CacheKey = string.IsNullOrWhiteSpace(emoteId) ? string.Empty : $"twitch:{emoteId}:2x",
        ToolTip = text
    };

    public static ChatMessagePartModel ThirdPartyEmote(ThirdPartyEmote emote) => new()
    {
        Kind = ChatMessagePartKind.ThirdPartyEmote,
        Text = emote.Code,
        ImageUrl = emote.ImageUrl,
        FallbackImageUrl = emote.FallbackImageUrl,
        CacheKey = $"{emote.Provider}:{emote.Id}:{emote.ImageUrl}",
        ToolTip = string.IsNullOrWhiteSpace(emote.Provider) ? emote.Code : $"{emote.Code} ({emote.Provider})",
        IsZeroWidth = emote.IsZeroWidth
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
