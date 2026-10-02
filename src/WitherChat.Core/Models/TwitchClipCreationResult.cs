namespace WitherChat.Core.Models;

public sealed record TwitchClipCreationResult(
    string Id,
    Uri EditUri,
    Uri ShareUri);
