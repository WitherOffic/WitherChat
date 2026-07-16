namespace WitherChat.Models;

public sealed record BannedUsersPage(
    IReadOnlyList<BannedUserEntry> Users,
    string Cursor);
