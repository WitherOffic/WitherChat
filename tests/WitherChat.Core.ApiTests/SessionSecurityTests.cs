using WitherChat.Core.Models;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class SessionSecurityTests
{
    [Fact]
    public void SessionDiagnosticsNeverExposeOAuthTokens()
    {
        var expires = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
        var twitch = new TwitchAuthSession(
            "twitch-access-secret",
            "twitch-refresh-secret",
            "client",
            "user",
            "login",
            ["chat:read"],
            expires,
            expires);
        var youtube = new YouTubeAuthSession(
            "youtube-access-secret",
            "youtube-refresh-secret",
            "client",
            "channel",
            "Channel",
            "@channel",
            null,
            ["youtube.readonly"],
            expires);
        var donationAlerts = new DonationAlertsAuthSession(
            "donation-access-secret",
            "client",
            42,
            "user-code",
            "Streamer",
            null,
            ["oauth-user-show"],
            expires);

        AssertRedacted(twitch.ToString(), "twitch-access-secret", "twitch-refresh-secret");
        AssertRedacted(youtube.ToString(), "youtube-access-secret", "youtube-refresh-secret");
        AssertRedacted(donationAlerts.ToString(), "donation-access-secret");
    }

    private static void AssertRedacted(string diagnostic, params string[] secrets)
    {
        Assert.Contains("[REDACTED]", diagnostic, StringComparison.Ordinal);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, diagnostic, StringComparison.Ordinal);
        }
    }
}
