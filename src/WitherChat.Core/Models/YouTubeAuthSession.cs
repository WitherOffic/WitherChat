namespace WitherChat.Core.Models;

public sealed record YouTubeAuthSession(
    string AccessToken,
    string RefreshToken,
    string ClientId,
    string ChannelId,
    string ChannelTitle,
    string ChannelHandle,
    Uri? ThumbnailUri,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAtUtc)
{
    public bool NeedsRefresh(TimeSpan safetyWindow) => DateTimeOffset.UtcNow + safetyWindow >= ExpiresAtUtc;

    public override string ToString() =>
        $"YouTubeAuthSession {{ ClientId = {ClientId}, ChannelId = {ChannelId}, ChannelTitle = {ChannelTitle}, " +
        $"ChannelHandle = {ChannelHandle}, Scopes = {Scopes.Count}, ExpiresAtUtc = {ExpiresAtUtc:O}, " +
        "AccessToken = [REDACTED], RefreshToken = [REDACTED] }";
}
