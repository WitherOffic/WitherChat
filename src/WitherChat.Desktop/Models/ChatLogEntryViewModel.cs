using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Avalonia.Threading;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;

namespace WitherChat.Desktop.Models;

public sealed class ChatLogEntryViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions LogJsonOptions = CreateLogJsonOptions();
    private IReadOnlyList<ChatBadgeItemViewModel> _badges = [];
    private IReadOnlyList<ChatMessagePartViewModel> _parts = [];
    private IBrush? _userBrush;

    public ChatLogEntryViewModel(
        string rawText,
        string timeText,
        string user,
        string text,
        string role)
    {
        RawText = rawText;
        TimeText = timeText;
        User = user;
        Text = text;
        Role = role;
    }

    public string RawText { get; }
    public string TimeText { get; }
    public string User { get; }
    public string Text { get; }
    public string Role { get; }
    public string UserColor { get; private set; } = string.Empty;
    public string BroadcasterId { get; private set; } = string.Empty;
    public IReadOnlyList<ChatBadge> BadgeData { get; private set; } = [];
    public IReadOnlyList<ChatMessagePart> PartData { get; private set; } = [];
    public IReadOnlyList<ChatBadgeItemViewModel> Badges => _badges;
    public IReadOnlyList<ChatMessagePartViewModel> Parts => _parts;
    public IBrush? UserBrush => _userBrush;
    public bool HasBadges => _badges.Count > 0;
    public bool HasPresentation => _userBrush is not null;

    public static ChatLogEntryViewModel Parse(string line)
    {
        if (line.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var timestampText = ReadString(root, "timestamp");
                var jsonTime = DateTimeOffset.TryParse(
                    timestampText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var timestamp)
                    ? timestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                    : string.Empty;
                var user = ReadString(root, "displayName");
                if (string.IsNullOrWhiteSpace(user))
                {
                    user = ReadString(root, "userLogin");
                }
                var text = ReadString(root, "text");
                var badges = ReadArray<ChatBadge>(root, "badges")
                    .Where(badge => !string.IsNullOrWhiteSpace(badge.SetId))
                    .ToArray();
                IReadOnlyList<ChatMessagePart> parts = ReadArray<ChatMessagePart>(root, "parts")
                    .Where(part => part.Text is not null && Enum.IsDefined(part.Kind))
                    .ToArray();
                if (parts.Count == 0 && text.Length > 0)
                {
                    parts = [ChatMessagePart.PlainText(text)];
                }

                return new ChatLogEntryViewModel(
                    line,
                    jsonTime,
                    user,
                    text,
                    ReadRole(badges))
                {
                    UserColor = ReadString(root, "color"),
                    BroadcasterId = ReadString(root, "broadcasterId"),
                    BadgeData = badges,
                    PartData = parts
                };
            }
            catch (JsonException)
            {
                // A partially written JSONL line remains visible as raw text.
            }
        }

        var time = string.Empty;
        var remainder = line;
        if (line.StartsWith("[", StringComparison.Ordinal) && line.IndexOf(']') is var end and > 1)
        {
            if (DateTimeOffset.TryParse(line[1..end], out var timestamp))
            {
                time = timestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            }
            remainder = line[(end + 1)..].TrimStart();
        }

        var separator = remainder.IndexOf(": ", StringComparison.Ordinal);
        var entry = separator > 0
            ? new ChatLogEntryViewModel(
                line,
                time,
                remainder[..separator],
                remainder[(separator + 2)..],
                string.Empty)
            : new ChatLogEntryViewModel(line, time, string.Empty, remainder, string.Empty);
        entry.PartData = entry.Text.Length == 0 ? [] : [ChatMessagePart.PlainText(entry.Text)];
        return entry;
    }

    public void ApplyPresentation(
        ChatImageCache imageCache,
        IReadOnlyDictionary<string, TwitchBadgeDefinition>? badgeCatalog,
        IReadOnlyDictionary<string, ThirdPartyEmote>? thirdPartyCatalog,
        bool enableTwitchEmotes,
        bool enableBttvEmotes,
        bool enableSevenTvEmotes,
        UiText? texts = null,
        bool useLightTwitchTheme = false)
    {
        ArgumentNullException.ThrowIfNull(imageCache);
        Dispatcher.UIThread.VerifyAccess();
        _badges = BadgeData
            .Select(badge => new ChatBadgeItemViewModel(badge, imageCache, badgeCatalog, texts))
            .ToArray();

        IReadOnlyList<ChatMessagePart> source = PartData.Count == 0
            ? Text.Length == 0 ? [] : [ChatMessagePart.PlainText(Text)]
            : PartData;
        if (!enableTwitchEmotes)
        {
            source = source
                .Select(part => part.Kind == ChatMessagePartKind.Emote &&
                                string.Equals(part.Provider, "Twitch", StringComparison.OrdinalIgnoreCase)
                    ? ChatMessagePart.PlainText(part.Text)
                    : part)
                .ToArray();
        }

        IReadOnlyDictionary<string, ThirdPartyEmote>? filteredCatalog = thirdPartyCatalog;
        if (thirdPartyCatalog is not null && (!enableBttvEmotes || !enableSevenTvEmotes))
        {
            filteredCatalog = thirdPartyCatalog
                .Where(pair =>
                    (enableBttvEmotes ||
                     !ThirdPartyEmoteProviders.IsBttv(pair.Value.Provider)) &&
                    (enableSevenTvEmotes ||
                     !ThirdPartyEmoteProviders.IsSevenTv(pair.Value.Provider)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        source = ThirdPartyEmoteTokenizer.Tokenize(source, filteredCatalog);
        _parts = source
            .Select(part => new ChatMessagePartViewModel(part, imageCache, useLightTwitchTheme))
            .ToArray();
        _userBrush = ChatUserColor.Create(UserColor, useLightTwitchTheme);

        OnPropertyChanged(nameof(Badges));
        OnPropertyChanged(nameof(HasBadges));
        OnPropertyChanged(nameof(Parts));
        OnPropertyChanged(nameof(UserBrush));
        OnPropertyChanged(nameof(HasPresentation));
    }

    private static IReadOnlyList<T> ReadArray<T>(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        try
        {
            return element.Deserialize<T[]>(LogJsonOptions)?.Where(item => item is not null).ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadRole(IEnumerable<ChatBadge> badges)
    {
        var roles = badges
            .Select(badge => badge.SetId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (roles.Contains("broadcaster")) return "broadcaster";
        if (roles.Contains("moderator")) return "moderator";
        if (roles.Contains("vip")) return "vip";
        if (roles.Contains("subscriber")) return "subscriber";
        return string.Empty;
    }

    private static JsonSerializerOptions CreateLogJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
