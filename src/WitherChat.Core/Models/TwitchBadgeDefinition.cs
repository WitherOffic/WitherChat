namespace WitherChat.Core.Models;

public sealed record TwitchBadgeDefinition(
    string SetId,
    string VersionId,
    Uri? ImageUri,
    string Title)
{
    public string Key => $"{SetId}/{VersionId}";
}
