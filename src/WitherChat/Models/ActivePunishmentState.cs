namespace WitherChat.Models;

public enum PunishmentType
{
    Unknown = 0,
    Timeout = 1,
    Ban = 2
}

public enum PunishmentSource
{
    LocalAction = 0,
    IrcClearChat = 1,
    EventSub = 2
}

public sealed class ActivePunishmentState
{
    public string UserId { get; init; } = string.Empty;
    public string UserLogin { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public PunishmentType Type { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public int? DurationSeconds { get; set; }
    public string ModeratorId { get; set; } = string.Empty;
    public string ModeratorName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public PunishmentSource Source { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
}
