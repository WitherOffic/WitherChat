namespace WitherChat.Core.Models;

public sealed record BannedUser(
    string UserId,
    string UserLogin,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    string Reason);

public sealed record BannedUsersPage(IReadOnlyList<BannedUser> Users, string Cursor);
