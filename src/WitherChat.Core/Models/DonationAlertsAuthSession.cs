namespace WitherChat.Core.Models;

public sealed record DonationAlertsAuthSession(
    string AccessToken,
    string ClientId,
    long UserId,
    string UserCode,
    string DisplayName,
    Uri? AvatarUri,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() =>
        $"DonationAlertsAuthSession {{ ClientId = {ClientId}, UserId = {UserId}, UserCode = {UserCode}, " +
        $"DisplayName = {DisplayName}, Scopes = {Scopes.Count}, ExpiresAtUtc = {ExpiresAtUtc:O}, " +
        "AccessToken = [REDACTED] }";
}
