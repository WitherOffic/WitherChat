namespace WitherChat.Core.Models;

public sealed record SendChatMessageResult(
    bool IsSent,
    string MessageId,
    string DropReasonCode,
    string DropReasonMessage);
