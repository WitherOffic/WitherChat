namespace WitherChat.Models;

public sealed class PinnedChatMessageModel
{
    public string MessageId { get; init; } = string.Empty;
    public string BroadcasterId { get; init; } = string.Empty;
    public string SenderUserId { get; init; } = string.Empty;
    public string SenderUserLogin { get; init; } = string.Empty;
    public string SenderDisplayName { get; init; } = string.Empty;
    public string PinnedByUserId { get; init; } = string.Empty;
    public string PinnedByUserLogin { get; init; } = string.Empty;
    public string PinnedByDisplayName { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset? EndsAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public string SenderLabel
    {
        get
        {
            var value = string.IsNullOrWhiteSpace(SenderDisplayName) ? SenderUserLogin : SenderDisplayName;
            return string.IsNullOrWhiteSpace(value) ? string.Empty : "@" + value.TrimStart('@');
        }
    }

    public string PinnedByLabel
    {
        get
        {
            var value = string.IsNullOrWhiteSpace(PinnedByDisplayName) ? PinnedByUserLogin : PinnedByDisplayName;
            return string.IsNullOrWhiteSpace(value) ? string.Empty : "@" + value.TrimStart('@');
        }
    }
}
