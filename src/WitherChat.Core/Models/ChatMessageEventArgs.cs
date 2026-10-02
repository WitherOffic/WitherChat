namespace WitherChat.Core.Models;

public sealed class ChatMessageEventArgs(ChatMessage message) : EventArgs
{
    public ChatMessage Message { get; } = message ?? throw new ArgumentNullException(nameof(message));
}
