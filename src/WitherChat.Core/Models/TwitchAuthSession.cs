namespace WitherChat.Core.Models;

public sealed record TwitchAuthSession(
    string AccessToken,
    string RefreshToken,
    string ClientId,
    string UserId,
    string Login,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset ValidatedAtUtc)
{
    public bool NeedsRefresh(TimeSpan safetyWindow) => DateTimeOffset.UtcNow + safetyWindow >= ExpiresAtUtc;

    public override string ToString() =>
        $"TwitchAuthSession {{ ClientId = {ClientId}, UserId = {UserId}, Login = {Login}, " +
        $"Scopes = {Scopes.Count}, ExpiresAtUtc = {ExpiresAtUtc:O}, ValidatedAtUtc = {ValidatedAtUtc:O}, " +
        "AccessToken = [REDACTED], RefreshToken = [REDACTED] }";
}
