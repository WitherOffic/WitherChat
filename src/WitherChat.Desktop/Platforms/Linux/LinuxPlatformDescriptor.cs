namespace WitherChat.Desktop.Platforms.Linux;

public sealed class LinuxPlatformDescriptor : IPlatformDescriptor
{
    public string Id => "linux";
    public string DisplayName => "Linux";
    public IReadOnlyList<string> RuntimeIdentifiers { get; } = ["linux-x64", "linux-arm64"];
    public bool SupportsSystemTray => true;
}
