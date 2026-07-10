using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public static class AppTwitchDefaults
{
    // Release builders: replace this with the public Client ID of your Twitch Developer Application.
    // Do not put a Client Secret in this desktop app.
    public const string ClientId = "f1v3cswx6e7w42gibf68m0ca8903g0";
    public const string RedirectUri = "http://localhost:17654/";

    public static string GetClientId(AppSettings settings)
    {
        var customClientId = settings.UseCustomClientId ? settings.ClientId.Trim() : string.Empty;
        return string.IsNullOrWhiteSpace(customClientId) ? ClientId : customClientId;
    }

    public static bool IsClientIdConfigured(AppSettings settings)
    {
        var clientId = GetClientId(settings);
        return !string.IsNullOrWhiteSpace(clientId) &&
               !string.Equals(clientId, "PASTE_YOUR_TWITCH_CLIENT_ID_HERE", StringComparison.Ordinal);
    }
}
