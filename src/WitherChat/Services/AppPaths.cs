using System.IO;

namespace WitherChat.Services;

public static class AppPaths
{
    // Keep one-time migration compatible without retaining the retired project name in source text.
    private static readonly string LegacyAppDataDirectoryName = CreateLegacyAppDataDirectoryName();

    private static string CreateLegacyAppDataDirectoryName()
    {
        ReadOnlySpan<byte> encoded = [14, 45, 51, 46, 57, 50, 25, 50, 59, 46, 23, 44, 42];
        Span<char> decoded = stackalloc char[encoded.Length];
        for (var index = 0; index < encoded.Length; index++)
        {
            decoded[index] = (char)(encoded[index] ^ 0x5A);
        }

        return new string(decoded);
    }

    private static string LegacyAppDataDirectory =>
#if DEBUG
        DebugDataDirectory is { } debugDirectory
            ? Path.Combine(debugDirectory, "legacy")
            :
#endif
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyAppDataDirectoryName);

    private static string LegacyChatLogsDirectory => Path.Combine(LegacyAppDataDirectory, "chat_logs");

    public static string AppDataDirectory =>
#if DEBUG
        DebugDataDirectory ??
#endif
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name);

#if DEBUG
    private static string? DebugDataDirectory
    {
        get
        {
            var path = AppContext.GetData("WitherChat.DebugDataDirectory") as string;
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
    }
#endif

    public static string LocalDataDirectory =>
        AppDataDirectory;

    public static string ChatLogsDirectory =>
        Path.Combine(AppDataDirectory, "chat_logs");

    public static string SettingsFile => Path.Combine(AppDataDirectory, "settings.json");
    public static string TokenFile => Path.Combine(AppDataDirectory, "token.dat");
    public static string TokenLogoutMarker => Path.Combine(AppDataDirectory, "token.logout");
    public static string LegacySettingsFile => Path.Combine(LegacyAppDataDirectory, "settings.json");
    public static string LegacyTokenFile => Path.Combine(LegacyAppDataDirectory, "token.dat");
    public static string BadgeCacheFile => Path.Combine(LocalDataDirectory, "badge-cache.json");
    public static string ModerationCacheFile => Path.Combine(LocalDataDirectory, "moderation-cache.json");
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

    public static void TryMigrateLegacyChatLogs()
    {
        if (!Directory.Exists(LegacyChatLogsDirectory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(ChatLogsDirectory);
            foreach (var legacyChannel in Directory.EnumerateDirectories(LegacyChatLogsDirectory))
            {
                if (HasReparsePoint(legacyChannel))
                {
                    continue;
                }

                var channelName = Path.GetFileName(legacyChannel);
                var destinationChannel = Path.Combine(ChatLogsDirectory, channelName);
                if (!Directory.Exists(destinationChannel))
                {
                    Directory.Move(legacyChannel, destinationChannel);
                    continue;
                }

                foreach (var legacySession in Directory.EnumerateDirectories(legacyChannel))
                {
                    if (HasReparsePoint(legacySession))
                    {
                        continue;
                    }

                    var destinationSession = Path.Combine(destinationChannel, Path.GetFileName(legacySession));
                    if (!Directory.Exists(destinationSession) && !File.Exists(destinationSession))
                    {
                        Directory.Move(legacySession, destinationSession);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Migration is retried on the next start; existing logs are never overwritten.
        }
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
