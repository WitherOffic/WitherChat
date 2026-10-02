namespace WitherChat.Core.Models;

public sealed record TwitchUserProfile(
    string Id,
    string Login,
    string DisplayName,
    Uri? ProfileImageUri);
