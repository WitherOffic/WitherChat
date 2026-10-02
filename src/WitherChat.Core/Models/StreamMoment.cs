namespace WitherChat.Core.Models;

public sealed record StreamMoment(
    string Id,
    string Platform,
    string Channel,
    string MessageId,
    string User,
    string Text,
    string Note,
    DateTimeOffset MessageTimestamp,
    DateTimeOffset SavedAtUtc,
    string EventKind = "");
