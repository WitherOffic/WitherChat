namespace WitherChat.Models;

public sealed class SendChatMessageResult
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");
    public bool IsSent { get; init; }
    public string? DropCode { get; init; }
    public string? DropMessage { get; init; }
}
