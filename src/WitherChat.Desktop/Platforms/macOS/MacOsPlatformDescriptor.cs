namespace WitherChat.Desktop.Platforms.macOS;

public sealed class MacOsPlatformDescriptor : IPlatformDescriptor
{
    public string Id => "macos";
    public string DisplayName => "macOS";
    public IReadOnlyList<string> RuntimeIdentifiers { get; } = ["osx-x64", "osx-arm64"];
    // Avalonia's mutable native tray menu is not stable on macOS when the
    // channel collection changes. macOS already keeps the application in the
    // Dock, so prefer the in-app channel switcher and avoid a native crash.
    public bool SupportsSystemTray => false;
}
