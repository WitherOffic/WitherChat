using TwitchChatMvp.Services;

namespace TwitchChatMvp.Models;

public sealed class AppSettings
{
    public bool UseCustomClientId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = "http://localhost:17654/";
    public bool UseCustomChannel { get; set; }
    public string ChannelLogin { get; set; } = string.Empty;
    public string BroadcasterId { get; set; } = string.Empty;
    public ChatConnectionMode ConnectionMode { get; set; } = ChatConnectionMode.SignedIn;
    public string LastReadOnlyChannel { get; set; } = string.Empty;
    public double FontSize { get; set; } = 17;
    public int MessageLimit { get; set; } = 500;
    public bool ShowTimestamps { get; set; } = true;
    public bool EnableTwitchEmotes { get; set; } = true;
    public bool EnableBttvEmotes { get; set; } = true;
    public bool EnableSevenTvEmotes { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = LocalizationService.Russian;
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
    public bool SaveChatLogJsonl { get; set; } = true;
    public bool SaveChatLogTxt { get; set; } = true;
    public bool LogChatBadges { get; set; } = true;
    public bool LogDeletedMessages { get; set; }
    public int MaxLogViewerMessages { get; set; } = 3000;
    public bool AutoSplitLogsByStream { get; set; } = true;

    public string OverlayUrl => $"http://localhost:{OverlayPort}/overlay/chat";

    public AppSettings Clone() => new()
    {
        UseCustomClientId = UseCustomClientId,
        ClientId = ClientId,
        RedirectUri = RedirectUri,
        UseCustomChannel = UseCustomChannel,
        ChannelLogin = ChannelLogin,
        BroadcasterId = BroadcasterId,
        ConnectionMode = ConnectionMode,
        LastReadOnlyChannel = LastReadOnlyChannel,
        FontSize = FontSize,
        MessageLimit = MessageLimit,
        ShowTimestamps = ShowTimestamps,
        EnableTwitchEmotes = EnableTwitchEmotes,
        EnableBttvEmotes = EnableBttvEmotes,
        EnableSevenTvEmotes = EnableSevenTvEmotes,
        Theme = Theme,
        Language = Language,
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
        SaveChatLogJsonl = SaveChatLogJsonl,
        SaveChatLogTxt = SaveChatLogTxt,
        LogChatBadges = LogChatBadges,
        LogDeletedMessages = LogDeletedMessages,
        MaxLogViewerMessages = MaxLogViewerMessages,
        AutoSplitLogsByStream = AutoSplitLogsByStream
    };

    public void Normalize()
    {
        ClientId = ClientId.Trim();
        RedirectUri = string.IsNullOrWhiteSpace(RedirectUri) ? "http://localhost:17654/" : RedirectUri.Trim();
        if (string.Equals(RedirectUri, "http://localhost:3000/", StringComparison.OrdinalIgnoreCase))
        {
            RedirectUri = "http://localhost:17654/";
        }

        if (!RedirectUri.EndsWith("/", StringComparison.Ordinal))
        {
            RedirectUri += "/";
        }

        ChannelLogin = ChannelLogin.Trim().TrimStart('@').ToLowerInvariant();
        BroadcasterId = BroadcasterId.Trim();
        LastReadOnlyChannel = LastReadOnlyChannel.Trim().TrimStart('@').ToLowerInvariant();
        if (!Enum.IsDefined(ConnectionMode))
        {
            ConnectionMode = ChatConnectionMode.SignedIn;
        }

        FontSize = Math.Clamp(FontSize, 10, 32);
        MessageLimit = MessageLimit is < 100 or > 5000 ? 500 : MessageLimit;
        Theme = string.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        Language = LocalizationService.NormalizeLanguage(Language);
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

        if (!SaveChatLogJsonl && !SaveChatLogTxt)
        {
            SaveChatLogJsonl = true;
        }
    }
}
