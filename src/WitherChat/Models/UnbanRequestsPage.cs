namespace WitherChat.Models;

public sealed record UnbanRequestsPage(
    IReadOnlyList<UnbanRequestEntry> Requests,
    string Cursor);
