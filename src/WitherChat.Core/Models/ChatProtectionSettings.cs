namespace WitherChat.Core.Models;

public sealed record ChatProtectionSettings(
    bool SlowMode,
    int SlowModeWaitSeconds,
    bool SubscriberMode,
    bool FollowerMode,
    int FollowerModeDurationMinutes);
