namespace WitherChat.Desktop.Platforms;

public interface IPlatformDescriptor
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<string> RuntimeIdentifiers { get; }
    bool SupportsSystemTray { get; }
}
