using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace WitherChat.Models;

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
    public string Provider { get; init; } = string.Empty;
    public string EmoteId { get; init; } = string.Empty;
    public int Flags { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public bool DeclaredAnimated { get; init; }
    public ObservableCollection<ChatMessagePartModel> OverlayParts { get; } = [];
    public bool HasOverlays => OverlayParts.Count > 0;
    public double DisplayHeight => 28;
    public double DisplayWidth
    {
        get
        {
            var width = SourceWidth;
            var height = SourceHeight;
            if (width <= 0 || height <= 0)
            {
                return DisplayHeight;
            }

            return Math.Clamp(DisplayHeight * width / height, 8, 196);
        }
    }
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
        CacheKey = $"visual-v2:{emote.Provider}:{emote.Id}:{emote.ImageUrl}",
        ToolTip = string.IsNullOrWhiteSpace(emote.Provider) ? emote.Code : $"{emote.Code} ({emote.Provider})",
        IsZeroWidth = emote.IsZeroWidth,
        Provider = emote.Provider,
        EmoteId = emote.Id,
        Flags = emote.Flags,
        SourceWidth = emote.SourceWidth,
        SourceHeight = emote.SourceHeight,
        DeclaredAnimated = emote.IsAnimated
    };

    public void AddOverlay(ChatMessagePartModel overlay)
    {
        OverlayParts.Add(overlay);
        OnPropertyChanged(nameof(HasOverlays));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
