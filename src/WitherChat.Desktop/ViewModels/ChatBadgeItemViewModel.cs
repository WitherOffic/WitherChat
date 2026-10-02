using CommunityToolkit.Mvvm.ComponentModel;
using WitherChat.Core.Models;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.ViewModels;

public sealed partial class ChatBadgeItemViewModel : ViewModelBase
{
    private readonly ChatBadge _badge;
    private readonly ChatImageCache _imageCache;
    private readonly UiText _texts;
    private Uri? _imageUri;

    public ChatBadgeItemViewModel(
        ChatBadge badge,
        ChatImageCache imageCache,
        IReadOnlyDictionary<string, TwitchBadgeDefinition>? catalog,
        UiText? texts = null)
    {
        _badge = badge ?? throw new ArgumentNullException(nameof(badge));
        _imageCache = imageCache ?? throw new ArgumentNullException(nameof(imageCache));
        _texts = texts ?? new UiText();
        Label = GetBadgeLabel(badge.SetId);
        ToolTip = string.IsNullOrWhiteSpace(badge.Info) ? badge.SetId : $"{badge.SetId}: {badge.Info}";
        ApplyCatalog(catalog);
    }

    public string Label { get; }
    public string SetId => _badge.SetId;
    public bool IsYouTube => string.Equals(SetId, "youtube", StringComparison.OrdinalIgnoreCase);
    public bool IsBroadcaster => string.Equals(SetId, "broadcaster", StringComparison.OrdinalIgnoreCase);
    public bool IsModerator =>
        string.Equals(SetId, "moderator", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SetId, "global_mod", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SetId, "staff", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SetId, "admin", StringComparison.OrdinalIgnoreCase);
    public bool IsVip => string.Equals(SetId, "vip", StringComparison.OrdinalIgnoreCase);
    public bool IsSubscription =>
        string.Equals(SetId, "subscriber", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SetId, "founder", StringComparison.OrdinalIgnoreCase);
    public bool IsTurbo => string.Equals(SetId, "turbo", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(SetId, "premium", StringComparison.OrdinalIgnoreCase);
    public bool IsGeneric =>
        !IsYouTube && !IsBroadcaster && !IsModerator && !IsVip && !IsSubscription && !IsTurbo;
    public bool HasImageResource => ImageResource is not null;
    public bool ShowStaticLabel => ImageResource is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImageResource))]
    [NotifyPropertyChangedFor(nameof(ShowStaticLabel))]
    private ChatImageResource? _imageResource;

    [ObservableProperty]
    private string _toolTip;

    public void ApplyCatalog(IReadOnlyDictionary<string, TwitchBadgeDefinition>? catalog)
    {
        TwitchBadgeDefinition? definition = null;
        catalog?.TryGetValue(_badge.Key, out definition);
        var nextUri = definition?.ImageUri ?? _badge.ImageUri;
        var title = string.IsNullOrWhiteSpace(definition?.Title) ? LocalizeTitle(_badge.Title) : definition.Title;
        ToolTip = string.IsNullOrWhiteSpace(title)
            ? string.IsNullOrWhiteSpace(_badge.Info) ? _badge.SetId : $"{_badge.SetId}: {_badge.Info}"
            : title;

        if (_imageUri == nextUri)
        {
            return;
        }

        _imageUri = nextUri;
        ImageResource = nextUri is null ? null : _imageCache.GetResource(nextUri, 18);
    }

    private static string GetBadgeLabel(string badge) => badge switch
    {
        "broadcaster" => "BR",
        "moderator" => "MOD",
        "vip" => "VIP",
        "subscriber" => "SUB",
        "founder" => "FND",
        "staff" => "STAFF",
        "youtube" => "YT",
        _ => badge.ToUpperInvariant()
    };

    private string LocalizeTitle(string title) => title switch
    {
        "witherchat.youtube.owner" => _texts.YouTubeChannelOwnerBadge,
        "witherchat.youtube.moderator" => _texts.YouTubeModeratorBadge,
        "witherchat.youtube.sponsor" => _texts.YouTubeSponsorBadge,
        _ => title
    };
}
