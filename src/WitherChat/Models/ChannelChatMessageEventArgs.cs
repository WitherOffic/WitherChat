namespace WitherChat.Models;

public sealed class ChannelChatMessageEventArgs(string channelLogin, ChatMessageModel message) : EventArgs
{
    public string ChannelLogin { get; } = channelLogin;
    public ChatMessageModel Message { get; } = message;
}
