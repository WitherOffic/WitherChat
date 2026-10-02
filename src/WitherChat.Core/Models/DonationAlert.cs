namespace WitherChat.Core.Models;

public sealed record DonationAlert(
    string Id,
    string Username,
    string Message,
    decimal Amount,
    string Currency,
    DateTimeOffset ReceivedAtUtc,
    bool IsShown = false,
    DateTimeOffset? ShownAtUtc = null,
    string MessageType = "text");
