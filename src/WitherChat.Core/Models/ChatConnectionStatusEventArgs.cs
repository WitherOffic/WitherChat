namespace WitherChat.Core.Models;

public sealed class ChatConnectionStatusEventArgs(
    ChatConnectionState state,
    string channel,
    string? detail = null) : EventArgs
{
    public ChatConnectionState State { get; } = state;
    public string Channel { get; } = channel;
    public string? Detail { get; } = detail;
}
