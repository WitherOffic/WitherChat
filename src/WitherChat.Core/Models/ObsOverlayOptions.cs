namespace WitherChat.Core.Models;

public sealed record ObsOverlayOptions(
    int Port,
    int MaximumMessages,
    double FontSize,
    bool ShowTimestamps,
    bool ShowBadges,
    bool ShowEmotes,
    int FadeOutSeconds,
    bool TextShadow,
    bool TextOutline,
    bool DarkBackground,
    double BackgroundOpacity,
    string Alignment,
    string MessageTheme);
