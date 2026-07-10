using System.Diagnostics;

namespace TwitchChatMvp.Services;

public static class DonationService
{
    public const string UsdtTrc20Address = "THXKG6WaoTRaKEUm1itGQHywCqNhpfUsnz";
    public const string UsdtTonAddress = "UQC7ap0mFUP8KfGjDUGvpgIcy8FqyKPTdI0zoj5PZMYawdXC";

    public static void Open(string target)
    {
        var url = target switch
        {
            "Boosty" => "https://boosty.to/wither101",
            "DonationAlerts" => "https://www.donationalerts.com/r/wither_101",
            "Twitch" => "https://www.twitch.tv/wither_101",
            "YouTube" => "https://www.youtube.com/witheres",
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
