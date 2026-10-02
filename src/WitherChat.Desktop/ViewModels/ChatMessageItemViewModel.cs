using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.ViewModels;

public enum ChatMessageModerationState
{
    None,
    Deleted,
    TimedOut,
    Banned
}

public sealed partial class ChatMessageItemViewModel : ViewModelBase
{
    private readonly ChatImageCache _imageCache;
    private readonly UiText _texts;
    private IReadOnlyDictionary<string, TwitchBadgeDefinition>? _badgeCatalog;
    private IReadOnlyDictionary<string, ThirdPartyEmote>? _thirdPartyCatalog;
    private IReadOnlyList<ChatBadgeItemViewModel>? _badges;
    private IReadOnlyList<ChatBadgeItemViewModel>? _compactBadges;
    private IReadOnlyList<ChatMessagePartViewModel>? _parts;
    private bool _enableTwitchEmotes = true;
    private bool _enableBttvEmotes = true;
    private bool _enableSevenTvEmotes = true;
    private bool _useLightTwitchTheme;

    public ChatMessageItemViewModel(
        ChatMessage message,
        ChatImageCache imageCache,
        UiText texts,
        IReadOnlyDictionary<string, TwitchBadgeDefinition>? badgeCatalog = null,
        IReadOnlyDictionary<string, ThirdPartyEmote>? thirdPartyCatalog = null,
        MainWindowViewModel? owner = null)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        _imageCache = imageCache ?? throw new ArgumentNullException(nameof(imageCache));
        _texts = texts ?? throw new ArgumentNullException(nameof(texts));
        Owner = owner;
        _badgeCatalog = badgeCatalog;
        _thirdPartyCatalog = thirdPartyCatalog;
        if (owner is not null)
        {
            _enableTwitchEmotes = owner.EnableTwitchEmotes;
            _enableBttvEmotes = owner.EnableBttvEmotes;
            _enableSevenTvEmotes = owner.EnableSevenTvEmotes;
            _useLightTwitchTheme = string.Equals(owner.Theme, "Light", StringComparison.Ordinal);
        }
        UserBrush = ChatUserColor.Create(message.UserColor, _useLightTwitchTheme);
    }

    public ChatMessage Message { get; }
    public MainWindowViewModel? Owner { get; }
    public string Id => Message.Id;
    public string TimeText => Message.TimeText;
    public string UserLabel => Message.UserLabel;
    public string UserLabelWithColon => Message.UserLabel + ":";
    public string Text => Message.Text;
    public bool HasText => !string.IsNullOrWhiteSpace(Message.Text);
    public string AvatarInitial => Message.AvatarInitial;
    public string LoginLabel => string.IsNullOrWhiteSpace(Message.UserLogin)
        ? string.Empty
        : "@" + Message.UserLogin;
    public IReadOnlyList<ChatBadgeItemViewModel> Badges =>
        _badges ??= Message.Badges
            .Select(badge => new ChatBadgeItemViewModel(badge, _imageCache, _badgeCatalog, _texts))
            .ToArray();
    public IReadOnlyList<ChatBadgeItemViewModel> CompactBadges =>
        _compactBadges ??= Badges
            .Where(badge => badge.IsYouTube)
            .Take(1)
            .Concat(Badges.Where(badge => badge.IsSubscription).Take(1))
            .ToArray();
    public IReadOnlyList<ChatMessagePartViewModel> Parts => _parts ??= BuildParts();
    internal bool HasHydratedBadges => _badges is not null;
    internal bool HasHydratedParts => _parts is not null;
    public bool HasBadges => Badges.Count > 0;
    public bool HasCompactBadges => CompactBadges.Count > 0;
    public bool HasReply => Message.HasReply;
    public string ReplyLabel => string.IsNullOrWhiteSpace(Message.ReplyParentDisplayName)
        ? _texts.Reply
        : _texts.Reply + " @" + Message.ReplyParentDisplayName;
    public string ReplyText => Message.ReplyParentText;
    public bool IsLong => Message.IsLong;
    public bool IsPinned => Message.IsPinned || IsPinnedOverride;
    public bool IsChannelPointRedemption => Message.IsChannelPointRedemption;
    public bool IsSharedChatMessage => Message.IsSharedChatMessage;
    public bool IsYouTubeMessage => Message.IsYouTubeMessage;
    public bool IsSystemEvent => Message.IsSystemEvent;
    public bool IsPaidEvent => Message.IsPaidEvent;
    public string SystemEventTitle => Message.StreamEvent is null
        ? string.Empty
        : _texts.StreamEventTitle(Message.StreamEvent.Kind, Message.StreamEvent.Count);
    public string SystemEventAmount => Message.StreamEvent?.AmountDisplay ?? string.Empty;
    public bool HasSystemEventAmount => SystemEventAmount.Length > 0;
    public string SystemEventPlatformMark => Message.StreamEvent?.Platform switch
    {
        ChatPlatforms.YouTube => "YT",
        "DonationAlerts" => "DA",
        _ => "TW"
    };
    public string OpenUserProfileLabel => IsYouTubeMessage ? _texts.OpenOnYouTube : _texts.OpenOnTwitch;
    public string SharedSourceLabel => "↗ @" + (string.IsNullOrWhiteSpace(Message.SourceChannelDisplayName)
        ? Message.SourceChannelLogin
        : Message.SourceChannelDisplayName);
    public string ChannelPointsLabel => string.IsNullOrWhiteSpace(Message.RewardTitle)
        ? _texts.ChannelPointsMark
        : Message.RewardCost is { } cost
            ? $"{Message.RewardTitle} · {cost:N0}"
            : Message.RewardTitle;
    public string PinnedLabel => _texts.PinnedMark;
    public IBrush UserBrush { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfileImage))]
    private ChatImageResource? _profileImageResource;

    public bool HasProfileImage => ProfileImageResource?.HasImage == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModerated))]
    [NotifyPropertyChangedFor(nameof(ModerationStatus))]
    [NotifyPropertyChangedFor(nameof(ContentOpacity))]
    [NotifyPropertyChangedFor(nameof(ContentTextDecorations))]
    private ChatMessageModerationState _moderationState;

    public bool IsModerated => ModerationState != ChatMessageModerationState.None;
    public string ModerationStatus => ModerationState switch
    {
        ChatMessageModerationState.Deleted => _texts.MessageDeleted,
        ChatMessageModerationState.TimedOut => _texts.UserTimedOut,
        ChatMessageModerationState.Banned => _texts.UserBanned,
        _ => string.Empty
    };
    public double ContentOpacity => IsModerated ? 0.48 : 1;
    public TextDecorationCollection? ContentTextDecorations =>
        IsModerated ? TextDecorations.Strikethrough : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    private bool _isPinnedOverride;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxLines))]
    [NotifyPropertyChangedFor(nameof(ExpandLabel))]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedContent))]
    [NotifyPropertyChangedFor(nameof(ShowRichContent))]
    private bool _isExpanded;

    public int MaxLines => IsExpanded ? 0 : 5;
    public string ExpandLabel => IsExpanded ? _texts.Collapse : _texts.Expand;
    public bool ShowCollapsedContent => IsLong && !IsExpanded;
    public bool ShowRichContent => !IsLong || IsExpanded;

    [RelayCommand]
    private void ToggleExpanded()
    {
        if (IsLong)
        {
            IsExpanded = !IsExpanded;
        }
    }

    public void ApplyBadgeCatalog(IReadOnlyDictionary<string, TwitchBadgeDefinition> catalog)
    {
        _badgeCatalog = catalog;
        if (_badges is null)
        {
            return;
        }

        foreach (var badge in _badges)
        {
            badge.ApplyCatalog(catalog);
        }
    }

    public void ApplyThirdPartyCatalog(IReadOnlyDictionary<string, ThirdPartyEmote>? catalog)
    {
        _thirdPartyCatalog = catalog;
        RebuildHydratedParts();
    }

    public void ApplyPresentationSettings(
        bool enableTwitchEmotes,
        bool enableBttvEmotes,
        bool enableSevenTvEmotes,
        IReadOnlyDictionary<string, ThirdPartyEmote>? catalog,
        bool useLightTwitchTheme = false)
    {
        var themeChanged = _useLightTwitchTheme != useLightTwitchTheme;
        var changed = _enableTwitchEmotes != enableTwitchEmotes ||
                      _enableBttvEmotes != enableBttvEmotes ||
                      _enableSevenTvEmotes != enableSevenTvEmotes ||
                      _useLightTwitchTheme != useLightTwitchTheme ||
                      !ReferenceEquals(_thirdPartyCatalog, catalog);
        _enableTwitchEmotes = enableTwitchEmotes;
        _enableBttvEmotes = enableBttvEmotes;
        _enableSevenTvEmotes = enableSevenTvEmotes;
        _useLightTwitchTheme = useLightTwitchTheme;
        _thirdPartyCatalog = catalog;
        if (themeChanged)
        {
            UserBrush = ChatUserColor.Create(Message.UserColor, _useLightTwitchTheme);
            OnPropertyChanged(nameof(UserBrush));
        }
        if (changed)
        {
            RebuildHydratedParts();
        }
    }

    private IReadOnlyList<ChatMessagePartViewModel> BuildParts()
    {
        IReadOnlyList<ChatMessagePart> source = Message.Parts.Count == 0
            ? [ChatMessagePart.PlainText(Message.Text)]
            : Message.Parts;
        if (!_enableTwitchEmotes)
        {
            source = source
                .Select(part => part.Kind == ChatMessagePartKind.Emote &&
                                string.Equals(part.Provider, "Twitch", StringComparison.OrdinalIgnoreCase)
                    ? ChatMessagePart.PlainText(part.Text)
                    : part)
                .ToArray();
        }

        IReadOnlyDictionary<string, ThirdPartyEmote>? filteredCatalog = _thirdPartyCatalog;
        if (_thirdPartyCatalog is not null && (!_enableBttvEmotes || !_enableSevenTvEmotes))
        {
            filteredCatalog = _thirdPartyCatalog
                .Where(pair =>
                    (_enableBttvEmotes ||
                     !ThirdPartyEmoteProviders.IsBttv(pair.Value.Provider)) &&
                    (_enableSevenTvEmotes ||
                     !ThirdPartyEmoteProviders.IsSevenTv(pair.Value.Provider)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        return ThirdPartyEmoteTokenizer.Tokenize(source, filteredCatalog)
            .SelectMany(part => ChatMessagePartViewModel.Create(part, _imageCache, _useLightTwitchTheme))
            .ToArray();
    }

    private void RebuildHydratedParts()
    {
        if (_parts is null)
        {
            return;
        }

        _parts = BuildParts();
        OnPropertyChanged(nameof(Parts));
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(ReplyLabel));
        OnPropertyChanged(nameof(ExpandLabel));
        OnPropertyChanged(nameof(PinnedLabel));
        OnPropertyChanged(nameof(ModerationStatus));
        OnPropertyChanged(nameof(OpenUserProfileLabel));
        OnPropertyChanged(nameof(SystemEventTitle));
        if (_badges is not null)
        {
            foreach (var badge in _badges)
            {
                badge.ApplyCatalog(_badgeCatalog);
            }
        }
    }

    public void MarkModerated(ChatMessageModerationState state) => ModerationState = state;



}
