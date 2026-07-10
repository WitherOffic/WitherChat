namespace TwitchChatMvp.Models;

public sealed class ModerationRequest
{
    public string Reason { get; init; } = string.Empty;
    public int? DurationSeconds { get; init; }
}
