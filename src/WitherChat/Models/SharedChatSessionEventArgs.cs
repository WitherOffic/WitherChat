namespace WitherChat.Models;

public sealed class SharedChatSessionEventArgs(
    string broadcasterId,
    string sessionId,
    string hostBroadcasterId,
    string hostBroadcasterLogin,
    string hostBroadcasterName,
    IReadOnlyList<SharedChatParticipant> participants,
    bool isActive) : EventArgs
{
    public string BroadcasterId { get; } = broadcasterId;
    public string SessionId { get; } = sessionId;
    public string HostBroadcasterId { get; } = hostBroadcasterId;
    public string HostBroadcasterLogin { get; } = hostBroadcasterLogin;
    public string HostBroadcasterName { get; } = hostBroadcasterName;
    public IReadOnlyList<SharedChatParticipant> Participants { get; } = participants;
    public bool IsActive { get; } = isActive;
}

public sealed record SharedChatParticipant(string BroadcasterId, string Login, string DisplayName);

public sealed record SharedChatSessionInfo(
    string SessionId,
    string HostBroadcasterId,
    IReadOnlyList<string> ParticipantBroadcasterIds);
