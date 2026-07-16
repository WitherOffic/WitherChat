using System.Diagnostics;

namespace WitherChat.Services;

public static class DonationService
{
    public const string BoostyUrl = "https://boosty.to/wither101";
    public const string DonationAlertsUrl = "https://www.donationalerts.com/r/wither_101";
    public const string TwitchUrl = "https://www.twitch.tv/wither_101";
    public const string YouTubeUrl = "https://www.youtube.com/witheres";
    public const string SteamUrl = "https://steamcommunity.com/id/WitherOffic/";
    public const string TelegramUrl = "https://t.me/WitherOffic";
    public const string UsdtTrc20Address = "THXKG6WaoTRaKEUm1itGQHywCqNhpfUsnz";
    public const string UsdtTonAddress = "UQC7ap0mFUP8KfGjDUGvpgIcy8FqyKPTdI0zoj5PZMYawdXC";

    public static void Open(string target)
    {
        var url = target switch
        {
            "Boosty" => BoostyUrl,
            "DonationAlerts" => DonationAlertsUrl,
            "Twitch" => TwitchUrl,
            "YouTube" => YouTubeUrl,
            "Steam" => SteamUrl,
            "Telegram" => TelegramUrl,
            _ => throw new InvalidOperationException("Unknown support target.")
        };

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static string GetAddress(string network) => network switch
    {
        "USDT_TRC20" => UsdtTrc20Address,
        "USDT_TON" => UsdtTonAddress,
        _ => throw new InvalidOperationException("Unknown crypto network.")
    };
}
