namespace TwitchChatMvp.Services;

public static class AppInfo
{
    public const string Name = "WitherChat";
    public static string Version { get; } = GetVersion();

    private static string GetVersion()
    {
        var version = typeof(AppInfo).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
