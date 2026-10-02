namespace WitherChat.Core.Models;

public enum ChatConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error
}
