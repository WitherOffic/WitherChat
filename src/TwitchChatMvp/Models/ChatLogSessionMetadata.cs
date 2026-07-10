namespace TwitchChatMvp.Models;

public sealed class ChatLogSessionMetadata
{
    public string ChannelLogin { get; set; } = string.Empty;
    public string ChannelDisplayName { get; set; } = string.Empty;
    public string BroadcasterId { get; set; } = string.Empty;
    public string StreamTitle { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public DateTimeOffset? StreamStartedAtUtc { get; set; }
    public DateTimeOffset LogStartedAtLocal { get; set; }
    public DateTimeOffset LogStartedAtUtc { get; set; }
    public bool IsLive { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public long MessageCount { get; set; }
}
