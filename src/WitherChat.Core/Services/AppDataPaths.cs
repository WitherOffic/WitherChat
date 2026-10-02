namespace WitherChat.Core.Services;

public sealed class AppDataPaths
{
    public AppDataPaths(string? configDirectory = null)
    {
        ConfigDirectory = string.IsNullOrWhiteSpace(configDirectory)
            ? ResolveConfigDirectory()
            : Path.GetFullPath(configDirectory);
        LogDirectory = Path.Combine(ConfigDirectory, "chat_logs");
        SettingsFile = Path.Combine(ConfigDirectory, "settings.json");
        TokenFile = Path.Combine(ConfigDirectory, "twitch-session.dat");
        YouTubeTokenFile = Path.Combine(ConfigDirectory, "youtube-session.dat");
        DonationAlertsTokenFile = Path.Combine(ConfigDirectory, "donationalerts-session.dat");
        DonationAlertsWidgetTokenFile = Path.Combine(ConfigDirectory, "donationalerts-widget.dat");
        SecretKeyFile = Path.Combine(ConfigDirectory, "secret.key");
        ModerationCacheFile = Path.Combine(ConfigDirectory, "moderation-cache.json");
        MomentsFile = Path.Combine(ConfigDirectory, "stream-moments.json");
        TokenLogoutMarker = Path.Combine(ConfigDirectory, "twitch-session.logged-out");
        YouTubeTokenLogoutMarker = Path.Combine(ConfigDirectory, "youtube-session.logged-out");
        DonationAlertsTokenLogoutMarker = Path.Combine(ConfigDirectory, "donationalerts-session.logged-out");
        var legacyDirectory = Directory.GetParent(ConfigDirectory)?.FullName ?? ConfigDirectory;
        LegacySettingsFile = Path.Combine(legacyDirectory, "settings.json");
        LegacyTokenFile = Path.Combine(legacyDirectory, "token.dat");
        LegacyModerationCacheFile = Path.Combine(legacyDirectory, "moderation-cache.json");
        LegacyTokenLogoutMarker = Path.Combine(legacyDirectory, "token.logout");
        LegacyChatLogsDirectory = Path.Combine(legacyDirectory, "chat_logs");
    }

    public string ConfigDirectory { get; }
    public string LogDirectory { get; }
    public string SettingsFile { get; }
    public string TokenFile { get; }
    public string YouTubeTokenFile { get; }
    public string DonationAlertsTokenFile { get; }
    public string DonationAlertsWidgetTokenFile { get; }
    public string SecretKeyFile { get; }
    public string ModerationCacheFile { get; }
    public string MomentsFile { get; }
    public string TokenLogoutMarker { get; }
    public string YouTubeTokenLogoutMarker { get; }
    public string DonationAlertsTokenLogoutMarker { get; }
    public string LegacySettingsFile { get; }
    public string LegacyTokenFile { get; }
    public string LegacyModerationCacheFile { get; }
    public string LegacyTokenLogoutMarker { get; }
    public string LegacyChatLogsDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
        if (!OperatingSystem.IsWindows())
        {
            var privateDirectoryMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(ConfigDirectory, privateDirectoryMode);
            File.SetUnixFileMode(LogDirectory, privateDirectoryMode);
        }
    }

    private static string ResolveConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppVersion.ProductName,
                AppVersion.DataProfileName);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppVersion.ProductName,
                AppVersion.DataProfileName);
        }

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = string.IsNullOrWhiteSpace(xdgConfig)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdgConfig;
        return Path.Combine(root, AppVersion.ProductName, AppVersion.DataProfileName);
    }

}
