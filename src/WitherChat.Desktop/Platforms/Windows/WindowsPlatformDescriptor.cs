namespace WitherChat.Desktop.Platforms.Windows;

public sealed class WindowsPlatformDescriptor : IPlatformDescriptor
{
    public string Id => "windows";
    public string DisplayName => "Windows";
    public IReadOnlyList<string> RuntimeIdentifiers { get; } = ["win-x64", "win-arm64"];
    public bool SupportsSystemTray => true;
}
