namespace TwitchChatMvp.Models;

public sealed class TwitchUser
{
    public string Id { get; init; } = string.Empty;
    public string Login { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ProfileImageUrl { get; init; } = string.Empty;

    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? Login : DisplayName;
}
