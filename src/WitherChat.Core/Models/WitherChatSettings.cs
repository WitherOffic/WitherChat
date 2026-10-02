using System.Diagnostics.CodeAnalysis;

namespace WitherChat.Core.Models;

public sealed class WitherChatSettings
{
    public const int DefaultViewerCountRefreshIntervalSeconds = 30;
    public const int MinimumViewerCountRefreshIntervalSeconds = 15;
    public const int MaximumViewerCountRefreshIntervalSeconds = 600;

    public string Channel { get; set; } = string.Empty;
    public List<string> SavedChannels { get; set; } = [];
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "ru";
    public bool AlwaysOnTop { get; set; }
    public bool ToastNotifications { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public int MessageLimit { get; set; } = 500;
    public int ViewerCountRefreshIntervalSeconds { get; set; } = DefaultViewerCountRefreshIntervalSeconds;
    public double ChatFontSize { get; set; } = 17;
    public bool ShowTimestamps { get; set; } = true;
    public bool ShowBadges { get; set; } = true;
    public bool EnableTwitchEmotes { get; set; } = true;
    public bool EnableBttvEmotes { get; set; } = true;
    public bool EnableSevenTvEmotes { get; set; } = true;
    public bool ShowChannelPointRedemptions { get; set; } = true;
    public bool SplitChatView { get; set; }
    public string MessageVisualTheme { get; set; } = "TornBlack";
    public string UiFontFamily { get; set; } = "SegoeUIVariable";
    public bool WindowControlsOnRight { get; set; }
    public string WindowControlsStyle { get; set; } = "Mac";
    public bool ReduceMotion { get; set; }
    public bool HasCompletedOnboarding { get; set; }
    public bool UseCustomClientId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = "http://localhost:17654/";
    public bool DonationAlertsAutoOpenWindow { get; set; } = true;
    public bool EnableObsOverlay { get; set; }
    public int OverlayPort { get; set; } = 17655;
    public int OverlayMaxMessages { get; set; } = 12;
    public double OverlayFontSize { get; set; } = 22;
    public bool OverlayShowTimestamps { get; set; } = true;
    public bool OverlayShowBadges { get; set; } = true;
    public bool OverlayShowEmotes { get; set; } = true;
    public int OverlayFadeOutSeconds { get; set; }
    public bool OverlayTextShadow { get; set; } = true;
    public bool OverlayTextOutline { get; set; } = true;
    public bool OverlayDarkBackground { get; set; } = true;
    public double OverlayBackgroundOpacity { get; set; }
    public string OverlayAlign { get; set; } = "left";
    public bool EnableChatLogging { get; set; } = true;
    public bool SaveChatLogTxt { get; set; } = true;
    public bool LogChatBadges { get; set; } = true;
    public bool LogChannelPointRedemptions { get; set; } = true;
    public string ChatLogsFolder { get; set; } = string.Empty;
    public int MaxLogViewerMessages { get; set; } = 3000;

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Twitch channel logins are canonically lowercase.")]
    public void Normalize()
    {
        Channel = NormalizeChannel(Channel);
        SavedChannels = (SavedChannels ?? [])
            .Select(NormalizeChannel)
            .Where(channel => channel.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        if (Channel.Length > 0 && !SavedChannels.Contains(Channel, StringComparer.OrdinalIgnoreCase))
        {
            SavedChannels.Insert(0, Channel);
            SavedChannels = SavedChannels.Take(3).ToList();
        }
        Theme = Theme is "Dark" or "Light" or "System" ? Theme : "Dark";
        Language = Language is "ru" or "en" ? Language : "ru";
        MessageLimit = Math.Clamp(MessageLimit, 250, 10_000);
        ViewerCountRefreshIntervalSeconds = Math.Clamp(
            ViewerCountRefreshIntervalSeconds,
            MinimumViewerCountRefreshIntervalSeconds,
            MaximumViewerCountRefreshIntervalSeconds);
        ChatFontSize = Math.Clamp(ChatFontSize, 11, 28);
        MessageVisualTheme = string.Equals(MessageVisualTheme, "TornBlack", StringComparison.OrdinalIgnoreCase)
            ? "TornBlack"
            : "Default";
        UiFontFamily = UiFontFamily switch
        {
            "Inter" or "SegoeUIVariable" or "SegoeUI" or "Aptos" or "Bahnschrift" or
                "Calibri" or "Candara" or "Trebuchet" => UiFontFamily,
            "System" => "SegoeUIVariable",
            _ => "SegoeUIVariable"
        };
        WindowControlsStyle = string.Equals(WindowControlsStyle, "Windows", StringComparison.OrdinalIgnoreCase)
            ? "Windows"
            : "Mac";
        ClientId = (ClientId ?? string.Empty).Trim();
        RedirectUri = (RedirectUri ?? string.Empty).Trim();
        if (RedirectUri.Length == 0)
        {
            RedirectUri = "http://localhost:17654/";
        }
        else if (!RedirectUri.EndsWith('/'))
        {
            RedirectUri += "/";
        }
        OverlayPort = Math.Clamp(OverlayPort, 1024, 65535);
        OverlayMaxMessages = Math.Clamp(OverlayMaxMessages, 1, 100);
        OverlayFontSize = Math.Clamp(OverlayFontSize, 10, 72);
        OverlayFadeOutSeconds = Math.Clamp(OverlayFadeOutSeconds, 0, 600);
        OverlayBackgroundOpacity = Math.Clamp(OverlayBackgroundOpacity, 0, 1);
        OverlayAlign = OverlayAlign is "center" or "right" ? OverlayAlign : "left";
        ChatLogsFolder = (ChatLogsFolder ?? string.Empty).Trim();
        MaxLogViewerMessages = Math.Clamp(MaxLogViewerMessages, 100, 50_000);
    }

    private static string NormalizeChannel(string? channel)
    {
        var normalized = (channel ?? string.Empty)
            .Trim()
            .TrimStart('@', '#')
            .ToLowerInvariant();
        return normalized.Length is > 0 and <= 25 && normalized.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '_')
            ? normalized
            : string.Empty;
    }
}
