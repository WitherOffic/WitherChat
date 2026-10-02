namespace WitherChat.Core.Models;

public enum UnbanRequestStatus
{
    Pending,
    Approved,
    Denied,
    Acknowledged,
    Canceled
}

public sealed record UnbanRequest(
    string RequestId,
    string BroadcasterId,
    string UserId,
    string UserLogin,
    string DisplayName,
    string RequestText,
    DateTimeOffset CreatedAt,
    UnbanRequestStatus Status,
    DateTimeOffset? ResolvedAt,
    string ResolutionText);

public sealed record UnbanRequestsPage(IReadOnlyList<UnbanRequest> Requests, string Cursor);
