using WitherChat.Services;
using System.Text.Json.Serialization;

namespace WitherChat.Models;

public sealed class AppSettings
{
    public const int CurrentChannelSettingsMigrationVersion = 1;

    public bool UseCustomClientId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = "http://localhost:17654/";
    public ChatConnectionMode ConnectionMode { get; set; } = ChatConnectionMode.SignedIn;
    public string LastReadOnlyChannel { get; set; } = string.Empty;
    public List<string> SavedChannelLogins { get; set; } = [];
    public string LastActiveChannelLogin { get; set; } = string.Empty;
    public int ChannelSettingsMigrationVersion { get; set; }
    public double FontSize { get; set; } = 17;
    public int MessageLimit { get; set; } = 500;
    public bool ShowTimestamps { get; set; } = true;
    public bool EnableTwitchEmotes { get; set; } = true;
    public bool EnableBttvEmotes { get; set; } = true;
    public bool EnableSevenTvEmotes { get; set; } = true;
    public bool ShowChannelPointRedemptions { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = LocalizationService.Russian;
    public string WindowControlsPosition { get; set; } = "Left";
    public bool AlwaysOnTop { get; set; }
    public bool ToastNotifications { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public bool EnableBadges { get; set; } = true;
    public bool EnableObsOverlay { get; set; }
    public int OverlayPort { get; set; } = 17655;
    public int OverlayMaxMessages { get; set; } = 12;
    public double OverlayFontSize { get; set; } = 22;
    public bool OverlayShowTimestamps { get; set; } = true;
    public bool OverlayShowBadges { get; set; } = true;
    public bool OverlayShowEmotes { get; set; } = true;
    public int OverlayFadeOutSeconds { get; set; }
    public bool OverlayTextShadow { get; set; } = true;
    public double OverlayBackgroundOpacity { get; set; }
    public string OverlayAlign { get; set; } = "left";
    public bool EnableChatLogging { get; set; } = true;
    public string ChatLogsFolder { get; set; } = string.Empty;
    public bool SaveChatLogTxt { get; set; } = true;
    public bool LogChatBadges { get; set; } = true;
    public bool LogChannelPointRedemptions { get; set; } = true;
    public int MaxLogViewerMessages { get; set; } = 3000;

    [JsonIgnore]
    public string OverlayUrl => $"http://localhost:{OverlayPort}/overlay/chat";

    public AppSettings Clone() => new()
    {
        UseCustomClientId = UseCustomClientId,
        ClientId = ClientId,
        RedirectUri = RedirectUri,
        ConnectionMode = ConnectionMode,
        LastReadOnlyChannel = LastReadOnlyChannel,
        SavedChannelLogins = SavedChannelLogins.ToList(),
        LastActiveChannelLogin = LastActiveChannelLogin,
        ChannelSettingsMigrationVersion = ChannelSettingsMigrationVersion,
        FontSize = FontSize,
        MessageLimit = MessageLimit,
        ShowTimestamps = ShowTimestamps,
        EnableTwitchEmotes = EnableTwitchEmotes,
        EnableBttvEmotes = EnableBttvEmotes,
        EnableSevenTvEmotes = EnableSevenTvEmotes,
        ShowChannelPointRedemptions = ShowChannelPointRedemptions,
        Theme = Theme,
        Language = Language,
        WindowControlsPosition = WindowControlsPosition,
        AlwaysOnTop = AlwaysOnTop,
        ToastNotifications = ToastNotifications,
        ReduceMotion = ReduceMotion,
        EnableBadges = EnableBadges,
        EnableObsOverlay = EnableObsOverlay,
        OverlayPort = OverlayPort,
        OverlayMaxMessages = OverlayMaxMessages,
        OverlayFontSize = OverlayFontSize,
        OverlayShowTimestamps = OverlayShowTimestamps,
        OverlayShowBadges = OverlayShowBadges,
        OverlayShowEmotes = OverlayShowEmotes,
        OverlayFadeOutSeconds = OverlayFadeOutSeconds,
        OverlayTextShadow = OverlayTextShadow,
        OverlayBackgroundOpacity = OverlayBackgroundOpacity,
        OverlayAlign = OverlayAlign,
        EnableChatLogging = EnableChatLogging,
        ChatLogsFolder = ChatLogsFolder,
        SaveChatLogTxt = SaveChatLogTxt,
        LogChatBadges = LogChatBadges,
        LogChannelPointRedemptions = LogChannelPointRedemptions,
        MaxLogViewerMessages = MaxLogViewerMessages
    };

    public void Normalize()
    {
        ClientId = (ClientId ?? string.Empty).Trim();
        RedirectUri = string.IsNullOrWhiteSpace(RedirectUri) ? "http://localhost:17654/" : RedirectUri.Trim();
        if (string.Equals(RedirectUri, "http://localhost:3000/", StringComparison.OrdinalIgnoreCase))
        {
            RedirectUri = "http://localhost:17654/";
        }

        if (!RedirectUri.EndsWith('/'))
        {
            RedirectUri += "/";
        }

        LastReadOnlyChannel = (LastReadOnlyChannel ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();
        var savedChannels = (SavedChannelLogins ?? [])
            .Select(NormalizeChannelLogin)
            .Where(IsValidChannelLogin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        if (ChannelSettingsMigrationVersion < CurrentChannelSettingsMigrationVersion)
        {
            if (IsValidChannelLogin(LastReadOnlyChannel) &&
                !savedChannels.Contains(LastReadOnlyChannel, StringComparer.OrdinalIgnoreCase))
            {
                savedChannels.Add(LastReadOnlyChannel);
            }

            ChannelSettingsMigrationVersion = CurrentChannelSettingsMigrationVersion;
        }

        SavedChannelLogins = savedChannels.Take(3).ToList();
        LastActiveChannelLogin = NormalizeChannelLogin(LastActiveChannelLogin);
        if (!SavedChannelLogins.Contains(LastActiveChannelLogin, StringComparer.OrdinalIgnoreCase))
        {
            LastActiveChannelLogin = SavedChannelLogins.FirstOrDefault() ?? string.Empty;
        }
        if (!Enum.IsDefined(ConnectionMode))
        {
            ConnectionMode = ChatConnectionMode.SignedIn;
        }

        FontSize = Math.Clamp(FontSize, 10, 32);
        MessageLimit = MessageLimit is < 100 or > 5000 ? 500 : MessageLimit;
        Theme = string.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        Language = LocalizationService.NormalizeLanguage(Language);
        WindowControlsPosition = string.Equals(WindowControlsPosition, "Right", StringComparison.OrdinalIgnoreCase)
            ? "Right"
            : "Left";
        OverlayPort = Math.Clamp(OverlayPort, 1024, 65535);
        OverlayMaxMessages = Math.Clamp(OverlayMaxMessages, 1, 100);
        OverlayFontSize = Math.Clamp(OverlayFontSize, 10, 72);
        OverlayFadeOutSeconds = Math.Clamp(OverlayFadeOutSeconds, 0, 600);
        OverlayBackgroundOpacity = Math.Clamp(OverlayBackgroundOpacity, 0, 1);
        OverlayAlign = (OverlayAlign ?? string.Empty).ToLowerInvariant() switch
        {
            "center" => "center",
            "right" => "right",
            _ => "left"
        };
        ChatLogsFolder = (ChatLogsFolder ?? string.Empty).Trim();
        MaxLogViewerMessages = Math.Clamp(MaxLogViewerMessages, 100, 50000);

    }

    private static string NormalizeChannelLogin(string? login) =>
        (login ?? string.Empty).Trim().TrimStart('@', '#').ToLowerInvariant();

    private static bool IsValidChannelLogin(string login) =>
        login.Length is > 0 and <= 25 && login.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
