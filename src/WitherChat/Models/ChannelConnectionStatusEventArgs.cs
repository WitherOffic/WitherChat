namespace WitherChat.Models;

public enum ChannelConnectionState
{
    Connecting,
    Reconnecting,
    Connected,
    Disconnected,
    Error
}

public sealed class ChannelConnectionStatusEventArgs(
    string channelLogin,
    ChannelConnectionState state,
    string error = "") : EventArgs
{
    public string ChannelLogin { get; } = channelLogin;
    public ChannelConnectionState State { get; } = state;
    public string Error { get; } = error;
}

public sealed class EventSubConnectionStatusEventArgs(
    ChannelConnectionState state,
    string error = "",
    string errorCode = "") : EventArgs
{
    public ChannelConnectionState State { get; } = state;
    public string Error { get; } = error;
    public string ErrorCode { get; } = errorCode;
}

public sealed class ChannelIdentityResolvedEventArgs(string channelLogin, string broadcasterId) : EventArgs
{
    public string ChannelLogin { get; } = channelLogin;
    public string BroadcasterId { get; } = broadcasterId;
}
