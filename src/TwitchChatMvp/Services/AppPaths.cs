using System.IO;

namespace TwitchChatMvp.Services;

public static class AppPaths
{
    private static string LegacyAppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TwitchChatMvp");

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name);

    public static string LocalDataDirectory =>
        AppDataDirectory;

    public static string ChatLogsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, "chat_logs");

    public static string SettingsFile => Path.Combine(AppDataDirectory, "settings.json");
    public static string TokenFile => Path.Combine(AppDataDirectory, "token.dat");
    public static string LegacySettingsFile => Path.Combine(LegacyAppDataDirectory, "settings.json");
    public static string LegacyTokenFile => Path.Combine(LegacyAppDataDirectory, "token.dat");
    public static string BadgeCacheFile => Path.Combine(LocalDataDirectory, "badge-cache.json");
    public static string LogDirectory => Path.Combine(LocalDataDirectory, "logs");
    public static string LogFile => Path.Combine(LogDirectory, "app.log");

    public static void TryMigrateLegacyFile(string legacyPath, string destinationPath)
    {
        if (File.Exists(destinationPath) || !File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(legacyPath, destinationPath, overwrite: false);
        }
        catch
        {
            // Migration is best-effort; never damage the legacy user data.
        }
    }
}
